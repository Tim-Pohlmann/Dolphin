using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dolphin;
using Dolphin.Lsp;
using Dolphin.Scanner;
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
            int rel = bytes.AsSpan(pos).IndexOf("\r\n\r\n"u8);
            int headerEnd = rel < 0 ? -1 : pos + rel;
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
    public async Task RunAsync_HeaderWithNoContentLength_ClosesConnection()
    {
        // Missing Content-Length is a protocol error per LSP spec → close immediately.
        // Any subsequent messages are ignored because the connection closes.
        var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("X-Unknown: whatever\r\n\r\n"));
        var shutdownJson = """{"jsonrpc":"2.0","id":1,"method":"shutdown"}""";
        var body = Encoding.UTF8.GetBytes(shutdownJson);
        ms.Write(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
        ms.Write(body);

        var output = new MemoryStream();
        await LspServer.RunAsync(inputStream: new MemoryStream(ms.ToArray()), outputStream: output);
        var responses = ParseOutput(output.ToArray());

        // No responses because connection closes on missing Content-Length, before shutdown is read.
        Assert.AreEqual(0, responses.Count);
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

        Assert.AreEqual(2, responses.Count);
        // First response: parse error for the invalid JSON body.
        Assert.IsTrue(responses[0].ContainsKey("error"));
        Assert.AreEqual(-32700, responses[0]["error"]!["code"]!.GetValue<int>());
        Assert.IsNull(responses[0]["id"]);
        // Second response: normal result for the shutdown request.
        Assert.IsTrue(responses[1].ContainsKey("result"));
    }

    // ── HandleMessageAsync dispatch paths ─────────────────────────────────────

    [TestMethod]
    public async Task HandleMessage_NoMethodProperty_WithId_ProducesInvalidRequestError()
    {
        // A JSON object with an "id" but no "method" field → Invalid Request error response.
        var responses = await RunServerAsync(
            """{"jsonrpc":"2.0","id":1,"params":{}}""",
            """{"jsonrpc":"2.0","id":2,"method":"shutdown"}""");

        Assert.AreEqual(2, responses.Count, "Invalid request and shutdown should both produce responses");
        // First response: -32600 Invalid Request for the message missing a 'method'.
        Assert.IsTrue(responses[0].ContainsKey("error"));
        Assert.AreEqual(-32600, responses[0]["error"]!["code"]!.GetValue<int>());
        Assert.AreEqual(1, responses[0]["id"]!.GetValue<int>());
        // Second response: normal result for the shutdown request.
        Assert.IsTrue(responses[1].ContainsKey("result"));
        Assert.AreEqual(2, responses[1]["id"]!.GetValue<int>());
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
    public async Task HandleMessage_Initialize_AdvertisesDiagnosticProvider()
    {
        // LSP 3.17 pull diagnostics: initialize must advertise diagnosticProvider so
        // capability-aware clients issue textDocument/diagnostic requests.
        var responses = await RunServerAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}""");

        Assert.AreEqual(1, responses.Count, "initialize must produce exactly one response");
        var provider = responses[0]["result"]?["capabilities"]?["diagnosticProvider"];
        Assert.IsNotNull(provider, "diagnosticProvider must be present in capabilities");
        Assert.IsFalse(provider["interFileDependencies"]?.GetValue<bool>());
        Assert.IsFalse(provider["workspaceDiagnostics"]?.GetValue<bool>());
    }

    [TestMethod]
    public async Task HandleMessage_PullDiagnostic_NonDolphinFile_ReturnsEmptyFullReport()
    {
        // Non-dolphin URIs never run validation: respond with an empty full report
        // so the client doesn't stall waiting and doesn't conflate with our rules file.
        var responses = await RunServerAsync(
            """{"jsonrpc":"2.0","id":42,"method":"textDocument/diagnostic","params":{"textDocument":{"uri":"file:///src/app.ts"}}}""",
            """{"jsonrpc":"2.0","id":99,"method":"shutdown"}""");

        var pull = responses.FirstOrDefault(r => r["id"]?.GetValue<int>() == 42);
        Assert.IsNotNull(pull, "Pull diagnostic request must produce a response");
        Assert.AreEqual("full", pull["result"]?["kind"]?.GetValue<string>());
        Assert.AreEqual(0, pull["result"]?["items"]?.AsArray().Count);
    }

    [TestMethod]
    public async Task HandleMessage_PullDiagnostic_DolphinFile_NonAsciiText_ReturnsDiagnostic()
    {
        // Non-ASCII fast path runs synchronously (no opengrep needed) so we can assert
        // on the returned diagnostic regardless of whether the scanner is installed.
        const string uri = "file:///project/.dolphin/rules.yaml";
        var openJson = $$$$"""{"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":"{{{{uri}}}}","languageId":"yaml","version":1,"text":"rules: []\n# \u2708"}}}""";
        var pullJson = $$$$"""{"jsonrpc":"2.0","id":7,"method":"textDocument/diagnostic","params":{"textDocument":{"uri":"{{{{uri}}}}"}}}""";

        var responses = await RunServerAsync(
            openJson,
            pullJson,
            """{"jsonrpc":"2.0","id":99,"method":"shutdown"}""");

        var pull = responses.FirstOrDefault(r => r["id"]?.GetValue<int>() == 7);
        Assert.IsNotNull(pull, "Pull diagnostic response with id=7 expected");
        Assert.AreEqual("full", pull["result"]?["kind"]?.GetValue<string>());
        var items = pull["result"]?["items"]?.AsArray();
        Assert.IsNotNull(items);
        Assert.AreEqual(1, items.Count, "Non-ASCII text must produce exactly one diagnostic");
        Assert.IsTrue(items[0]!["message"]!.GetValue<string>().Contains("Non-ASCII"));
    }

    [TestMethod]
    public async Task HandleMessage_PullDiagnostic_BeforeDidOpen_ReturnsServerCancelled()
    {
        // A pull request that arrives before any didOpen for the same Dolphin URI hits
        // the transient cache-miss path.  The server must respond with
        // ServerCancelled (-32802) and include data.retriggerRequest=true so that
        // conformant clients re-issue the pull once the cache is populated.
        var responses = await RunServerAsync(
            """{"jsonrpc":"2.0","id":5,"method":"textDocument/diagnostic","params":{"textDocument":{"uri":"file:///project/.dolphin/rules.yaml"}}}""",
            """{"jsonrpc":"2.0","id":99,"method":"shutdown"}""");

        var pull = responses.FirstOrDefault(r => r["id"]?.GetValue<int>() == 5);
        Assert.IsNotNull(pull, "Pull diagnostic must produce a response even without prior didOpen");
        var error = pull["error"];
        Assert.IsNotNull(error, "Response must be an error (ServerCancelled)");
        Assert.AreEqual(-32802, error["code"]?.GetValue<int>(),
            "Error code must be -32802 (ServerCancelled)");
        Assert.IsTrue(error["data"]?["retriggerRequest"]?.GetValue<bool>(),
            "retriggerRequest hint must be true");
    }

    [TestMethod]
    public async Task HandleMessage_PullDiagnostic_OversizedText_ReturnsNonRetriggering()
    {
        // A didOpen whose text exceeds MaxCachedTextBytes marks the URI permanently
        // uncacheable. A subsequent pull must return InternalError (-32603) with no
        // retriggerRequest hint — not ServerCancelled — so conformant clients don't retry.
        const string uri = "file:///project/.dolphin/rules.yaml";
        var oversizedText = new string('a', LspServer.MaxCachedTextBytes + 1);
        var openJson = $$$$"""{"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":"{{{{uri}}}}","languageId":"yaml","version":1,"text":"{{{{oversizedText}}}}"}}}""";
        var pullJson = $$$$"""{"jsonrpc":"2.0","id":8,"method":"textDocument/diagnostic","params":{"textDocument":{"uri":"{{{{uri}}}}"}}}""";

        var responses = await RunServerAsync(
            openJson,
            pullJson,
            """{"jsonrpc":"2.0","id":99,"method":"shutdown"}""");

        var pull = responses.FirstOrDefault(r => r["id"]?.GetValue<int>() == 8);
        Assert.IsNotNull(pull, "Pull diagnostic must produce a response for a permanently uncacheable URI");
        var error = pull["error"];
        Assert.IsNotNull(error, "Response must be an error (InternalError)");
        Assert.AreEqual(-32603, error["code"]?.GetValue<int>(), "Error code must be -32603 (InternalError)");
        Assert.IsFalse(error["data"]?["retriggerRequest"]?.GetValue<bool>() ?? false,
            "Must not set retriggerRequest=true for permanently uncacheable documents");
    }

    [TestMethod]
    public async Task HandleMessage_PullDiagnostic_CacheFull_ReturnsNonRetriggering()
    {
        // Filling the document cache to MaxCachedDocuments then opening a new URI marks
        // the extra URI as permanently uncacheable. Its pull diagnostic must return
        // InternalError (-32603) without retriggerRequest so clients don't retry forever.
        var fillMessages = Enumerable.Range(0, LspServer.MaxCachedDocuments)
            .Select(i => $$$$"""{"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":"file:///p{{{{i}}}}/.dolphin/rules.yaml","languageId":"yaml","version":1,"text":"rules: []"}}}""")
            .ToArray();
        const string extraUri = "file:///extra/.dolphin/rules.yaml";
        var extraOpen = $$$$"""{"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":"{{{{extraUri}}}}","languageId":"yaml","version":1,"text":"rules: []"}}}""";
        var pullJson = $$$$"""{"jsonrpc":"2.0","id":11,"method":"textDocument/diagnostic","params":{"textDocument":{"uri":"{{{{extraUri}}}}"}}}""";

        var all = fillMessages
            .Append(extraOpen)
            .Append(pullJson)
            .Append("""{"jsonrpc":"2.0","id":99,"method":"shutdown"}""")
            .ToArray();
        var responses = await RunServerAsync(all);

        var pull = responses.FirstOrDefault(r => r["id"]?.GetValue<int>() == 11);
        Assert.IsNotNull(pull, "Pull diagnostic must produce a response when cache is full");
        var error = pull["error"];
        Assert.IsNotNull(error, "Response must be an error (InternalError)");
        Assert.AreEqual(-32603, error["code"]?.GetValue<int>(), "Error code must be -32603 (InternalError)");
        Assert.IsFalse(error["data"]?["retriggerRequest"]?.GetValue<bool>() ?? false,
            "Must not set retriggerRequest=true for a cache-full refusal");
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
    public void Validate_SchemaInvalidYaml_ReturnsDiagnosticsWithLineAndCharacter()
    {
        // "something: value" is syntactically valid YAML but fails the Semgrep schema
        // (missing the required top-level "rules:" key), so YamlRuleValidator.Validate
        // returns at least one diagnostic. Testing the validator directly avoids a race
        // between the fire-and-forget ValidateAndPublishAsync task and the shutdown/EOF
        // sequence in LSP integration tests.
        var diagnostics = YamlRuleValidator.Validate("something: value");

        Assert.IsTrue(diagnostics.Length > 0, "Expected at least one diagnostic for schema-invalid YAML");
        // Each diagnostic must have valid range.start.line and range.start.character
        var first = diagnostics[0];
        Assert.IsTrue(first.Range.Start.Line >= 0, $"Expected line >= 0, got {first.Range.Start.Line}");
        Assert.IsTrue(first.Range.Start.Character >= 0, $"Expected character >= 0, got {first.Range.Start.Character}");
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

        // Use LastOrDefault: the async didOpen validation may publish a non-empty
        // diagnostics notification before didClose publishes the empty one.
        var publish = responses.LastOrDefault(r => r["method"]?.GetValue<string>() == "textDocument/publishDiagnostics");
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
        // Since id is present, an error response with code -32602 (Invalid params) must be sent.
        var responses = await RunServerAsync(
            """{"jsonrpc":"2.0","id":7,"method":"textDocument/didOpen","params":{"textDocument":{"uri":"file:///project/.dolphin/rules.yaml"}}}""",
            """{"jsonrpc":"2.0","id":8,"method":"shutdown"}""");

        var error = responses.FirstOrDefault(r => r["id"]?.GetValue<int>() == 7 && r.ContainsKey("error"));
        Assert.IsNotNull(error, "Error response expected for malformed params with id");
        Assert.AreEqual(-32602, error["error"]!["code"]!.GetValue<int>());
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

    [TestMethod]
    public async Task HandleMessage_MissingMethod_WithId_SendsInvalidRequestError()
    {
        // Object with an id but no "method" field → must reply with -32600 (Invalid Request).
        var responses = await RunServerAsync(
            """{"jsonrpc":"2.0","id":5,"result":"unexpected"}""",
            """{"jsonrpc":"2.0","id":6,"method":"shutdown"}""");

        var error = responses.FirstOrDefault(r => r["id"]?.GetValue<int>() == 5 && r.ContainsKey("error"));
        Assert.IsNotNull(error, "Error response expected when 'method' is absent and id is present");
        Assert.AreEqual(-32600, error["error"]!["code"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task HandleMessage_MissingMethod_NoId_NoErrorResponse()
    {
        // Object with no "method" and no id (pure notification-like garbage) → no response.
        var responses = await RunServerAsync(
            """{"jsonrpc":"2.0","result":"unexpected"}""",
            """{"jsonrpc":"2.0","id":1,"method":"shutdown"}""");

        var error = responses.FirstOrDefault(r => r.ContainsKey("error"));
        Assert.IsNull(error, "No error response expected when 'method' is absent and id is absent");
        Assert.AreEqual(1, responses.Count, "Only shutdown response expected");
    }

    [TestMethod]
    public async Task HandleMessage_InvalidMethodType_WithId_SendsInvalidRequestError()
    {
        // "method" is a number, not a string → should reply with -32600 (Invalid Request).
        var responses = await RunServerAsync(
            """{"jsonrpc":"2.0","id":10,"method":42}""",
            """{"jsonrpc":"2.0","id":11,"method":"shutdown"}""");

        var error = responses.FirstOrDefault(r => r["id"]?.GetValue<int>() == 10 && r.ContainsKey("error"));
        Assert.IsNotNull(error, "Error response expected for non-string method");
        Assert.AreEqual(-32600, error["error"]!["code"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task HandleMessage_UnknownMethod_WithId_SendsMethodNotFoundError()
    {
        // Unknown method with an id → should reply with -32601 (Method Not Found).
        var responses = await RunServerAsync(
            """{"jsonrpc":"2.0","id":20,"method":"unknown/method"}""",
            """{"jsonrpc":"2.0","id":21,"method":"shutdown"}""");

        var error = responses.FirstOrDefault(r => r["id"]?.GetValue<int>() == 20 && r.ContainsKey("error"));
        Assert.IsNotNull(error, "Error response expected for unknown method with id");
        Assert.AreEqual(-32601, error["error"]!["code"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task HandleMessage_UnknownMethod_NoId_NoErrorResponse()
    {
        // Unknown method without an id is a notification → per JSON-RPC 2.0 no response is sent.
        var responses = await RunServerAsync(
            """{"jsonrpc":"2.0","method":"unknown/notification"}""",
            """{"jsonrpc":"2.0","id":1,"method":"shutdown"}""");

        var error = responses.FirstOrDefault(r => r.ContainsKey("error"));
        Assert.IsNull(error, "No error response expected for unknown-method notification");
        Assert.AreEqual(1, responses.Count, "Only shutdown response expected");
    }

    [TestMethod]
    public async Task RunAsync_NonObjectMessage_SendsInvalidRequestError()
    {
        // JSON-RPC messages must be JSON objects. A JSON array (batch) is not supported.
        // The server must send -32600 (Invalid Request) with id: null and keep the loop running.
        var input = new MemoryStream(BuildInput(
            """[{"jsonrpc":"2.0","id":1,"method":"shutdown"}]""",
            """{"jsonrpc":"2.0","id":2,"method":"shutdown"}"""));
        var output = new MemoryStream();

        await LspServer.RunAsync(input, output);

        var responses = ParseOutput(output.ToArray());
        Assert.IsTrue(responses.Count >= 2, "Error response + shutdown response expected");
        var error = responses[0];
        Assert.IsTrue(error.ContainsKey("error"), "First response should be an error");
        Assert.AreEqual(-32600, error["error"]?["code"]?.GetValue<int>());
        Assert.IsTrue(error.ContainsKey("id"), "Error must have an id field");
        Assert.IsNull(error["id"]?.GetValue<object?>(), "id must be null for non-object messages");
    }

    [TestMethod]
    public async Task RunAsync_MethodNotString_SendsInvalidRequestError()
    {
        // "method" field must be a string, not a number or other type.
        var input = new MemoryStream(BuildInput(
            """{"jsonrpc":"2.0","id":1,"method":123}""",
            """{"jsonrpc":"2.0","id":2,"method":"shutdown"}"""));
        var output = new MemoryStream();

        await LspServer.RunAsync(input, output);

        var responses = ParseOutput(output.ToArray());
        Assert.IsTrue(responses.Count >= 2, "Error response + shutdown response expected");
        Assert.AreEqual(-32600, responses[0]["error"]?["code"]?.GetValue<int>());
        Assert.AreEqual(1, responses[0]["id"]?.GetValue<int>());
    }

    [TestMethod]
    public async Task RunAsync_UnknownMethod_SendsMethodNotFoundError()
    {
        var input = new MemoryStream(BuildInput(
            """{"jsonrpc":"2.0","id":1,"method":"unknown_method","params":{}}""",
            """{"jsonrpc":"2.0","id":2,"method":"shutdown"}"""));
        var output = new MemoryStream();

        await LspServer.RunAsync(input, output);

        var responses = ParseOutput(output.ToArray());
        Assert.IsTrue(responses.Count >= 2);
        Assert.AreEqual(-32601, responses[0]["error"]?["code"]?.GetValue<int>());
        Assert.AreEqual(1, responses[0]["id"]?.GetValue<int>());
    }

    [TestMethod]
    public async Task RunAsync_DidOpenInvalidParams_SendsInvalidParamsError()
    {
        // didOpen with missing required params should trigger InvalidOperationException (missing property).
        var input = new MemoryStream(BuildInput(
            """{"jsonrpc":"2.0","id":1,"method":"textDocument/didOpen","params":{}}""",
            """{"jsonrpc":"2.0","id":2,"method":"shutdown"}"""));
        var output = new MemoryStream();

        await LspServer.RunAsync(input, output);

        var responses = ParseOutput(output.ToArray());
        Assert.IsTrue(responses.Count >= 2);
        Assert.AreEqual(-32602, responses[0]["error"]?["code"]?.GetValue<int>());
        Assert.AreEqual(1, responses[0]["id"]?.GetValue<int>());
    }

    [TestMethod]
    public void FindNonAsciiDiagnostic_WithNonAsciiCharacter_ReturnsDiagnostic()
    {
        // Rules files must be ASCII-only. Verify the synchronous non-ASCII detection works.
        // We test this directly rather than through LSP protocol because validation is async/fire-and-forget.
        var textWithNonAscii = "rules: []\n# Comment with \u2708 emoji"; // ✈ is non-ASCII

        var diags = LspServer.FindNonAsciiDiagnostic(textWithNonAscii);

        Assert.IsNotNull(diags, "Non-ASCII text should produce a diagnostic array");
        Assert.AreEqual(1, diags.Length);
        Assert.IsTrue(diags[0].Message.Contains("Non-ASCII"), "Message should mention non-ASCII");
        Assert.IsTrue(diags[0].Message.Contains("U+2708"), "Message should include Unicode codepoint");
    }

    [TestMethod]
    public void FindNonAsciiDiagnostic_WithUnpairedSurrogate_DoesNotThrow()
    {
        // An unpaired high surrogate must not crash the server (new Rune(char) would throw).
        var text = "rules: []\n# " + "\uD800"; // lone high surrogate

        var diags = LspServer.FindNonAsciiDiagnostic(text);

        Assert.IsNotNull(diags, "Unpaired surrogate is non-ASCII and should produce a diagnostic");
        Assert.AreEqual(1, diags.Length);
        Assert.IsTrue(diags[0].Message.Contains("Non-ASCII"));
    }

    [TestMethod]
    public void FindNonAsciiDiagnostic_WithSurrogatePair_ReturnsDiagnostic()
    {
        // A valid surrogate pair (😀 = U+1F600 = \uD83D\uDE00) is non-ASCII and should
        // be decoded as U+1F600 rather than reported as two separate code units.
        var text = "rules: []\n# Emoji: \uD83D\uDE00";

        var diags = LspServer.FindNonAsciiDiagnostic(text);

        Assert.IsNotNull(diags, "Surrogate pair is non-ASCII and should produce a diagnostic");
        Assert.AreEqual(1, diags.Length);
        Assert.IsTrue(diags[0].Message.Contains("U+1F600"), $"Expected U+1F600 in message but got: {diags[0].Message}");
    }

    [TestMethod]
    public void FindNonAsciiDiagnostic_WithCrlfLineEnding_ReportsCorrectLine()
    {
        // CRLF (\r\n) must be counted as a single newline so that the line number
        // reported for a non-ASCII character on line 1 (0-based) is correct.
        var text = "rules: []\r\n# Non-ASCII: \u00E9"; // é is U+00E9

        var diags = LspServer.FindNonAsciiDiagnostic(text);

        Assert.IsNotNull(diags, "Non-ASCII text should produce a diagnostic");
        Assert.AreEqual(1, diags.Length, "Expected exactly one diagnostic");
        Assert.AreEqual(1, diags[0].Range.Start.Line, "Non-ASCII char is on line 1 (0-based) after one CRLF");
    }

    [TestMethod]
    public void FindNonAsciiDiagnostic_WithCrOnlyLineEnding_ReportsCorrectLine()
    {
        // Bare CR (\r, without following LF) must also count as a newline.
        var text = "rules: []\r# Non-ASCII: \u00E9";

        var diags = LspServer.FindNonAsciiDiagnostic(text);

        Assert.IsNotNull(diags, "Non-ASCII text should produce a diagnostic");
        Assert.AreEqual(1, diags.Length, "Expected exactly one diagnostic");
        Assert.AreEqual(1, diags[0].Range.Start.Line, "Non-ASCII char is on line 1 (0-based) after one CR");
    }

    // ── FindProjectRoot ───────────────────────────────────────────────────────

    [TestMethod]
    public void FindProjectRoot_ReturnsNull_WhenNoRulesFileExists()
    {
        // Use a fresh temp directory with no .dolphin folder — cross-platform safe.
        var tmpDir = Path.Combine(Path.GetTempPath(), $"dolphin-nrtest-{Guid.NewGuid()}");
        Directory.CreateDirectory(tmpDir);
        try
        {
            var result = LspServer.FindProjectRoot(Path.Combine(tmpDir, "src", "app.ts"));
            Assert.IsNull(result, "Expected null when no .dolphin/rules.yaml exists in any ancestor");
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [TestMethod]
    public void FindProjectRoot_FindsRulesYaml_InParentDirectory()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"dolphin-prtest-{Guid.NewGuid()}");
        var dolphinDir = Path.Combine(tmpDir, ".dolphin");
        var srcDir = Path.Combine(tmpDir, "src");
        Directory.CreateDirectory(dolphinDir);
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(dolphinDir, "rules.yaml"), "rules: []");
        var filePath = Path.Combine(srcDir, "app.ts");
        File.WriteAllText(filePath, "");
        try
        {
            var result = LspServer.FindProjectRoot(filePath);
            Assert.AreEqual(tmpDir, result, "FindProjectRoot must return the directory containing .dolphin/rules.yaml");
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    // ── Source file diagnostics (didOpen / didSave / pull) ────────────────────

    [TestMethod]
    public async Task HandleMessage_DidOpen_SourceFile_NoProjectRoot_NoPublishDiagnostics()
    {
        // A source file with no .dolphin/rules.yaml ancestor must not trigger a scan
        // or publish any diagnostics — the server must stay silent.
        var responses = await RunServerAsync(
            """{"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":"file:///src/app.ts","languageId":"typescript","version":1,"text":"console.log('hi')"}}}""",
            """{"jsonrpc":"2.0","id":1,"method":"shutdown"}""");

        var publish = responses.FirstOrDefault(r => r["method"]?.GetValue<string>() == "textDocument/publishDiagnostics");
        Assert.IsNull(publish, "No publishDiagnostics expected for a source file with no .dolphin/rules.yaml ancestor");
        Assert.AreEqual(1, responses.Count, "Only shutdown response expected");
    }

    [TestMethod]
    public async Task HandleMessage_DidSave_NonSourceFile_NoScan()
    {
        // didSave on a non-source URI (e.g. a dolphin rules file) is silently ignored
        // by HandleDidSave since IsDolphinRulesFile(uri) is true, not IsSourceFile.
        const string uri = "file:///project/.dolphin/rules.yaml";
        var responses = await RunServerAsync(
            $"{{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didSave\",\"params\":{{\"textDocument\":{{\"uri\":\"{uri}\"}}}}}}",
            """{"jsonrpc":"2.0","id":1,"method":"shutdown"}""");

        // No publishDiagnostics from the save; only shutdown response.
        Assert.AreEqual(1, responses.Count, "didSave on a dolphin rules file must not trigger a source scan");
    }

    [TestMethod]
    public async Task HandleMessage_DidSave_SourceFile_NoProjectRoot_NoScan()
    {
        // didSave on a source file with no .dolphin ancestor is a no-op.
        var responses = await RunServerAsync(
            """{"jsonrpc":"2.0","method":"textDocument/didSave","params":{"textDocument":{"uri":"file:///src/app.ts"}}}""",
            """{"jsonrpc":"2.0","id":1,"method":"shutdown"}""");

        Assert.AreEqual(1, responses.Count, "Only shutdown response expected");
    }

    [TestMethod]
    public async Task HandleMessage_DidClose_SourceFile_WithCachedDiagnostics_PublishesEmpty()
    {
        // didClose on a source file that has a cached scan result must clear the diagnostics.
        // We inject a cached entry directly to simulate a prior scan.
        const string uri = "file:///project/src/app.ts";

        // Seed the cache as if a scan had previously run and found diagnostics.
        var pos = new LspPosition(0, 0);
        var fakeDiag = new LspDiagnostic(new LspRange(pos, pos), 2, "dolphin", "test finding [rule]", false);
        LspServer.SetSourceFileDiagnosticsForTest(uri, [fakeDiag]);

        var responses = await RunServerAsync(
            $"{{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didClose\",\"params\":{{\"textDocument\":{{\"uri\":\"{uri}\"}}}}}}",
            """{"jsonrpc":"2.0","id":1,"method":"shutdown"}""");

        var publish = responses.FirstOrDefault(r => r["method"]?.GetValue<string>() == "textDocument/publishDiagnostics");
        Assert.IsNotNull(publish, "didClose must publish empty diagnostics to clear previously-published findings");
        Assert.AreEqual(uri, publish["params"]?["uri"]?.GetValue<string>());
        Assert.AreEqual(0, publish["params"]?["diagnostics"]?.AsArray().Count);
    }

    [TestMethod]
    public async Task HandleMessage_PullDiagnostic_SourceFile_NoProjectRoot_ReturnsEmptyFullReport()
    {
        // A pull for a source file with no .dolphin ancestor returns an empty full report
        // immediately — same behaviour as before this feature, just routed differently.
        var responses = await RunServerAsync(
            """{"jsonrpc":"2.0","id":42,"method":"textDocument/diagnostic","params":{"textDocument":{"uri":"file:///src/app.ts"}}}""",
            """{"jsonrpc":"2.0","id":99,"method":"shutdown"}""");

        var pull = responses.FirstOrDefault(r => r["id"]?.GetValue<int>() == 42);
        Assert.IsNotNull(pull, "Pull diagnostic must produce a response");
        Assert.AreEqual("full", pull["result"]?["kind"]?.GetValue<string>());
        Assert.AreEqual(0, pull["result"]?["items"]?.AsArray().Count);
    }

    [TestMethod]
    public async Task HandleMessage_PullDiagnostic_SourceFile_WithCachedResult_ReturnsImmediately()
    {
        // When a scan result is cached (from a prior didOpen/didSave push), the pull must
        // return it immediately as a full report without triggering ServerCancelled.
        const string uri = "file:///project/src/app.ts";

        var pos = new LspPosition(2, 4);
        var fakeDiag = new LspDiagnostic(new LspRange(pos, pos), 1, "dolphin", "bad [rule-id]", false);
        LspServer.SetSourceFileDiagnosticsForTest(uri, [fakeDiag]);

        var responses = await RunServerAsync(
            $"{{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"textDocument/diagnostic\",\"params\":{{\"textDocument\":{{\"uri\":\"{uri}\"}}}}}}",
            """{"jsonrpc":"2.0","id":99,"method":"shutdown"}""");

        var pull = responses.FirstOrDefault(r => r["id"]?.GetValue<int>() == 5);
        Assert.IsNotNull(pull, "Pull must produce a response");
        Assert.IsNull(pull["error"], "Must not be an error response when cache is populated");
        Assert.AreEqual("full", pull["result"]?["kind"]?.GetValue<string>());
        var items = pull["result"]?["items"]?.AsArray();
        Assert.IsNotNull(items);
        Assert.AreEqual(1, items.Count, "Cached finding must appear in pull response");
        Assert.IsTrue(items[0]!["message"]!.GetValue<string>().Contains("bad [rule-id]"));
    }

    [TestMethod]
    public async Task HandleMessage_PullDiagnostic_SourceFile_WithProjectRoot_NoCached_ReturnsServerCancelled()
    {
        // A pull for a source file whose .dolphin/rules.yaml ancestor EXISTS but has no
        // cached result yet must trigger a background scan and return ServerCancelled +
        // retriggerRequest so the client retries after the push lands.
        var tmpDir = Path.Combine(Path.GetTempPath(), $"dolphin-lsptest-{Guid.NewGuid()}");
        Directory.CreateDirectory(Path.Combine(tmpDir, ".dolphin"));
        File.WriteAllText(Path.Combine(tmpDir, ".dolphin", "rules.yaml"), "rules: []");
        var srcFile = Path.Combine(tmpDir, "app.ts");
        File.WriteAllText(srcFile, "");
        var uri = new Uri(srcFile).AbsoluteUri;
        try
        {
            var responses = await RunServerAsync(
                $"{{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"textDocument/diagnostic\",\"params\":{{\"textDocument\":{{\"uri\":\"{uri}\"}}}}}}",
                """{"jsonrpc":"2.0","id":99,"method":"shutdown"}""");

            var pull = responses.FirstOrDefault(r => r["id"]?.GetValue<int>() == 5);
            Assert.IsNotNull(pull, "Pull must produce a response");
            var error = pull["error"];
            Assert.IsNotNull(error, "Must be an error response (ServerCancelled) when project root exists but no cache");
            Assert.AreEqual(-32802, error["code"]?.GetValue<int>(), "Error code must be -32802 (ServerCancelled)");
            Assert.IsTrue(error["data"]?["retriggerRequest"]?.GetValue<bool>(), "retriggerRequest must be true");
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task HandleMessage_DidOpen_SourceFile_WithProjectRoot_StartsBackgroundScan()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"dolphin-lsptest-{Guid.NewGuid()}");
        Directory.CreateDirectory(Path.Combine(tmpDir, ".dolphin"));
        File.WriteAllText(Path.Combine(tmpDir, ".dolphin", "rules.yaml"), "rules: []");
        var srcFile = Path.Combine(tmpDir, "app.ts");
        File.WriteAllText(srcFile, "");
        var uri = new Uri(srcFile).AbsoluteUri;
        try
        {
            var responses = await RunServerAsync(
                $"{{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{{\"textDocument\":{{\"uri\":\"{uri}\",\"languageId\":\"typescript\",\"version\":1,\"text\":\"\"}}}}}}",
                """{"jsonrpc":"2.0","id":1,"method":"shutdown"}""");

            Assert.IsTrue(responses.Any(r => r["id"]?.GetValue<int>() == 1), "Shutdown response expected");
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task HandleMessage_DidSave_SourceFile_WithProjectRoot_StartsBackgroundScan()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"dolphin-lsptest-{Guid.NewGuid()}");
        Directory.CreateDirectory(Path.Combine(tmpDir, ".dolphin"));
        File.WriteAllText(Path.Combine(tmpDir, ".dolphin", "rules.yaml"), "rules: []");
        var srcFile = Path.Combine(tmpDir, "app.ts");
        File.WriteAllText(srcFile, "");
        var uri = new Uri(srcFile).AbsoluteUri;
        try
        {
            var responses = await RunServerAsync(
                $"{{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didSave\",\"params\":{{\"textDocument\":{{\"uri\":\"{uri}\"}}}}}}",
                """{"jsonrpc":"2.0","id":1,"method":"shutdown"}""");

            Assert.IsTrue(responses.Any(r => r["id"]?.GetValue<int>() == 1), "Shutdown response expected");
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task HandleDiagnosticPull_NonRules_NonSource_ReturnsEmptyFullReport()
    {
        // A pull for a URI that is neither a dolphin rules file nor a source file
        // (non-file scheme) must return an empty full report immediately.
        var responses = await RunServerAsync(
            """{"jsonrpc":"2.0","id":7,"method":"textDocument/diagnostic","params":{"textDocument":{"uri":"untitled://newfile"}}}""",
            """{"jsonrpc":"2.0","id":99,"method":"shutdown"}""");

        var pull = responses.FirstOrDefault(r => r["id"]?.GetValue<int>() == 7);
        Assert.IsNotNull(pull, "Pull must produce a response");
        Assert.AreEqual("full", pull["result"]?["kind"]?.GetValue<string>());
        Assert.AreEqual(0, pull["result"]?["items"]?.AsArray().Count);
    }

    // ── ConvertFindingsToDiagnostics ──────────────────────────────────────────

    [TestMethod]
    public void ConvertFindingsToDiagnostics_EmptyList_ReturnsEmpty()
    {
        var result = LspServer.ConvertFindingsToDiagnostics([], "/project/src/app.ts", "/project");
        Assert.AreEqual(0, result.Length);
    }

    [TestMethod]
    public void ConvertFindingsToDiagnostics_FiltersOutFindingsForOtherFiles()
    {
        var findings = new List<Finding>
        {
            new("rule-a", Severity.Warning, "src/other.ts", 1, 1, "msg", ""),
        };
        var result = LspServer.ConvertFindingsToDiagnostics(findings, "/project/src/app.ts", "/project");
        Assert.AreEqual(0, result.Length, "Findings for other files must be filtered out");
    }

    [TestMethod]
    public void ConvertFindingsToDiagnostics_MapsServerityCorrectly()
    {
        var projectRoot = "/project";
        var filePath = "/project/src/app.ts";
        var findings = new List<Finding>
        {
            new("rule-err",  Severity.Error,   "src/app.ts", 1, 1, "error msg",   ""),
            new("rule-warn", Severity.Warning,  "src/app.ts", 2, 1, "warning msg", ""),
            new("rule-info", Severity.Info,     "src/app.ts", 3, 1, "info msg",    ""),
        };
        var result = LspServer.ConvertFindingsToDiagnostics(findings, filePath, projectRoot);

        Assert.AreEqual(3, result.Length);
        Assert.AreEqual(1, result[0].Severity, "Error → LSP severity 1");
        Assert.AreEqual(2, result[1].Severity, "Warning → LSP severity 2");
        Assert.AreEqual(3, result[2].Severity, "Info → LSP severity 3");
    }

    [TestMethod]
    public void ConvertFindingsToDiagnostics_ConvertsOneBasedToZeroBased()
    {
        var projectRoot = "/project";
        var filePath = "/project/src/app.ts";
        var findings = new List<Finding>
        {
            new("rule-x", Severity.Warning, "src/app.ts", 5, 3, "msg", ""),
        };
        var result = LspServer.ConvertFindingsToDiagnostics(findings, filePath, projectRoot);

        Assert.AreEqual(1, result.Length);
        // Opengrep 1-based → LSP 0-based: line 5 → 4, col 3 → 2
        Assert.AreEqual(4, result[0].Range.Start.Line);
        Assert.AreEqual(2, result[0].Range.Start.Character);
    }

    [TestMethod]
    public void ConvertFindingsToDiagnostics_FormatsMessageWithRuleId()
    {
        var projectRoot = "/project";
        var filePath = "/project/src/app.ts";
        var findings = new List<Finding>
        {
            new("my-rule", Severity.Warning, "src/app.ts", 1, 1, "do not do this", ""),
        };
        var result = LspServer.ConvertFindingsToDiagnostics(findings, filePath, projectRoot);

        Assert.AreEqual(1, result.Length);
        Assert.AreEqual("do not do this [my-rule]", result[0].Message);
        Assert.AreEqual("dolphin", result[0].Source);
    }

    [TestMethod]
    public void ConvertFindingsToDiagnostics_ClampsBelowZeroToZero()
    {
        // Line/col of 0 in a finding (malformed) must not produce negative LSP positions.
        var findings = new List<Finding>
        {
            new("rule-x", Severity.Warning, "src/app.ts", 0, 0, "msg", ""),
        };
        var result = LspServer.ConvertFindingsToDiagnostics(findings, "/project/src/app.ts", "/project");

        Assert.AreEqual(1, result.Length);
        Assert.AreEqual(0, result[0].Range.Start.Line);
        Assert.AreEqual(0, result[0].Range.Start.Character);
    }

    // ── StripAnsi ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void StripAnsi_RemovesColorCodes()
    {
        const string input = "\x1B[33mWARNING\x1B[0m: invalid rule";
        Assert.AreEqual("WARNING: invalid rule", LspServer.StripAnsi(input));
    }

    [TestMethod]
    public void StripAnsi_NoEscapeCodes_ReturnsUnchanged()
    {
        const string input = "plain text without escapes";
        Assert.AreEqual(input, LspServer.StripAnsi(input));
    }

    [TestMethod]
    public void StripAnsi_MultipleSequences_RemovesAll()
    {
        const string input = "\x1B[31merror\x1B[0m: \x1B[1mbold\x1B[0m message";
        Assert.AreEqual("error: bold message", LspServer.StripAnsi(input));
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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var exitCode = await Startup.RunAsync(["--help"]).WaitAsync(cts.Token);
            Assert.AreEqual(0, exitCode, "\"--help\" must exit with code 0");
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("RunAsync([\"--help\"]) must complete promptly — CLI routing hung or threw");
        }
    }

    [GeneratedRegex(@"Content-Length:\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ContentLengthRegex();
}
