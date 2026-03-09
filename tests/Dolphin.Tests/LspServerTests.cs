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
public class LspServerTests
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
        var len = int.Parse(Regex.Match(header, @"\d+").Value);
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
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        return Process.Start(psi)!;
    }

    private static void SendLsp(Process proc, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        proc.StandardInput.Write($"Content-Length: {bytes.Length}\r\n\r\n{json}");
        proc.StandardInput.Flush();
    }

    /// <summary>Reads the next LSP message (response or notification) from the server.</summary>
    private static async Task<JsonObject> ReceiveLspMessageAsync(Process proc, CancellationToken ct)
    {
        int contentLength = 0;
        while (true)
        {
            var line = await proc.StandardOutput.ReadLineAsync(ct)
                       ?? throw new EndOfStreamException("LSP server closed stdout");
            if (line.Length == 0) break;
            var m = Regex.Match(line, @"^Content-Length:\s*(\d+)$", RegexOptions.IgnoreCase);
            if (m.Success) contentLength = int.Parse(m.Groups[1].Value);
        }
        var buf = new char[contentLength];
        int offset = 0;
        while (offset < contentLength)
            offset += await proc.StandardOutput.ReadAsync(buf.AsMemory(offset, contentLength - offset), ct);
        return (JsonObject)JsonNode.Parse(new string(buf))!;
    }

    private static async Task<JsonObject?> TryReceiveLspMessageAsync(Process proc, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try { return await ReceiveLspMessageAsync(proc, cts.Token); }
        catch (OperationCanceledException) { return null; }
    }

    // ── Integration tests ──────────────────────────────────────────────────────

    [TestMethod]
    public async Task LspServer_Initialize_ReturnsCapabilities()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var proc = StartLspServer();
        try
        {
            SendLsp(proc, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}""");
            var response = await ReceiveLspMessageAsync(proc, cts.Token);

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
        try
        {
            SendLsp(proc, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}""");
            await ReceiveLspMessageAsync(proc, cts.Token);

            SendLsp(proc, """{"jsonrpc":"2.0","id":2,"method":"shutdown"}""");
            var response = await ReceiveLspMessageAsync(proc, cts.Token);

            Assert.IsNull(response["error"], $"Unexpected error: {response["error"]}");
            Assert.IsTrue(response.ContainsKey("result"), "Shutdown response must have a result key");
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
        try
        {
            SendLsp(proc, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}""");
            await ReceiveLspMessageAsync(proc, cts.Token);

            const string uri = "file:///tmp/.dolphin/rules.yaml";
            SendLsp(proc, "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didClose\",\"params\":{\"textDocument\":{\"uri\":\"" + uri + "\"}}}");

            // didClose synchronously publishes empty diagnostics before the next message is read
            var notif = await ReceiveLspMessageAsync(proc, cts.Token);
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
        try
        {
            const string garbage = "this is not json at all";
            proc.StandardInput.Write(
                $"Content-Length: {Encoding.UTF8.GetByteCount(garbage)}\r\n\r\n{garbage}");
            proc.StandardInput.Flush();

            await Task.Delay(200, cts.Token);

            SendLsp(proc, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}""");
            var response = await ReceiveLspMessageAsync(proc, cts.Token);

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
    public async Task LspServer_OversizedBody_SkipsMessageAndRemainsResponsive()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var proc = StartLspServer();
        try
        {
            // Claim a body far beyond MaxBodyBytes; send no body bytes so the
            // server skips it and reads the next header from the stream.
            proc.StandardInput.Write("Content-Length: 20000000\r\n\r\n");
            proc.StandardInput.Flush();

            SendLsp(proc, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}""");
            var response = await ReceiveLspMessageAsync(proc, cts.Token);

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
    public async Task LspServer_DidOpen_NonDolphinFile_NoPublishDiagnostics()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var proc = StartLspServer();
        try
        {
            SendLsp(proc, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}""");
            await ReceiveLspMessageAsync(proc, cts.Token);

            // Non-.dolphin/ URI — server should silently ignore the open.
            SendLsp(proc, """{"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":"file:///src/main.ts","languageId":"typescript","version":1,"text":""}}}""");

            // Server must immediately handle the next request without publishing diagnostics.
            SendLsp(proc, """{"jsonrpc":"2.0","id":2,"method":"shutdown"}""");
            var response = await ReceiveLspMessageAsync(proc, cts.Token);

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
        try
        {
            SendLsp(proc, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}""");
            await ReceiveLspMessageAsync(proc, cts.Token);

            SendLsp(proc, """{"jsonrpc":"2.0","method":"textDocument/didChange","params":{"textDocument":{"uri":"file:///src/main.ts","version":2},"contentChanges":[{"text":"x=1"}]}}""");

            SendLsp(proc, """{"jsonrpc":"2.0","id":2,"method":"shutdown"}""");
            var response = await ReceiveLspMessageAsync(proc, cts.Token);

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
        try
        {
            SendLsp(proc, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}""");
            await ReceiveLspMessageAsync(proc, cts.Token);

            // Fires the validation path; opengrep may or may not be present — either way
            // the server must not crash.
            SendLsp(proc, """{"jsonrpc":"2.0","method":"textDocument/didChange","params":{"textDocument":{"uri":"file:///project/.dolphin/rules.yaml","version":2},"contentChanges":[{"text":"rules: []"}]}}""");

            await Task.Delay(300, cts.Token); // let fire-and-forget settle

            SendLsp(proc, """{"jsonrpc":"2.0","id":2,"method":"shutdown"}""");
            var response = await ReceiveLspMessageAsync(proc, cts.Token);

            // Drain any publishDiagnostics notification that may have arrived first.
            if (response["method"]?.GetValue<string>() == "textDocument/publishDiagnostics")
                response = await ReceiveLspMessageAsync(proc, cts.Token);

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
        try
        {
            SendLsp(proc, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}""");
            await ReceiveLspMessageAsync(proc, cts.Token);

            // Non-.dolphin/ URI — no publishDiagnostics should be sent.
            SendLsp(proc, """{"jsonrpc":"2.0","method":"textDocument/didClose","params":{"textDocument":{"uri":"file:///src/main.ts"}}}""");

            SendLsp(proc, """{"jsonrpc":"2.0","id":2,"method":"shutdown"}""");
            var response = await ReceiveLspMessageAsync(proc, cts.Token);

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

        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => reader.ReadHeaderAsync());
    }

    [TestMethod]
    public async Task LspReader_ReadBodyAsync_StreamClosedMidMessage_ThrowsEndOfStreamException()
    {
        // Stream has only 3 bytes but we request 10.
        var reader = new LspReader(new MemoryStream(new byte[3]));
        await Assert.ThrowsExceptionAsync<EndOfStreamException>(() => reader.ReadBodyAsync(10));
    }

    [TestMethod]
    public async Task LspReader_ReadHeaderAsync_BadHeader_NoContentLength_ReturnsHeader()
    {
        // A well-formed header sequence that has no Content-Length should still
        // return the raw string (the loop in RunAsync then skips it via `continue`).
        var bytes = Encoding.ASCII.GetBytes("X-Custom: value\r\n\r\n");
        var reader = new LspReader(new MemoryStream(bytes));
        var header = await reader.ReadHeaderAsync();
        Assert.IsNotNull(header);
        StringAssert.Contains(header, "X-Custom");
    }
}
