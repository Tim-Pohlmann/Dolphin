using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    public static async Task RunAsync()
    {
        // Resolve the opengrep binary up-front, but don't fail if it isn't
        // ready yet — we'll skip validation silently until it is.
        try { _opengrepBinary = await Installer.EnsureInstalledAsync(); }
        catch { /* will remain null; diagnostics skipped */ }

        var reader = new LspReader(Console.OpenStandardInput());
        var stdout = Console.OpenStandardOutput();

        while (true)
        {
            var header = await reader.ReadHeaderAsync();
            if (header is null) break; // stdin closed

            var clMatch = Regex.Match(header, @"Content-Length:\s*(\d+)");
            if (!clMatch.Success) continue;

            var body = await reader.ReadBodyAsync(int.Parse(clMatch.Groups[1].Value));

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
                await ValidateAndPublishAsync(stdout, uri, text);
                break;
            }

            case "textDocument/didChange":
            {
                var uri = p.GetProperty("textDocument").GetProperty("uri").GetString() ?? "";
                var changes = p.GetProperty("contentChanges");
                if (changes.GetArrayLength() > 0)
                    await ValidateAndPublishAsync(stdout, uri,
                        changes[0].GetProperty("text").GetString() ?? "");
                break;
            }

            case "textDocument/didClose":
                await PublishDiagnosticsAsync(stdout,
                    p.GetProperty("textDocument").GetProperty("uri").GetString() ?? "", []);
                break;

            case "shutdown":
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
                Environment.Exit(0);
                break;
        }
    }

    // ── Validation ────────────────────────────────────────────────────────────

    private static bool IsDolphinRulesFile(string uri) =>
        uri.Contains("/.dolphin/") || uri.Contains("\\.dolphin\\");

    private static async Task ValidateAndPublishAsync(Stream stdout, string uri, string text)
    {
        if (!IsDolphinRulesFile(uri))
        {
            await PublishDiagnosticsAsync(stdout, uri, []);
            return;
        }

        if (_opengrepBinary is null)
            return; // binary not ready; skip silently

        var diagnostics = await RunValidateAsync(text);
        await PublishDiagnosticsAsync(stdout, uri, diagnostics);
    }

    private static async Task<LspDiagnostic[]> RunValidateAsync(string text)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"dolphin-lsp-{Environment.ProcessId}.yaml");
        try
        {
            await File.WriteAllTextAsync(tmp, text, Encoding.UTF8);

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
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            return proc.ExitCode == 0
                ? []
                : ParseDiagnostics(stdout + stderr);
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

    /// <summary>
    /// Parses `opengrep validate` output (inherited from Semgrep) into LSP diagnostics.
    ///
    /// Opengrep emits errors like:
    ///   Invalid rule 'no-console-log': missing required field 'message'
    ///     --> /tmp/dolphin-lsp-1234.yaml:8:5
    /// </summary>
    private static LspDiagnostic[] ParseDiagnostics(string output)
    {
        var diagnostics = new List<LspDiagnostic>();
        var lines = output.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var trimmed = raw.Trim();
            if (trimmed.Length == 0) continue;

            // Location pointer: "  --> /path/to/file:LINE:COL"
            var locMatch = Regex.Match(raw, @"-->\s+\S+:(\d+)(?::(\d+))?");
            if (locMatch.Success)
            {
                var lineNum = Math.Max(0, int.Parse(locMatch.Groups[1].Value) - 1);
                var colNum = locMatch.Groups[2].Success
                    ? Math.Max(0, int.Parse(locMatch.Groups[2].Value) - 1)
                    : 0;

                // Update the preceding pending diagnostic with the precise location
                if (diagnostics.Count > 0 && diagnostics[^1].Pending)
                {
                    var d = diagnostics[^1];
                    diagnostics[^1] = d with
                    {
                        Range = new LspRange(
                            new LspPosition(lineNum, colNum),
                            new LspPosition(lineNum, int.MaxValue)),
                        Pending = false,
                    };
                }
                continue;
            }

            // Error message line
            if (Regex.IsMatch(trimmed, @"error|invalid|missing|required|unexpected",
                    RegexOptions.IgnoreCase))
            {
                diagnostics.Add(new LspDiagnostic(
                    Range: new LspRange(new LspPosition(0, 0), new LspPosition(0, int.MaxValue)),
                    Severity: 1,
                    Source: "opengrep",
                    Message: trimmed,
                    Pending: true));
            }
        }

        // Finalise any diagnostics that never got a location (leave at line 0)
        for (int i = 0; i < diagnostics.Count; i++)
            if (diagnostics[i].Pending)
                diagnostics[i] = diagnostics[i] with { Pending = false };

        // Fallback: non-zero exit but no recognisable error lines
        if (diagnostics.Count == 0 && output.Trim().Length > 0)
        {
            diagnostics.Add(new LspDiagnostic(
                Range: new LspRange(new LspPosition(0, 0), new LspPosition(0, int.MaxValue)),
                Severity: 1,
                Source: "opengrep",
                Message: output.Trim().Split('\n')[0],
                Pending: false));
        }

        return [.. diagnostics];
    }

    // ── Wire protocol helpers ─────────────────────────────────────────────────

    private static async Task SendAsync(Stream stdout, Action<Utf8JsonWriter> write)
    {
        var buf = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buf))
            write(w);

        var header = $"Content-Length: {buf.WrittenCount}\r\n\r\n";
        await stdout.WriteAsync(Encoding.ASCII.GetBytes(header));
        await stdout.WriteAsync(buf.WrittenMemory);
        await stdout.FlushAsync();
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

        // Drain what's already in our internal buffer first
        int fromBuf = Math.Min(length, _end - _start);
        if (fromBuf > 0)
        {
            Array.Copy(_buf, _start, body, 0, fromBuf);
            _start += fromBuf;
            offset += fromBuf;
        }

        // Read the remainder directly from the stream
        while (offset < length)
        {
            var read = await stream.ReadAsync(body.AsMemory(offset, length - offset));
            if (read == 0) throw new EndOfStreamException("LSP stdin closed mid-message.");
            offset += read;
        }

        return body;
    }
}
