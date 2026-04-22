using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dolphin;
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
    public async Task RunAsync_OversizedHeader_ClosesConnectionAndLogsError()
    {
        // MaxHeaderBytes bytes of 'A' without \r\n\r\n → ReadHeaderAsync throws
        // InvalidDataException, caught by TryReadNextMessageAsync; loop exits cleanly.
        var oversizedHeader = new byte[LspServer.MaxHeaderBytes + 1];
        Array.Fill(oversizedHeader, (byte)'A');
        var input = new MemoryStream(oversizedHeader);
        var output = new MemoryStream();

        await LspServer.RunAsync(inputStream: input, outputStream: output);

        // Connection closes without producing any response messages.
        Assert.AreEqual(0, ParseOutput(output.ToArray()).Count,
            "No responses expected after oversized header triggers connection close");
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
        Assert.AreEqual(false, provider["interFileDependencies"]?.GetValue<bool>());
        Assert.AreEqual(false, provider["workspaceDiagnostics"]?.GetValue<bool>());
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
