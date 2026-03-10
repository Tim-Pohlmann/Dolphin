using System.Buffers;
using System.Collections.Concurrent;
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
    private static bool _shutdownReceived;

    // Guards concurrent writes to stdout (validation runs off the message loop).
    private static readonly SemaphoreSlim _stdoutLock = new(1, 1);

    // Per-URI cancellation: cancels superseded validations on rapid edits.
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> _validationCts = new();

    // Safety caps to prevent OOM from malformed/hostile clients.
    internal const int MaxHeaderBytes = 8 * 1024;        // 8 KB — headers are tiny
    internal const int MaxBodyBytes   = 10 * 1024 * 1024; // 10 MB

    private const int ProcessReaperTimeoutSeconds = 5;
    private const int JsonRpcInternalError = -32603;

    private const string JsonRpc = "jsonrpc";

    public static async Task RunAsync(Stream? inputStream = null, Stream? outputStream = null)
    {
        // Reset per-session state so in-process re-entry starts clean.
        _shutdownReceived = false;
        // Drain atomically: TryRemove per key avoids losing a CTS inserted between
        // a foreach snapshot and a subsequent Clear().
        var cancelTasks = _validationCts.Keys
            .Select(key => _validationCts.TryRemove(key, out var old) ? old.CancelAsync() : Task.CompletedTask);
        await Task.WhenAll(cancelTasks);

        // Best-effort early resolution; if it fails we retry on first validate.
        try { _opengrepBinary = await Installer.EnsureInstalledAsync(); }
        catch { /* will retry lazily */ }

        var reader = new LspReader(inputStream ?? Console.OpenStandardInput());
        var stdout = outputStream ?? Console.OpenStandardOutput();

        while (true)
        {
            var (close, body) = await TryReadNextMessageAsync(reader);
            if (close) break;
            if (body is null) continue; // no Content-Length — skip malformed header
            try
            {
                using var doc = JsonDocument.Parse(body);
                await HandleMessageAsync(doc.RootElement, stdout);
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"[dolphin-lsp] failed to parse message: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Reads the next LSP header + body from <paramref name="reader"/>.
    /// Returns <c>(Close: true, _)</c> when the loop should exit (stdin closed or fatal error).
    /// Returns <c>(Close: false, null)</c> when the header had no Content-Length (skip).
    /// Returns <c>(Close: false, body)</c> when a complete message was read.
    /// </summary>
    private static async Task<(bool Close, byte[]? Body)> TryReadNextMessageAsync(LspReader reader)
    {
        string? header;
        try { header = await reader.ReadHeaderAsync(); }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[dolphin-lsp] error reading header: {ex.Message}");
            return (true, null);
        }
        if (header is null) return (true, null); // stdin closed

        // Case-insensitive per the LSP spec (HTTP-style headers).
        var clMatch = Regex.Match(header, @"Content-Length:\s*(\d+)",
            RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        if (!clMatch.Success) return (false, null);

        // Parse as long to catch values that overflow int; treat as too-large → close.
        if (!long.TryParse(clMatch.Groups[1].Value, out var lengthLong) || lengthLong > MaxBodyBytes)
        {
            await Console.Error.WriteLineAsync("[dolphin-lsp] message body too large or malformed Content-Length; closing connection.");
            return (true, null);
        }

        try { return (false, await reader.ReadBodyAsync((int)lengthLong)); }
        catch (EndOfStreamException) { return (true, null); } // stdin closed mid-message
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[dolphin-lsp] error reading body: {ex.Message}");
            return (true, null);
        }
    }

    // ── Message dispatch ──────────────────────────────────────────────────────

    private static async Task HandleMessageAsync(JsonElement msg, Stream stdout)
    {
        if (!msg.TryGetProperty("method", out var methodEl)) return;
        var method = methodEl.GetString();

        msg.TryGetProperty("id", out var id);
        msg.TryGetProperty("params", out var p);

        try
        {
            switch (method)
            {
                case "initialize":
                    await MaybeSendAsync(stdout, id, w =>
                    {
                        w.WriteStartObject();
                        w.WriteString(JsonRpc, "2.0");
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
                    if (IsDolphinRulesFile(uri))
                        _ = ValidateAndPublishAsync(stdout, uri, text, CancelPrevious(uri));
                    break;
                }

                case "textDocument/didChange":
                {
                    var uri = p.GetProperty("textDocument").GetProperty("uri").GetString() ?? "";
                    var changes = p.GetProperty("contentChanges");
                    if (changes.GetArrayLength() > 0 && IsDolphinRulesFile(uri))
                        _ = ValidateAndPublishAsync(stdout, uri,
                            changes[0].GetProperty("text").GetString() ?? "",
                            CancelPrevious(uri));
                    break;
                }

                case "textDocument/didClose":
                {
                    var uri = p.GetProperty("textDocument").GetProperty("uri").GetString() ?? "";
                    CancelAndRemove(uri);
                    // Only clear diagnostics we published; don't stomp on other servers.
                    if (IsDolphinRulesFile(uri))
                        await PublishDiagnosticsAsync(stdout, uri, []);
                    break;
                }

                case "shutdown":
                    _shutdownReceived = true;
                    await MaybeSendAsync(stdout, id, w =>
                    {
                        w.WriteStartObject();
                        w.WriteString(JsonRpc, "2.0");
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
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[dolphin-lsp] error handling '{method}': {ex.Message}");
            await TrySendErrorAsync(stdout, id, ex);
        }
    }

    // ── Validation ────────────────────────────────────────────────────────────

    private static bool IsDolphinRulesFile(string uri) =>
        (uri.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
         uri.EndsWith(".yml",  StringComparison.OrdinalIgnoreCase)) &&
        (uri.Contains("/.dolphin/") || uri.Contains("\\.dolphin\\"));

    /// <summary>
    /// Cancels any in-flight validation for <paramref name="uri"/> and returns a fresh
    /// <see cref="CancellationTokenSource"/> for the new validation.
    /// The caller must pass the returned CTS to <see cref="ValidateAndPublishAsync"/>,
    /// which owns its disposal via <c>using</c>.
    /// </summary>
    private static CancellationTokenSource CancelPrevious(string uri)
    {
        var cts = new CancellationTokenSource();
        _validationCts.AddOrUpdate(uri, cts, (_, prev) =>
        {
            prev.Cancel();
            // Do not dispose prev here: disposal is owned by its associated
            // ValidateAndPublishAsync task (via using(cts)), so the token remains
            // valid for any in-flight WaitForExitAsync/Register calls.
            return cts;
        });
        return cts;
    }

    /// <summary>
    /// Cancels any in-flight validation for <paramref name="uri"/> and removes it from the map.
    /// Called on document close to prevent stale publish and reclaim the CTS.
    /// </summary>
    private static void CancelAndRemove(string uri)
    {
        if (_validationCts.TryRemove(uri, out var cts))
        {
            cts.Cancel();
            // Disposal is owned by the associated ValidateAndPublishAsync task.
        }
    }

    private static async Task ValidateAndPublishAsync(
        Stream stdout, string uri, string text, CancellationTokenSource cts)
    {
        using (cts) // owns disposal; keeps the token alive for the full duration
        {
            var ct = cts.Token;
            try
            {
                // Lazy retry: attempt resolution if startup failed.
                if (_opengrepBinary is null)
                {
                    try { _opengrepBinary = await Installer.EnsureInstalledAsync(); }
                    catch { return; }
                }

                var diagnostics = await RunValidateAsync(text, ct);
                await PublishDiagnosticsAsync(stdout, uri, diagnostics, ct);
            }
            catch (OperationCanceledException) { /* superseded by a newer edit */ }
            catch { /* swallow — must not propagate from fire-and-forget */ }
            finally
            {
                // Remove from the map before disposal; if CancelPrevious already replaced
                // this CTS with a newer one the TryRemove is a no-op and the new CTS is safe.
                _validationCts.TryRemove(new KeyValuePair<string, CancellationTokenSource>(uri, cts));
            }
        }
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
            try
            {
                // Read stdout and stderr concurrently to avoid deadlock when
                // the child fills one pipe while we're blocked reading the other.
                var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
                var stderrTask = proc.StandardError.ReadToEndAsync(ct);
                await Task.WhenAll(stdoutTask, stderrTask);
                await proc.WaitForExitAsync(ct);

                return proc.ExitCode == 0
                    ? []
                    : LspDiagnosticsParser.Parse(stdoutTask.Result + stderrTask.Result);
            }
            catch (OperationCanceledException)
            {
                // Kill the process so it doesn't linger after a superseded validation.
                try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                // Reap the child to avoid zombie processes on Unix.
                try
                {
                    using var reaperCts = new CancellationTokenSource(TimeSpan.FromSeconds(ProcessReaperTimeoutSeconds));
                    await proc.WaitForExitAsync(reaperCts.Token);
                }
                catch { /* best-effort */ }
                return [];
            }
        }
        catch
        {
            return [];
        }
        finally
        {
            try { File.Delete(tmp); } catch (Exception) { /* best-effort cleanup */ }
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

    /// <summary>
    /// Sends a response only when <paramref name="id"/> is present (i.e. the message is a
    /// request, not a JSON-RPC notification).  Per JSON-RPC 2.0, notifications must not
    /// be answered.
    /// </summary>
    private static Task MaybeSendAsync(Stream stdout, JsonElement id, Action<Utf8JsonWriter> write) =>
        id.ValueKind == JsonValueKind.Undefined ? Task.CompletedTask : SendAsync(stdout, write);

    private static Task TrySendErrorAsync(Stream stdout, JsonElement id, Exception ex) =>
        MaybeSendAsync(stdout, id, w =>
        {
            w.WriteStartObject();
            w.WriteString(JsonRpc, "2.0");
            WriteId(w, id);
            w.WritePropertyName("error");
            w.WriteStartObject();
            w.WriteNumber("code", JsonRpcInternalError);
            w.WriteString("message", ex.Message);
            w.WriteEndObject();
            w.WriteEndObject();
        });

    private static void WriteId(Utf8JsonWriter w, JsonElement id)
    {
        w.WritePropertyName("id");
        if (id.ValueKind == JsonValueKind.Undefined)
            w.WriteNullValue();
        else
            id.WriteTo(w);
    }

    private static async Task PublishDiagnosticsAsync(
        Stream stdout, string uri, LspDiagnostic[] diagnostics, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); // guard against the window between validation and sending
        await SendAsync(stdout, w =>
        {
            w.WriteStartObject();
            w.WriteString(JsonRpc, "2.0");
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
        w.WriteNumber("character", pos.Character);
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

            if (header.Count > LspServer.MaxHeaderBytes)
                throw new InvalidDataException($"LSP header exceeds {LspServer.MaxHeaderBytes} bytes.");

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
