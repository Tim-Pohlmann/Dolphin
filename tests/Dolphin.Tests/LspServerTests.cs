using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dolphin.Lsp;

namespace Dolphin.Tests;

/// <summary>
/// Unit tests for LspReader and integration smoke-tests for the LSP server.
/// Integration tests spawn `dolphin lsp` and speak the LSP wire protocol.
/// </summary>
[TestClass]
public partial class LspServerTests
{
    // ── LspReader unit tests ───────────────────────────────────────────────────

    [TestMethod]
    public async Task LspReader_ReadHeaderAsync_ReturnsNullOnEmptyStream()
    {
        var reader = new LspReader(new MemoryStream(Array.Empty<byte>()));
        Assert.IsNull(await reader.ReadHeaderAsync());
    }

    [TestMethod]
    public async Task LspReader_ReadHeaderAsync_ParsesContentLengthHeader()
    {
        var bytes = Encoding.ASCII.GetBytes("Content-Length: 7\r\n\r\n");
        var reader = new LspReader(new MemoryStream(bytes));
        var header = await reader.ReadHeaderAsync();
        Assert.IsNotNull(header);
        StringAssert.Contains(header, "Content-Length: 7");
    }

    [TestMethod]
    public async Task LspReader_ReadBodyAsync_ReadsExactBytes()
    {
        var body = "{\"x\":1}"u8.ToArray();
        var reader = new LspReader(new MemoryStream(body));
        CollectionAssert.AreEqual(body, await reader.ReadBodyAsync(body.Length));
    }

    [TestMethod]
    public async Task LspReader_ReadHeaderThenBody_RoundTrips()
    {
        var json = "{\"method\":\"test\"}";
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var headerBytes = Encoding.ASCII.GetBytes($"Content-Length: {jsonBytes.Length}\r\n\r\n");
        var stream = new MemoryStream([.. headerBytes, .. jsonBytes]);
        var reader = new LspReader(stream);

        var header = await reader.ReadHeaderAsync();
        Assert.IsNotNull(header);
        var len = int.Parse(FirstDigitsRegex().Match(header).Value);
        var body = await reader.ReadBodyAsync(len);
        Assert.AreEqual(json, Encoding.UTF8.GetString(body));
    }

    // ── Integration helpers ────────────────────────────────────────────────────

    private static Process StartLspServer()
    {
        var projectPath = TestProcessHelper.FindDolphinProjectPath();
        var config = TestProcessHelper.CurrentConfiguration();
        var psi = new ProcessStartInfo(
            "dotnet",
            $"run --no-build --configuration {config} --project \"{projectPath}\" -- lsp")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false, // not consumed — redirecting without draining risks pipe deadlock
            UseShellExecute = false,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
        };
        return Process.Start(psi)
            ?? throw new InvalidOperationException(
                $"Failed to start LSP server process: dotnet {psi.Arguments}");
    }

    private static void SendLsp(Process proc, string json)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(json);
        var headerBytes = Encoding.ASCII.GetBytes($"Content-Length: {bodyBytes.Length}\r\n\r\n");
        proc.StandardInput.BaseStream.Write(headerBytes);
        proc.StandardInput.BaseStream.Write(bodyBytes);
        proc.StandardInput.BaseStream.Flush();
    }

    /// <summary>Reads the next LSP message (response or notification) from the server.</summary>
    private static async Task<JsonObject> ReceiveLspMessageAsync(LspReader reader, CancellationToken ct)
    {
        string header = await reader.ReadHeaderAsync().WaitAsync(ct)
                        ?? throw new EndOfStreamException("LSP server closed stdout");
        var m = ContentLengthRegex().Match(header);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var contentLength) || contentLength <= 0)
        {
            throw new System.IO.InvalidDataException($"Invalid LSP header, missing or malformed Content-Length: '{header}'");
        }
        var body = await reader.ReadBodyAsync(contentLength).WaitAsync(ct);
        return (JsonObject)JsonNode.Parse(body)!;
    }

    private static async Task<JsonObject?> TryReceiveLspMessageAsync(LspReader reader, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try { return await ReceiveLspMessageAsync(reader, cts.Token); }
        catch (OperationCanceledException) { return null; }
        catch (EndOfStreamException) { return null; }
    }

    // ── Integration tests ──────────────────────────────────────────────────────

    [TestMethod]
    public async Task LspServer_Initialize_ReturnsCapabilities()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var proc = StartLspServer();
        var reader = new LspReader(proc.StandardOutput.BaseStream);
        try
        {
            SendLsp(proc, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}""");
            var response = await ReceiveLspMessageAsync(reader, cts.Token);

            Assert.IsNull(response["error"], $"Unexpected error: {response["error"]}");
            Assert.IsNotNull(response["result"]?["capabilities"], "Missing capabilities in response");
        }
        finally
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            using var killCts = new CancellationTokenSource(5000);
            try { await proc.WaitForExitAsync(killCts.Token); } catch { }
        }
    }

    [TestMethod]
    public async Task LspServer_Shutdown_ReturnsNullResult()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var proc = StartLspServer();
        var reader = new LspReader(proc.StandardOutput.BaseStream);
        try
        {
            SendLsp(proc, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}""");
            await ReceiveLspMessageAsync(reader, cts.Token);

            SendLsp(proc, """{"jsonrpc":"2.0","id":2,"method":"shutdown"}""");
            var response = await ReceiveLspMessageAsync(reader, cts.Token);

            Assert.IsNull(response["error"], $"Unexpected error: {response["error"]}");
            Assert.IsTrue(response.ContainsKey("result"), "Shutdown response must have a result key");
            Assert.IsNull(response["result"], "Shutdown result must be JSON null");
        }
        finally
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            using var killCts = new CancellationTokenSource(5000);
            try { await proc.WaitForExitAsync(killCts.Token); } catch { }
        }
    }

    [TestMethod]
    public async Task LspServer_DidClose_PublishesEmptyDiagnostics()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var proc = StartLspServer();
        var reader = new LspReader(proc.StandardOutput.BaseStream);
        try
        {
            SendLsp(proc, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}""");
            await ReceiveLspMessageAsync(reader, cts.Token);

            const string uri = "file:///tmp/.dolphin/rules.yaml";
            SendLsp(proc, "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didClose\",\"params\":{\"textDocument\":{\"uri\":\"" + uri + "\"}}}");

            // didClose synchronously publishes empty diagnostics before the next message is read
            var notif = await ReceiveLspMessageAsync(reader, cts.Token);
            Assert.AreEqual("textDocument/publishDiagnostics", notif["method"]?.GetValue<string>());
            Assert.AreEqual(uri, notif["params"]?["uri"]?.GetValue<string>());
            Assert.AreEqual(0, notif["params"]?["diagnostics"]?.AsArray().Count);
        }
        finally
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            using var killCts = new CancellationTokenSource(5000);
            try { await proc.WaitForExitAsync(killCts.Token); } catch { }
        }
    }

    [TestMethod]
    public async Task LspServer_InvalidJsonMessage_ServerSurvivesAndAnswersNext()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var proc = StartLspServer();
        var reader = new LspReader(proc.StandardOutput.BaseStream);
        try
        {
            const string garbage = "this is not json at all";
            proc.StandardInput.Write(
                $"Content-Length: {Encoding.UTF8.GetByteCount(garbage)}\r\n\r\n{garbage}");
            proc.StandardInput.Flush();

            // First response: parse error for the garbage body (JSON-RPC -32700, id: null).
            var parseError = await ReceiveLspMessageAsync(reader, cts.Token);
            Assert.IsNull(parseError["id"]);
            Assert.AreEqual(-32700, parseError["error"]!["code"]!.GetValue<int>());

            SendLsp(proc, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}""");
            var response = await ReceiveLspMessageAsync(reader, cts.Token);

            Assert.IsNull(response["error"]);
            Assert.IsNotNull(response["result"]?["capabilities"]);
        }
        finally
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            using var killCts = new CancellationTokenSource(5000);
            try { await proc.WaitForExitAsync(killCts.Token); } catch { }
        }
    }

    [TestMethod]
    public async Task LspServer_OversizedBody_ClosesConnection()
    {
        // When Content-Length exceeds MaxBodyBytes the server breaks the message
        // loop immediately to avoid stream desynchronization.  The process must
        // exit cleanly (no hang, no crash).
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var proc = StartLspServer();
        var reader = new LspReader(proc.StandardOutput.BaseStream);
        try
        {
            proc.StandardInput.Write("Content-Length: 20000000\r\n\r\n");
            proc.StandardInput.Flush();

            // Server should exit; attempting to read from its stdout gives null/EOF.
            var msg = await TryReceiveLspMessageAsync(reader, timeoutMs: 5000);
            Assert.IsNull(msg, "Expected no response after oversized-body disconnect");

            using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await proc.WaitForExitAsync(exitCts.Token);
        }
        finally
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            using var killCts = new CancellationTokenSource(5000);
            try { await proc.WaitForExitAsync(killCts.Token); } catch { }
        }
    }

    [TestMethod]
    public async Task LspServer_DidOpen_NonDolphinFile_NoPublishDiagnostics()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var proc = StartLspServer();
        var reader = new LspReader(proc.StandardOutput.BaseStream);
        try
        {
            SendLsp(proc, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}""");
            await ReceiveLspMessageAsync(reader, cts.Token);

            // Non-.dolphin/ URI — server should silently ignore the open.
            SendLsp(proc, """{"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":"file:///src/main.ts","languageId":"typescript","version":1,"text":""}}}""");

            // Server must immediately handle the next request without publishing diagnostics.
            SendLsp(proc, """{"jsonrpc":"2.0","id":2,"method":"shutdown"}""");
            var response = await ReceiveLspMessageAsync(reader, cts.Token);

            // First message back must be the shutdown response, not a publishDiagnostics notification.
            Assert.IsTrue(response.ContainsKey("result"), $"Expected shutdown result, got: {response}");
        }
        finally
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            using var killCts = new CancellationTokenSource(5000);
            try { await proc.WaitForExitAsync(killCts.Token); } catch { }
        }
    }

    [TestMethod]
    public async Task LspServer_DidChange_NonDolphinFile_NoPublishDiagnostics()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var proc = StartLspServer();
        var reader = new LspReader(proc.StandardOutput.BaseStream);
        try
        {
            SendLsp(proc, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}""");
            await ReceiveLspMessageAsync(reader, cts.Token);

            SendLsp(proc, """{"jsonrpc":"2.0","method":"textDocument/didChange","params":{"textDocument":{"uri":"file:///src/main.ts","version":2},"contentChanges":[{"text":"x=1"}]}}""");

            SendLsp(proc, """{"jsonrpc":"2.0","id":2,"method":"shutdown"}""");
            var response = await ReceiveLspMessageAsync(reader, cts.Token);

            Assert.IsTrue(response.ContainsKey("result"), $"Expected shutdown result, got: {response}");
        }
        finally
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            using var killCts = new CancellationTokenSource(5000);
            try { await proc.WaitForExitAsync(killCts.Token); } catch { }
        }
    }

    [TestMethod]
    public async Task LspServer_DidChange_DolphinFile_ServerRemainsResponsive()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var proc = StartLspServer();
        var reader = new LspReader(proc.StandardOutput.BaseStream);
        try
        {
            SendLsp(proc, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}""");
            await ReceiveLspMessageAsync(reader, cts.Token);

            // Fires the validation path; opengrep may or may not be present — either way
            // the server must not crash.
            SendLsp(proc, """{"jsonrpc":"2.0","method":"textDocument/didChange","params":{"textDocument":{"uri":"file:///project/.dolphin/rules.yaml","version":2},"contentChanges":[{"text":"rules: []"}]}}""");

            SendLsp(proc, """{"jsonrpc":"2.0","id":2,"method":"shutdown"}""");

            // Drain any publishDiagnostics notifications that may arrive before the shutdown response.
            JsonObject response;
            while (true)
            {
                response = await ReceiveLspMessageAsync(reader, cts.Token);
                if (response["method"]?.GetValue<string>() == "textDocument/publishDiagnostics")
                    continue;
                break;
            }

            Assert.IsTrue(response.ContainsKey("result"), $"Expected shutdown result, got: {response}");
        }
        finally
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            using var killCts = new CancellationTokenSource(5000);
            try { await proc.WaitForExitAsync(killCts.Token); } catch { }
        }
    }

    [TestMethod]
    public async Task LspServer_DidClose_NonDolphinFile_NoPublishDiagnostics()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var proc = StartLspServer();
        var reader = new LspReader(proc.StandardOutput.BaseStream);
        try
        {
            SendLsp(proc, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}""");
            await ReceiveLspMessageAsync(reader, cts.Token);

            // Non-.dolphin/ URI — no publishDiagnostics should be sent.
            SendLsp(proc, """{"jsonrpc":"2.0","method":"textDocument/didClose","params":{"textDocument":{"uri":"file:///src/main.ts"}}}""");

            SendLsp(proc, """{"jsonrpc":"2.0","id":2,"method":"shutdown"}""");
            var response = await ReceiveLspMessageAsync(reader, cts.Token);

            Assert.IsTrue(response.ContainsKey("result"), $"Expected shutdown result, got: {response}");
        }
        finally
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            using var killCts = new CancellationTokenSource(5000);
            try { await proc.WaitForExitAsync(killCts.Token); } catch { }
        }
    }

    // ── LspReader edge-case unit tests ────────────────────────────────────────

    [TestMethod]
    public async Task LspReader_ReadHeaderAsync_OversizedHeader_ThrowsInvalidDataException()
    {
        // Build a stream with more than MaxHeaderBytes before \r\n\r\n.
        var payload = new byte[LspServer.MaxHeaderBytes + 1];
        Array.Fill(payload, (byte)'A');
        var reader = new LspReader(new MemoryStream(payload));

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reader.ReadHeaderAsync());
    }

    [TestMethod]
    public async Task LspReader_ReadBodyAsync_StreamClosedMidMessage_ThrowsEndOfStreamException()
    {
        // Stream has only 3 bytes but we request 10.
        var reader = new LspReader(new MemoryStream(new byte[3]));
        await Assert.ThrowsExactlyAsync<EndOfStreamException>(() => reader.ReadBodyAsync(10));
    }

    [TestMethod]
    public async Task LspReader_ReadHeaderAsync_BadHeader_NoContentLength_ReturnsHeader()
    {
        // A well-formed header sequence that has no Content-Length should still
        // return the raw string (TryReadNextMessageAsync then closes the connection).
        var bytes = Encoding.ASCII.GetBytes("X-Custom: value\r\n\r\n");
        var reader = new LspReader(new MemoryStream(bytes));
        var header = await reader.ReadHeaderAsync();
        Assert.IsNotNull(header);
        StringAssert.Contains(header, "X-Custom");
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex FirstDigitsRegex();

    [GeneratedRegex(@"Content-Length:\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ContentLengthRegex();
}
