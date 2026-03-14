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
public static partial class LspServer
{
    private static string? _opengrepBinary;

    // Guards concurrent writes to stdout (validation runs off the message loop).
    private static readonly SemaphoreSlim _stdoutLock = new(1, 1);

    // Per-URI cancellation: cancels superseded validations on rapid edits.
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> _validationCts = new();

    // Safety caps to prevent OOM from malformed/hostile clients.
    internal const int MaxHeaderBytes = 8 * 1024;        // 8 KB — headers are tiny
    internal const int MaxBodyBytes   = 10 * 1024 * 1024; // 10 MB

    private const int ProcessReaperTimeoutSeconds = 5;
    private const int JsonRpcParseError      = -32700;
    private const int JsonRpcInvalidRequest  = -32600;
    private const int JsonRpcMethodNotFound  = -32601;
    private const int JsonRpcInvalidParams   = -32602;
    private const int JsonRpcInternalError   = -32603;

    private const string JsonRpc        = "jsonrpc";
    private const string ErrorProperty   = "error";
    private const string MessageProperty = "message";

    [GeneratedRegex(@"Content-Length:\s*(\d+)", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ContentLengthRegex();

    public static async Task<int> RunAsync(Stream? inputStream = null, Stream? outputStream = null)
    {
        await DrainValidationsAsync(); // cancel any leftovers from a previous in-process session

        // Best-effort early resolution; if it fails we retry on first validate.
        try { _opengrepBinary = await Installer.EnsureInstalledAsync(); }
        catch { /* will retry lazily */ }

        var reader = new LspReader(inputStream ?? Console.OpenStandardInput());
        var stdout = outputStream ?? Console.OpenStandardOutput();

        bool shutdownReceived = false;
        while (true)
        {
            var (close, body) = await TryReadNextMessageAsync(reader);
            if (close) break;
            var (action, continueLoop) = await HandleBodyAsync(stdout, body!);
            if (!continueLoop) break;
            if (action == MessageAction.ShutdownReceived) shutdownReceived = true;
            else if (action == MessageAction.ExitRequested) break;
        }

        await DrainValidationsAsync(); // cancel any validations still in flight on disconnect
        // Per LSP spec, exit without a prior shutdown is an error (code 1).
        return shutdownReceived ? 0 : 1;
    }

    /// <summary>
    /// Sends a JSON-RPC parse-error response with <c>id: null</c>.
    /// Returns <c>false</c> if the write fails due to a broken pipe (client disconnected).
    /// </summary>
    private static async Task<bool> TrySendParseErrorAsync(Stream stdout)
    {
        try
        {
            await SendAsync(stdout, w =>
            {
                w.WriteStartObject();
                w.WriteString(JsonRpc, "2.0");
                w.WriteNull("id");
                w.WritePropertyName(ErrorProperty);
                w.WriteStartObject();
                w.WriteNumber("code", JsonRpcParseError);
                w.WriteString(MessageProperty, "Parse error");
                w.WriteEndObject();
                w.WriteEndObject();
            });
            return true;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            return false; // client disconnected while we were writing
        }
    }

    /// <summary>
    /// Parses and handles one JSON-RPC message body.
    /// Returns the resulting <see cref="MessageAction"/> and a flag indicating whether the
    /// message loop should continue (<c>true</c>) or break (<c>false</c>, i.e. broken pipe).
    /// </summary>
    private static async Task<(MessageAction action, bool continueLoop)> HandleBodyAsync(
        Stream stdout, byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return (await HandleMessageAsync(doc.RootElement, stdout), true);
        }
        catch (JsonException ex)
        {
            await Console.Error.WriteLineAsync($"[dolphin-lsp] failed to parse message: {ex.Message}");
            // Per JSON-RPC 2.0, a parse error must be answered with id: null because
            // we cannot recover the id from a malformed message.
            bool sent = await TrySendParseErrorAsync(stdout);
            return (MessageAction.Continue, sent);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Broken stdout pipe — no point continuing if we can't send responses.
            await Console.Error.WriteLineAsync($"[dolphin-lsp] stdout pipe closed: {ex.Message}");
            return (MessageAction.Continue, false);
        }
        catch (Exception ex)
        {
            // Non-parse exceptions escaping HandleMessageAsync — log and keep the loop running.
            await Console.Error.WriteLineAsync($"[dolphin-lsp] unexpected error: {ex.Message}");
            return (MessageAction.Continue, true);
        }
    }

    /// <summary>
    /// Best-effort cancellation of all in-flight validations.  A single Keys snapshot would
    /// miss entries inserted concurrently, so we loop until the dictionary appears empty.
    /// This is still not strictly atomic — a new entry can arrive between the IsEmpty check
    /// and the next iteration — but it is sufficient for the two call-sites (session start/end),
    /// which run outside the message loop where new validations are enqueued.
    /// </summary>
    private static Task DrainValidationsAsync()
    {
        var tasks = new List<Task>();
        while (!_validationCts.IsEmpty)
            foreach (var key in _validationCts.Keys)
                if (_validationCts.TryRemove(key, out var old))
                    tasks.Add(old.CancelAsync());
        return tasks.Count == 0 ? Task.CompletedTask : Task.WhenAll(tasks);
    }

    /// <summary>
    /// Reads the next LSP header + body from <paramref name="reader"/>.
    /// Returns <c>(Close: true, _)</c> when the loop should exit (stdin closed or fatal error).
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
        var clMatch = ContentLengthRegex().Match(header);
        if (!clMatch.Success)
        {
            await Console.Error.WriteLineAsync("[dolphin-lsp] Content-Length header missing; closing connection.");
            return (true, null);
        }

        // Parse as long to catch values that overflow int; reject zero/negative and too-large → close.
        if (!long.TryParse(clMatch.Groups[1].Value, out var lengthLong) || lengthLong <= 0 || lengthLong > MaxBodyBytes)
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

    private static async Task<MessageAction> HandleMessageAsync(JsonElement msg, Stream stdout)
    {
        // Ensure message is a JSON object; TryGetProperty throws if it's not.
        if (msg.ValueKind != JsonValueKind.Object)
        {
            await SendAsync(stdout, w =>
            {
                w.WriteStartObject();
                w.WriteString(JsonRpc, "2.0");
                w.WriteNull("id");
                w.WritePropertyName(ErrorProperty);
                w.WriteStartObject();
                w.WriteNumber("code", JsonRpcInvalidRequest);
                w.WriteString(MessageProperty, "Invalid Request: message must be a JSON object");
                w.WriteEndObject();
                w.WriteEndObject();
            });
            return MessageAction.Continue;
        }

        msg.TryGetProperty("id", out var id);

        if (!msg.TryGetProperty("method", out var methodEl))
        {
            // No "method" field — always an Invalid Request; reply when id is present.
            await MaybeSendAsync(stdout, id, w =>
            {
                w.WriteStartObject();
                w.WriteString(JsonRpc, "2.0");
                WriteId(w, id);
                w.WritePropertyName(ErrorProperty);
                w.WriteStartObject();
                w.WriteNumber("code", JsonRpcInvalidRequest);
                w.WriteString(MessageProperty, "Invalid Request: 'method' is required");
                w.WriteEndObject();
                w.WriteEndObject();
            });
            return MessageAction.Continue;
        }
        msg.TryGetProperty("params", out var p);

        try
        {
            if (methodEl.ValueKind != JsonValueKind.String)
            {
                await MaybeSendAsync(stdout, id, w =>
                {
                    w.WriteStartObject();
                    w.WriteString(JsonRpc, "2.0");
                    WriteId(w, id);
                    w.WritePropertyName(ErrorProperty);
                    w.WriteStartObject();
                    w.WriteNumber("code", JsonRpcInvalidRequest);
                    w.WriteString(MessageProperty, "Invalid Request: 'method' must be a string");
                    w.WriteEndObject();
                    w.WriteEndObject();
                });
                return MessageAction.Continue;
            }
            var method = methodEl.GetString();
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
                    await MaybeSendAsync(stdout, id, w =>
                    {
                        w.WriteStartObject();
                        w.WriteString(JsonRpc, "2.0");
                        WriteId(w, id);
                        w.WriteNull("result");
                        w.WriteEndObject();
                    });
                    return MessageAction.ShutdownReceived;

                case "exit":
                    return MessageAction.ExitRequested;

                default:
                    // Notifications (no id) are silently ignored per JSON-RPC 2.0.
                    // Requests (id present) must receive a response or the client hangs.
                    await MaybeSendAsync(stdout, id, w =>
                    {
                        w.WriteStartObject();
                        w.WriteString(JsonRpc, "2.0");
                        WriteId(w, id);
                        w.WritePropertyName(ErrorProperty);
                        w.WriteStartObject();
                        w.WriteNumber("code", JsonRpcMethodNotFound);
                        w.WriteString(MessageProperty, $"Method not found: {method}");
                        w.WriteEndObject();
                        w.WriteEndObject();
                    });
                    break;
            }
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[dolphin-lsp] error handling '{methodEl.GetRawText()}': {ex.Message}");
            if (IsParamsError(ex))
            {
                await MaybeSendAsync(stdout, id, w =>
                {
                    w.WriteStartObject();
                    w.WriteString(JsonRpc, "2.0");
                    WriteId(w, id);
                    w.WritePropertyName(ErrorProperty);
                    w.WriteStartObject();
                    w.WriteNumber("code", JsonRpcInvalidParams);
                    w.WriteString(MessageProperty, $"Invalid params: {ex.Message}");
                    w.WriteEndObject();
                    w.WriteEndObject();
                });
            }
            else
            {
                await TrySendErrorAsync(stdout, id);
            }
        }
        return MessageAction.Continue;
    }

    private enum MessageAction { Continue, ShutdownReceived, ExitRequested }

    /// <summary>
    /// Heuristically determines whether an exception was likely caused by invalid
    /// or malformed JSON-RPC params rather than an internal server fault.
    /// </summary>
    private static bool IsParamsError(Exception ex) =>
        ex is JsonException ||
        ex is FormatException ||
        ex is ArgumentException ||
        ex is KeyNotFoundException ||
        (ex is InvalidOperationException ioe &&
         (ioe.Message.Contains("property", StringComparison.OrdinalIgnoreCase) ||
          ioe.Message.Contains("requires an element of type", StringComparison.OrdinalIgnoreCase)));

    // ── Validation ────────────────────────────────────────────────────────────

    private static bool IsDolphinRulesFile(string uri) =>
        uri.EndsWith("/.dolphin/rules.yaml",  StringComparison.OrdinalIgnoreCase) ||
        uri.EndsWith("/.dolphin/rules.yml",   StringComparison.OrdinalIgnoreCase) ||
        uri.EndsWith("\\.dolphin\\rules.yaml", StringComparison.OrdinalIgnoreCase) ||
        uri.EndsWith("\\.dolphin\\rules.yml",  StringComparison.OrdinalIgnoreCase);

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
            catch (Exception ex) { await Console.Error.WriteLineAsync($"[dolphin-lsp] validation error for {uri}: {ex.Message}"); }
            finally
            {
                // Remove from the map before disposal; if CancelPrevious already replaced
                // this CTS with a newer one the TryRemove is a no-op and the new CTS is safe.
                _validationCts.TryRemove(new KeyValuePair<string, CancellationTokenSource>(uri, cts));
            }
        }
    }

    /// <summary>
    /// Scans <paramref name="text"/> in a single pass and returns a one-element diagnostic array
    /// pointing at the first non-ASCII character, or <c>null</c> if all characters are ASCII.
    /// </summary>
    internal static LspDiagnostic[]? FindNonAsciiDiagnostic(string text)
    {
        int line = 0, col = 0;
        bool skipNextLf = false; // Skip LF if we just saw CR (handles CRLF as single newline)
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch > 127)
            {
                var pos = new LspPosition(line, col);
                return [new LspDiagnostic(
                    Range: new LspRange(pos, new LspPosition(line, col + 1)),
                    Severity: 1,
                    Source: "dolphin",
                    Message: $"Non-ASCII character '{ch}' (U+{(int)ch:X4}): .dolphin/rules.yaml must contain only ASCII characters.",
                    Pending: false)];
            }

            // Skip LF that follows CR (CRLF sequence)
            if (skipNextLf && ch == '\n')
            {
                skipNextLf = false;
                continue;
            }
            skipNextLf = false;

            if (ch == '\r')
            {
                // Treat CR and CRLF as single newline.
                line++;
                col = 0;
                skipNextLf = true;
            }
            else if (ch == '\n')
            {
                line++;
                col = 0;
            }
            else
            {
                col++;
            }
        }
        return null;
    }

    private static async Task<LspDiagnostic[]> RunValidateAsync(string text, CancellationToken ct)
    {
        var nonAscii = FindNonAsciiDiagnostic(text);
        if (nonAscii is not null) return nonAscii;

        var tmp = Path.Combine(Path.GetTempPath(), $"dolphin-lsp-{Guid.NewGuid():N}.yaml");
        try
        {
            await File.WriteAllTextAsync(tmp, text, Encoding.ASCII, ct);

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
            Task<string> stdoutTask = Task.FromResult("");
            Task<string> stderrTask = Task.FromResult("");
            try
            {
                // Read stdout and stderr concurrently to avoid deadlock when
                // the child fills one pipe while we're blocked reading the other.
                // Pass ct so that if validation is cancelled, reads are interrupted
                // and we can kill the process even if it hangs.
                stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
                stderrTask = proc.StandardError.ReadToEndAsync(ct);
                await Task.WhenAll(stdoutTask, stderrTask);
                await proc.WaitForExitAsync(ct);

                var stdout = stdoutTask.Result;
                var stderr = stderrTask.Result;
                var combined = (stdout.Length > 0 && stderr.Length > 0 && !stdout.EndsWith('\n'))
                    ? stdout + '\n' + stderr
                    : stdout + stderr;
                return proc.ExitCode == 0
                    ? []
                    : LspDiagnosticsParser.Parse(combined);
            }
            catch (OperationCanceledException)
            {
                // Kill the process so it doesn't linger after a superseded validation.
                try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }

                // Observe any exceptions from the reads. If one read threw the cancellation,
                // the other might still be in-flight; awaiting them ensures both are properly observed.
                try { await Task.WhenAll(stdoutTask, stderrTask); }
                catch { /* suppressed: reads failed due to process being killed */ }

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
        catch (OperationCanceledException)
        {
            return []; // validation was superseded — caller ignores the result
        }
        finally
        {
            try { File.Delete(tmp); } catch (Exception) { /* best-effort cleanup */ }
        }
    }

    // ── Wire protocol helpers ─────────────────────────────────────────────────

    private static async Task SendAsync(Stream stdout, Action<Utf8JsonWriter> write,
        CancellationToken ct = default)
    {
        var buf = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buf))
            write(w);

        var header = $"Content-Length: {buf.WrittenCount}\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);

        // Honour cancellation before acquiring the lock; once we hold it we must
        // write atomically — passing ct to WriteAsync/FlushAsync risks a partial
        // header+body write that would permanently desync the LSP stream.
        await _stdoutLock.WaitAsync(ct);
        try
        {
            // Re-check after acquiring: the token may have fired while we waited,
            // so skip the write to avoid publishing stale diagnostics.
            if (ct.IsCancellationRequested) return;

            await stdout.WriteAsync(headerBytes, CancellationToken.None);
            await stdout.WriteAsync(buf.WrittenMemory, CancellationToken.None);
            await stdout.FlushAsync(CancellationToken.None);
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

    private static Task TrySendErrorAsync(Stream stdout, JsonElement id) =>
        MaybeSendAsync(stdout, id, w =>
        {
            // Send a generic message; full details are already logged to stderr.
            // Avoid leaking internal paths or exception details over the LSP channel.
            w.WriteStartObject();
            w.WriteString(JsonRpc, "2.0");
            WriteId(w, id);
            w.WritePropertyName(ErrorProperty);
            w.WriteStartObject();
            w.WriteNumber("code", JsonRpcInternalError);
            w.WriteString(MessageProperty, "Internal error");
            w.WriteEndObject();
            w.WriteEndObject();
        });

    private static void WriteId(Utf8JsonWriter w, JsonElement id)
    {
        w.WritePropertyName("id");
        // JSON-RPC 2.0: id must be a string, number, or null.
        // Echo it back only for those kinds; fall back to null for anything else
        // (object/array/bool) so we never emit an invalid response payload.
        switch (id.ValueKind)
        {
            case JsonValueKind.String:
            case JsonValueKind.Number:
            case JsonValueKind.Null:
                id.WriteTo(w);
                break;
            default:
                w.WriteNullValue();
                break;
        }
    }

    private static async Task PublishDiagnosticsAsync(
        Stream stdout, string uri, LspDiagnostic[] diagnostics, CancellationToken ct = default)
    {
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
                w.WriteString(MessageProperty, d.Message);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
        }, ct);
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
