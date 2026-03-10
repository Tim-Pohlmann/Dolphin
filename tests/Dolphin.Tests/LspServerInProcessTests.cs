using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dolphin.Lsp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Dolphin.Tests;

/// <summary>
/// In-process tests for LspServer and the Program.cs routing (via Startup.RunAsync).
/// These run the server with injected MemoryStream pipes so that code-coverage tools
/// instrument the server code directly — unlike the spawned-process integration tests.
/// </summary>
[TestClass]
public partial class LspServerInProcessTests
{
    // ── Wire-protocol helpers ─────────────────────────────────────────────────

    private static byte[] BuildInput(params string[] jsons)
    {
        var ms = new MemoryStream();
        foreach (var json in jsons)
        {
            var body = Encoding.UTF8.GetBytes(json);
            var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
            ms.Write(header);
            ms.Write(body);
        }
        return ms.ToArray();
    }

    private static List<JsonObject> ParseOutput(byte[] bytes)
    {
        var results = new List<JsonObject>();
        int pos = 0;
        while (pos < bytes.Length)
        {
            int headerEnd = IndexOf(bytes, pos, "\r\n\r\n"u8);
            if (headerEnd < 0) break;
            var headerStr = Encoding.ASCII.GetString(bytes, pos, headerEnd - pos);
            var m = ContentLengthRegex().Match(headerStr);
            if (!m.Success) break;
            var length = int.Parse(m.Groups[1].Value);
            pos = headerEnd + 4;
            var json = Encoding.UTF8.GetString(bytes, pos, length);
            pos += length;
            results.Add(JsonNode.Parse(json)!.AsObject());
        }
        return results;
    }

    private static int IndexOf(byte[] src, int start, ReadOnlySpan<byte> pattern)
    {
        for (int i = start; i <= src.Length - pattern.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < pattern.Length && ok; j++)
                if (src[i + j] != pattern[j]) ok = false;
            if (ok) return i;
        }
        return -1;
    }

    /// <summary>Runs the server in-process with the given JSON messages and returns all output messages.</summary>
    private static async Task<List<JsonObject>> RunServerAsync(params string[] messages)
    {
        var input = new MemoryStream(BuildInput(messages));
        var output = new MemoryStream();
        await LspServer.RunAsync(inputStream: input, outputStream: output);
        return ParseOutput(output.ToArray());
    }

    // ── RunAsync message-loop paths ───────────────────────────────────────────

    [TestMethod]
    public async Task RunAsync_StdinClosed_ExitsCleanly()
    {
        // Empty stream → ReadHeaderAsync returns null → loop breaks without throwing.
        var output = new MemoryStream();
        await LspServer.RunAsync(inputStream: new MemoryStream([]), outputStream: output);
        Assert.AreEqual(0, output.Length);
    }

    [TestMethod]
    public async Task RunAsync_HeaderWithNoContentLength_SkipsThenProcessesNext()
    {
        // A valid header sequence with no Content-Length → the continue branch fires.
        var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("X-Unknown: whatever\r\n\r\n"));
        var shutdownJson = """{"jsonrpc":"2.0","id":1,"method":"shutdown"}""";
        var body = Encoding.UTF8.GetBytes(shutdownJson);
        ms.Write(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
        ms.Write(body);

        var output = new MemoryStream();
        await LspServer.RunAsync(inputStream: new MemoryStream(ms.ToArray()), outputStream: output);
        var responses = ParseOutput(output.ToArray());

        Assert.AreEqual(1, responses.Count);
        Assert.IsTrue(responses[0].ContainsKey("result"));
    }

    [TestMethod]
    public async Task RunAsync_OversizedBodyHeader_ClosesConnection()
    {
        // Content-Length > MaxBodyBytes → server breaks the loop immediately to avoid
        // stream desynchronization; no response is sent for subsequent messages.
        var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes($"Content-Length: {LspServer.MaxBodyBytes + 1}\r\n\r\n"));
        var shutdownJson = """{"jsonrpc":"2.0","id":1,"method":"shutdown"}""";
        var body = Encoding.UTF8.GetBytes(shutdownJson);
        ms.Write(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
        ms.Write(body);

        var output = new MemoryStream();
        await LspServer.RunAsync(inputStream: new MemoryStream(ms.ToArray()), outputStream: output);
        var responses = ParseOutput(output.ToArray());

        // Server exited cleanly without processing the next message.
        Assert.AreEqual(0, responses.Count);
    }

    [TestMethod]
    public async Task RunAsync_InvalidJson_LogsErrorAndContinues()
    {
        // Body is not valid JSON → JsonDocument.Parse throws → caught, server continues.
        var garbage = "this is not json"u8.ToArray();
        var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes($"Content-Length: {garbage.Length}\r\n\r\n"));
        ms.Write(garbage);
        var shutdownJson = """{"jsonrpc":"2.0","id":1,"method":"shutdown"}""";
        var body = Encoding.UTF8.GetBytes(shutdownJson);
        ms.Write(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
        ms.Write(body);

        var output = new MemoryStream();
        await LspServer.RunAsync(inputStream: new MemoryStream(ms.ToArray()), outputStream: output);
        var responses = ParseOutput(output.ToArray());

        Assert.AreEqual(1, responses.Count);
        Assert.IsTrue(responses[0].ContainsKey("result"));
    }

    // ── HandleMessageAsync dispatch paths ─────────────────────────────────────

    [TestMethod]
    public async Task HandleMessage_NoMethodProperty_SilentlyIgnored()
    {
        // A JSON object with no "method" field → early return, no response.
        var responses = await RunServerAsync(
            """{"jsonrpc":"2.0","id":1,"params":{}}""",
            """{"jsonrpc":"2.0","id":2,"method":"shutdown"}""");

        Assert.AreEqual(1, responses.Count, "Only shutdown should produce a response");
    }

    [TestMethod]
    public async Task HandleMessage_Initialize_ReturnsCapabilities()
    {
        var responses = await RunServerAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}""");

        Assert.AreEqual(1, responses.Count);
        Assert.IsNotNull(responses[0]["result"]?["capabilities"]);
        Assert.AreEqual(1, responses[0]["id"]?.GetValue<int>());
    }

    [TestMethod]
    public async Task HandleMessage_Initialize_NoId_NoResponse()
    {
        // Per JSON-RPC 2.0, a message without "id" is a notification; no response must be sent.
        var responses = await RunServerAsync(
            """{"jsonrpc":"2.0","method":"initialize","params":{"capabilities":{}}}""",
            """{"jsonrpc":"2.0","id":1,"method":"shutdown"}""");

        Assert.AreEqual(1, responses.Count, "Only shutdown should produce a response");
        Assert.AreEqual(1, responses[0]["id"]?.GetValue<int>());
    }

    [TestMethod]
    public async Task HandleMessage_Initialize_StringId_EchoesId()
    {
        var responses = await RunServerAsync(
            """{"jsonrpc":"2.0","id":"req-abc","method":"initialize","params":{"capabilities":{}}}""");

        Assert.AreEqual(1, responses.Count);
        Assert.AreEqual("req-abc", responses[0]["id"]?.GetValue<string>());
    }

    [TestMethod]
    public async Task HandleMessage_Shutdown_SetsShutdownAndReturnsNull()
    {
        var responses = await RunServerAsync(
            """{"jsonrpc":"2.0","id":99,"method":"shutdown"}""");

        Assert.AreEqual(1, responses.Count);
        // JSON null → null JsonNode in System.Text.Json.Nodes
        Assert.IsTrue(responses[0].ContainsKey("result"), "'result' key must be present");
        Assert.IsNull(responses[0]["result"], "'result' must be JSON null");
        Assert.AreEqual(99, responses[0]["id"]?.GetValue<int>());
    }

    [TestMethod]
    public async Task HandleMessage_DidOpen_NonDolphinFile_NoResponse()
    {
        var responses = await RunServerAsync(
            """{"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":"file:///src/app.ts","languageId":"typescript","version":1,"text":""}}}""",
            """{"jsonrpc":"2.0","id":1,"method":"shutdown"}""");

        // Only shutdown response; no publishDiagnostics.
        Assert.AreEqual(1, responses.Count);
        Assert.IsTrue(responses[0].ContainsKey("result"));
    }

    [TestMethod]
    public async Task HandleMessage_DidOpen_DolphinFile_FiresValidation()
    {
        // Validation runs fire-and-forget; opengrep may or may not be present.
        // Either way the server must remain responsive and shutdown must respond.
        var responses = await RunServerAsync(
            """{"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":"file:///project/.dolphin/rules.yaml","languageId":"yaml","version":1,"text":"rules: []"}}}""",
            """{"jsonrpc":"2.0","id":1,"method":"shutdown"}""");

        var shutdown = responses.FirstOrDefault(r => r["id"]?.GetValue<int>() == 1 && r.ContainsKey("result"));
        Assert.IsNotNull(shutdown, "Shutdown response must arrive");
    }

    [TestMethod]
    public async Task HandleMessage_DidChange_NonDolphinFile_NoResponse()
    {
        var responses = await RunServerAsync(
            """{"jsonrpc":"2.0","method":"textDocument/didChange","params":{"textDocument":{"uri":"file:///src/app.ts","version":2},"contentChanges":[{"text":"x=1"}]}}""",
            """{"jsonrpc":"2.0","id":1,"method":"shutdown"}""");

        Assert.AreEqual(1, responses.Count);
    }

    [TestMethod]
    public async Task HandleMessage_DidChange_EmptyContentChanges_NoResponse()
    {
        // The `changes.GetArrayLength() > 0` guard must suppress validation.
        var responses = await RunServerAsync(
            """{"jsonrpc":"2.0","method":"textDocument/didChange","params":{"textDocument":{"uri":"file:///project/.dolphin/rules.yaml","version":2},"contentChanges":[]}}""",
            """{"jsonrpc":"2.0","id":1,"method":"shutdown"}""");

        Assert.AreEqual(1, responses.Count);
    }

    [TestMethod]
    public async Task HandleMessage_DidChange_DolphinFile_FiresValidation()
    {
        var responses = await RunServerAsync(
            """{"jsonrpc":"2.0","method":"textDocument/didChange","params":{"textDocument":{"uri":"file:///project/.dolphin/rules.yaml","version":2},"contentChanges":[{"text":"rules: []"}]}}""",
            """{"jsonrpc":"2.0","id":1,"method":"shutdown"}""");

        var shutdown = responses.FirstOrDefault(r => r["id"]?.GetValue<int>() == 1 && r.ContainsKey("result"));
        Assert.IsNotNull(shutdown, "Shutdown response must arrive");
    }

    [TestMethod]
    public async Task HandleMessage_DidChange_SameUri_Twice_CancelsPrevious()
    {
        // Second didChange for the same URI cancels the first validation's CTS.
        // No crash expected; shutdown must respond.
        var responses = await RunServerAsync(
            """{"jsonrpc":"2.0","method":"textDocument/didChange","params":{"textDocument":{"uri":"file:///project/.dolphin/rules.yaml","version":2},"contentChanges":[{"text":"rules: []"}]}}""",
            """{"jsonrpc":"2.0","method":"textDocument/didChange","params":{"textDocument":{"uri":"file:///project/.dolphin/rules.yaml","version":3},"contentChanges":[{"text":"rules: []"}]}}""",
            """{"jsonrpc":"2.0","id":1,"method":"shutdown"}""");

        var shutdown = responses.FirstOrDefault(r => r["id"]?.GetValue<int>() == 1 && r.ContainsKey("result"));
        Assert.IsNotNull(shutdown);
    }

    [TestMethod]
    public async Task HandleMessage_DidClose_DolphinFile_PublishesEmptyDiagnostics()
    {
        const string uri = "file:///project/.dolphin/rules.yaml";
        var responses = await RunServerAsync(
            "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didClose\",\"params\":{\"textDocument\":{\"uri\":\"" + uri + "\"}}}",
            """{"jsonrpc":"2.0","id":1,"method":"shutdown"}""");

        var publish = responses.FirstOrDefault(r => r["method"]?.GetValue<string>() == "textDocument/publishDiagnostics");
        Assert.IsNotNull(publish, "publishDiagnostics notification expected");
        Assert.AreEqual(uri, publish["params"]?["uri"]?.GetValue<string>());
        Assert.AreEqual(0, publish["params"]?["diagnostics"]?.AsArray().Count);
    }

    [TestMethod]
    public async Task HandleMessage_DidClose_NonDolphinFile_NoPublishDiagnostics()
    {
        var responses = await RunServerAsync(
            """{"jsonrpc":"2.0","method":"textDocument/didClose","params":{"textDocument":{"uri":"file:///src/app.ts"}}}""",
            """{"jsonrpc":"2.0","id":1,"method":"shutdown"}""");

        var publish = responses.FirstOrDefault(r => r["method"]?.GetValue<string>() == "textDocument/publishDiagnostics");
        Assert.IsNull(publish, "No publishDiagnostics expected for non-dolphin file");
        Assert.AreEqual(1, responses.Count);
    }

    [TestMethod]
    public async Task HandleMessage_DidClose_AfterDidOpen_CancelsAndClearsEntry()
    {
        // Open then immediately close the same URI: CancelPrevious then CancelAndRemove.
        const string uri = "file:///project/.dolphin/rules.yaml";
        var responses = await RunServerAsync(
            "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"" + uri + "\",\"languageId\":\"yaml\",\"version\":1,\"text\":\"rules: []\"}}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didClose\",\"params\":{\"textDocument\":{\"uri\":\"" + uri + "\"}}}",
            """{"jsonrpc":"2.0","id":1,"method":"shutdown"}""");

        var publish = responses.FirstOrDefault(r => r["method"]?.GetValue<string>() == "textDocument/publishDiagnostics");
        Assert.IsNotNull(publish, "didClose should publish empty diagnostics");
        Assert.AreEqual(0, publish["params"]?["diagnostics"]?.AsArray().Count);
    }

    [TestMethod]
    public async Task HandleMessage_IsDolphinRulesFile_WindowsStylePath_Recognised()
    {
        // URI with backslash .dolphin\ path should be treated as a dolphin rules file.
        var responses = await RunServerAsync(
            """{"jsonrpc":"2.0","method":"textDocument/didClose","params":{"textDocument":{"uri":"file:///C:\\.dolphin\\rules.yaml"}}}""",
            """{"jsonrpc":"2.0","id":1,"method":"shutdown"}""");

        var publish = responses.FirstOrDefault(r => r["method"]?.GetValue<string>() == "textDocument/publishDiagnostics");
        Assert.IsNotNull(publish, "Windows-style .dolphin\\ path must be recognised");
    }

    [TestMethod]
    public async Task HandleMessage_MalformedParams_WithId_SendsErrorResponse()
    {
        // didOpen with a dolphin URI but missing "text" field → KeyNotFoundException in dispatch.
        // Since id is present, an error response with code -32603 must be sent.
        var responses = await RunServerAsync(
            """{"jsonrpc":"2.0","id":7,"method":"textDocument/didOpen","params":{"textDocument":{"uri":"file:///project/.dolphin/rules.yaml"}}}""",
            """{"jsonrpc":"2.0","id":8,"method":"shutdown"}""");

        var error = responses.FirstOrDefault(r => r["id"]?.GetValue<int>() == 7 && r.ContainsKey("error"));
        Assert.IsNotNull(error, "Error response expected for malformed params with id");
        Assert.AreEqual(-32603, error["error"]!["code"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task HandleMessage_MalformedParams_NoId_NoErrorResponse()
    {
        // Same malformed params but without an "id" → error is only logged, no response sent.
        var responses = await RunServerAsync(
            """{"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":"file:///project/.dolphin/rules.yaml"}}}""",
            """{"jsonrpc":"2.0","id":1,"method":"shutdown"}""");

        // No error response (id was absent); only shutdown response arrives.
        var error = responses.FirstOrDefault(r => r.ContainsKey("error"));
        Assert.IsNull(error);
        Assert.AreEqual(1, responses.Count);
    }

    // ── Program.cs routing (via Startup.RunAsync) ─────────────────────────────

    [TestMethod]
    public async Task Startup_LspArg_RoutesToLspServer()
    {
        // Startup.RunAsync(["lsp"], ...) must delegate to LspServer, which reads from
        // the injected stream. Sending shutdown as the only message exercises the routing.
        var input = new MemoryStream(BuildInput("""{"jsonrpc":"2.0","id":1,"method":"shutdown"}"""));
        var output = new MemoryStream();

        await Startup.RunAsync(["lsp"], inputStream: input, outputStream: output);

        var responses = ParseOutput(output.ToArray());
        Assert.AreEqual(1, responses.Count, "Shutdown response expected via Startup → LspServer route");
        Assert.IsTrue(responses[0].ContainsKey("result"));
    }

    [TestMethod]
    public async Task Startup_CheckArg_RoutesToCli()
    {
        // A non-lsp, non-serve arg set should reach System.CommandLine without throwing.
        // `--help` exits cleanly with code 0 and writes to stdout.
        var task = Startup.RunAsync(["--help"]);
        var completed = await Task.WhenAny(task, Task.Delay(5000)) == task;
        Assert.IsTrue(completed, "RunAsync([\"--help\"]) must complete promptly — CLI routing hung or threw");
    }

    [GeneratedRegex(@"Content-Length:\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ContentLengthRegex();
}
