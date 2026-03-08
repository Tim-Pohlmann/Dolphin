using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Dolphin.Scanner;

namespace Dolphin.Lsp;

/// <summary>
/// Minimal Language Server for Opengrep rule files.
///
/// On every open/change of a file inside a .dolphin/ directory, runs
/// `opengrep validate` and publishes LSP diagnostics. All JSON is written
/// with Utf8JsonWriter so this is fully trim-safe (no reflection).
/// </summary>
public static class LspServer
{
    private static string? _opengrepBinary;
    private static bool _shutdownReceived;

    // Guards concurrent writes to stdout (validation runs off the message loop).
    private static readonly SemaphoreSlim _stdoutLock = new(1, 1);

    // Per-URI cancellation: cancels superseded validations on rapid edits.
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> _validationCts = new();

    public static async Task RunAsync()
    {
        // Best-effort early resolution; if it fails we retry on first validate.
        try { _opengrepBinary = await Installer.EnsureInstalledAsync(); }
        catch { /* will retry lazily */ }

        var reader = new LspReader(Console.OpenStandardInput());
        var stdout = Console.OpenStandardOutput();

        while (true)
        {
            var header = await reader.ReadHeaderAsync();
            if (header is null) break; // stdin closed

            // Case-insensitive per the LSP spec (HTTP-style headers).
            var clMatch = Regex.Match(header, @"Content-Length:\s*(\d+)",
                RegexOptions.IgnoreCase);
            if (!clMatch.Success || !int.TryParse(clMatch.Groups[1].Value, out var length))
                continue;

            var body = await reader.ReadBodyAsync(length);

            try
            {
                using var doc = JsonDocument.Parse(body);
                await HandleMessageAsync(doc.RootElement, stdout);
            }
            catch { /* never crash the LSP server process */ }
        }
    }

    // ── Message dispatch ──────────────────────────────────────────────────────

    private static async Task HandleMessageAsync(JsonElement msg, Stream stdout)
    {
        if (!msg.TryGetProperty("method", out var methodEl)) return;
        var method = methodEl.GetString();

        msg.TryGetProperty("id", out var id);
        msg.TryGetProperty("params", out var p);

        switch (method)
        {
            case "initialize":
                await SendAsync(stdout, w =>
                {
                    w.WriteStartObject();
                    w.WriteString("jsonrpc", "2.0");
                    WriteId(w, id);
                    w.WritePropertyName("result");
                    w.WriteStartObject();
                    w.WritePropertyName("capabilities");
                    w.WriteStartObject();
                    w.WriteNumber("textDocumentSync", 1); // full sync
                    w.WriteEndObject();
                    w.WriteEndObject();
                    w.WriteEndObject();
                });
                break;

            case "textDocument/didOpen":
            {
                var td = p.GetProperty("textDocument");
                var uri = td.GetProperty("uri").GetString() ?? "";
                var text = td.GetProperty("text").GetString() ?? "";
                _ = ValidateAndPublishAsync(stdout, uri, text, CancelPrevious(uri));
                break;
            }

            case "textDocument/didChange":
            {
                var uri = p.GetProperty("textDocument").GetProperty("uri").GetString() ?? "";
                var changes = p.GetProperty("contentChanges");
                if (changes.GetArrayLength() > 0)
                    _ = ValidateAndPublishAsync(stdout, uri,
                        changes[0].GetProperty("text").GetString() ?? "",
                        CancelPrevious(uri));
                break;
            }

            case "textDocument/didClose":
                await PublishDiagnosticsAsync(stdout,
                    p.GetProperty("textDocument").GetProperty("uri").GetString() ?? "", []);
                break;

            case "shutdown":
                _shutdownReceived = true;
                await SendAsync(stdout, w =>
                {
                    w.WriteStartObject();
                    w.WriteString("jsonrpc", "2.0");
                    WriteId(w, id);
                    w.WriteNull("result");
                    w.WriteEndObject();
                });
                break;

            case "exit":
                // LSP spec: exit 0 if preceded by shutdown, 1 otherwise.
                Environment.Exit(_shutdownReceived ? 0 : 1);
                break;
        }
    }

    // ── Validation ────────────────────────────────────────────────────────────

    private static bool IsDolphinRulesFile(string uri) =>
        uri.Contains("/.dolphin/") || uri.Contains("\\.dolphin\\");

    /// <summary>
    /// Cancels any in-flight validation for <paramref name="uri"/> and returns a fresh token.
    /// </summary>
    private static CancellationToken CancelPrevious(string uri)
    {
        var cts = new CancellationTokenSource();
        var old = _validationCts.AddOrUpdate(uri, cts, (_, prev) => { prev.Cancel(); return cts; });
        return cts.Token;
    }

    private static async Task ValidateAndPublishAsync(
        Stream stdout, string uri, string text, CancellationToken ct)
    {
        try
        {
            if (!IsDolphinRulesFile(uri))
                return; // not a Dolphin rules file — ignore

            // Lazy retry: attempt resolution if startup failed.
            if (_opengrepBinary is null)
            {
                try { _opengrepBinary = await Installer.EnsureInstalledAsync(); }
                catch { return; }
            }

            var diagnostics = await RunValidateAsync(text, ct);
            ct.ThrowIfCancellationRequested(); // don't publish stale results
            await PublishDiagnosticsAsync(stdout, uri, diagnostics);
        }
        catch (OperationCanceledException) { /* superseded by a newer edit */ }
        catch { /* swallow — must not propagate from fire-and-forget */ }
    }

    private static async Task<LspDiagnostic[]> RunValidateAsync(string text, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"dolphin-lsp-{Guid.NewGuid():N}.yaml");
        try
        {
            await File.WriteAllTextAsync(tmp, text, Encoding.UTF8, ct);

            var psi = new ProcessStartInfo(_opengrepBinary!)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("validate");
            psi.ArgumentList.Add("--config");
            psi.ArgumentList.Add(tmp);

            using var proc = Process.Start(psi)!;
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            return proc.ExitCode == 0
                ? []
                : LspDiagnosticsParser.Parse(stdout + stderr);
        }
        catch
        {
            return [];
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    // ── Wire protocol helpers ─────────────────────────────────────────────────

    private static async Task SendAsync(Stream stdout, Action<Utf8JsonWriter> write)
    {
        var buf = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buf))
            write(w);

        var header = $"Content-Length: {buf.WrittenCount}\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);

        await _stdoutLock.WaitAsync();
        try
        {
            await stdout.WriteAsync(headerBytes);
            await stdout.WriteAsync(buf.WrittenMemory);
            await stdout.FlushAsync();
        }
        finally
        {
            _stdoutLock.Release();
        }
    }

    private static void WriteId(Utf8JsonWriter w, JsonElement id)
    {
        w.WritePropertyName("id");
        if (id.ValueKind == JsonValueKind.Undefined)
            w.WriteNullValue();
        else
            id.WriteTo(w);
    }

    private static async Task PublishDiagnosticsAsync(
        Stream stdout, string uri, LspDiagnostic[] diagnostics)
    {
        await SendAsync(stdout, w =>
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WriteString("method", "textDocument/publishDiagnostics");
            w.WritePropertyName("params");
            w.WriteStartObject();
            w.WriteString("uri", uri);
            w.WritePropertyName("diagnostics");
            w.WriteStartArray();
            foreach (var d in diagnostics)
            {
                w.WriteStartObject();
                w.WritePropertyName("range");
                w.WriteStartObject();
                w.WritePropertyName("start");
                WritePosition(w, d.Range.Start);
                w.WritePropertyName("end");
                WritePosition(w, d.Range.End);
                w.WriteEndObject();
                w.WriteNumber("severity", d.Severity);
                w.WriteString("source", d.Source);
                w.WriteString("message", d.Message);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
        });
    }

    private static void WritePosition(Utf8JsonWriter w, LspPosition pos)
    {
        w.WriteStartObject();
        w.WriteNumber("line", pos.Line);
        w.WriteNumber("character", pos.Character == int.MaxValue ? 9999 : pos.Character);
        w.WriteEndObject();
    }
}

// ── Models ────────────────────────────────────────────────────────────────────

internal record LspDiagnostic(
    LspRange Range,
    int Severity,
    string Source,
    string Message,
    bool Pending);

internal record LspRange(LspPosition Start, LspPosition End);

internal record LspPosition(int Line, int Character);

// ── Buffered stdin reader ─────────────────────────────────────────────────────

internal sealed class LspReader(Stream stream)
{
    private readonly byte[] _buf = new byte[65536];
    private int _start, _end;

    private async Task<int> ReadByteAsync()
    {
        if (_start == _end)
        {
            _start = 0;
            _end = await stream.ReadAsync(_buf);
            if (_end == 0) return -1;
        }
        return _buf[_start++];
    }

    /// <summary>Reads bytes until \r\n\r\n is found; returns the header as ASCII.</summary>
    public async Task<string?> ReadHeaderAsync()
    {
        var header = new List<byte>(256);
        byte b0 = 0, b1 = 0, b2 = 0;

        while (true)
        {
            var b = await ReadByteAsync();
            if (b < 0) return null;

            header.Add((byte)b);

            // Check if last four bytes are \r \n \r \n
            if (b == '\n' && b2 == '\r' && b1 == '\n' && b0 == '\r')
                break;

            b0 = b1; b1 = b2; b2 = (byte)b;
        }

        return Encoding.ASCII.GetString([.. header]);
    }

    /// <summary>Reads exactly <paramref name="length"/> bytes.</summary>
    public async Task<byte[]> ReadBodyAsync(int length)
    {
        var body = new byte[length];
        int offset = 0;

        // Drain what's already in our internal buffer first.
        int fromBuf = Math.Min(length, _end - _start);
        if (fromBuf > 0)
        {
            Array.Copy(_buf, _start, body, 0, fromBuf);
            _start += fromBuf;
            offset += fromBuf;
        }

        // Read the remainder directly from the stream.
        while (offset < length)
        {
            var read = await stream.ReadAsync(body.AsMemory(offset, length - offset));
            if (read == 0) throw new EndOfStreamException("LSP stdin closed mid-message.");
            offset += read;
        }

        return body;
    }
}
