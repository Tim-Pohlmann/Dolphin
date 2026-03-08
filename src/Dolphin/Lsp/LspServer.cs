using System.Text.Json.Nodes;

namespace Dolphin.Lsp;

/// <summary>
/// LSP server for OpenGrep rules YAML files.
///
/// Implements the Language Server Protocol over stdio (Content-Length–framed JSON-RPC 2.0).
///
/// Features:
///   • Diagnostics — validates rule structure on open/change and pushes errors/warnings
///   • Completions — field names, severity values, language identifiers, pattern operators
///   • Hover — inline documentation for rule fields
///
/// Invoke as: <c>dolphin lsp --stdio</c>
/// </summary>
public static class LspServer
{
    // In-memory store of open document contents, keyed by LSP document URI.
    private static readonly Dictionary<string, string> Docs = new(StringComparer.Ordinal);
    private static readonly Lock DocsLock = new();

    public static async Task RunAsync()
    {
        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();

        await Console.Error.WriteLineAsync("[dolphin-lsp] Language server ready");

        bool shuttingDown = false;
        using var cts = new CancellationTokenSource();

        while (!cts.IsCancellationRequested)
        {
            JsonObject? msg;
            try
            {
                msg = await JsonRpc.ReadAsync(stdin, cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"[dolphin-lsp] read error: {ex.Message}");
                break;
            }

            if (msg is null) break; // EOF — client closed the connection

            var method = msg["method"]?.GetValue<string>() ?? "";
            var id = msg["id"];
            var @params = msg["params"]?.AsObject();

            try
            {
                switch (method)
                {
                    // ── Lifecycle ──────────────────────────────────────────────────
                    case "initialize":
                        await HandleInitializeAsync(stdout, id, cts.Token);
                        break;

                    case "initialized":
                        break; // notification, no response

                    case "shutdown":
                        shuttingDown = true;
                        await JsonRpc.RespondAsync(stdout, id, null, cts.Token);
                        break;

                    case "exit":
                        cts.Cancel();
                        // Per spec: exit code 0 after normal shutdown, 1 if no shutdown first
                        Environment.Exit(shuttingDown ? 0 : 1);
                        return;

                    // ── Text document sync ────────────────────────────────────────
                    case "textDocument/didOpen":
                        await HandleDidOpenAsync(stdout, @params, cts.Token);
                        break;

                    case "textDocument/didChange":
                        await HandleDidChangeAsync(stdout, @params, cts.Token);
                        break;

                    case "textDocument/didClose":
                        HandleDidClose(@params);
                        break;

                    // ── Language features ─────────────────────────────────────────
                    case "textDocument/completion":
                        await HandleCompletionAsync(stdout, id, @params, cts.Token);
                        break;

                    case "textDocument/hover":
                        await HandleHoverAsync(stdout, id, @params, cts.Token);
                        break;

                    // ── Ignored ───────────────────────────────────────────────────
                    case "$/cancelRequest":
                    case "$/setTrace":
                    case "$/logTrace":
                        break;

                    default:
                        // Respond with MethodNotFound for unknown requests (those with an id)
                        if (id is not null)
                            await JsonRpc.RespondErrorAsync(stdout, id, -32601,
                                $"Method not found: {method}", cts.Token);
                        break;
                }
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"[dolphin-lsp] error in {method}: {ex.Message}");
                if (id is not null)
                    await JsonRpc.RespondErrorAsync(stdout, id, -32603, ex.Message, cts.Token);
            }
        }
    }

    // ── Handlers ─────────────────────────────────────────────────────────────────

    private static async Task HandleInitializeAsync(Stream output, JsonNode? id, CancellationToken ct)
    {
        var result = new JsonObject
        {
            ["capabilities"] = new JsonObject
            {
                // Full document sync: client sends complete text on every change
                ["textDocumentSync"] = new JsonObject
                {
                    ["openClose"] = true,
                    ["change"] = 1
                },
                // Completions triggered by space, colon, dash, or open bracket
                ["completionProvider"] = new JsonObject
                {
                    ["triggerCharacters"] = new JsonArray(" ", ":", "-", "[")
                },
                ["hoverProvider"] = true
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "dolphin-lsp",
                ["version"] = "0.1.0"
            }
        };

        await JsonRpc.RespondAsync(output, id, result, ct);
    }

    private static async Task HandleDidOpenAsync(Stream output, JsonObject? @params, CancellationToken ct)
    {
        var uri = @params?["textDocument"]?["uri"]?.GetValue<string>();
        var text = @params?["textDocument"]?["text"]?.GetValue<string>();
        if (uri is null || text is null) return;

        lock (DocsLock) Docs[uri] = text;
        await PushDiagnosticsAsync(output, uri, text, ct);
    }

    private static async Task HandleDidChangeAsync(Stream output, JsonObject? @params, CancellationToken ct)
    {
        var uri = @params?["textDocument"]?["uri"]?.GetValue<string>();
        var changes = @params?["contentChanges"]?.AsArray();
        if (uri is null || changes is null) return;

        // Full sync: the last change event contains the complete new text
        string? text = null;
        foreach (var change in changes)
            text = change?["text"]?.GetValue<string>() ?? text;
        if (text is null) return;

        lock (DocsLock) Docs[uri] = text;
        await PushDiagnosticsAsync(output, uri, text, ct);
    }

    private static void HandleDidClose(JsonObject? @params)
    {
        var uri = @params?["textDocument"]?["uri"]?.GetValue<string>();
        if (uri is null) return;
        lock (DocsLock) Docs.Remove(uri);
    }

    private static async Task HandleCompletionAsync(
        Stream output, JsonNode? id, JsonObject? @params, CancellationToken ct)
    {
        var uri = @params?["textDocument"]?["uri"]?.GetValue<string>();
        var line = @params?["position"]?["line"]?.GetValue<int>() ?? 0;
        var character = @params?["position"]?["character"]?.GetValue<int>() ?? 0;

        string? content = null;
        if (uri is not null)
            lock (DocsLock) Docs.TryGetValue(uri, out content);

        var result = content is not null
            ? RulesYamlAnalyzer.GetCompletions(content, line, character)
            : (JsonNode)new JsonObject { ["isIncomplete"] = false, ["items"] = new JsonArray() };

        await JsonRpc.RespondAsync(output, id, result, ct);
    }

    private static async Task HandleHoverAsync(
        Stream output, JsonNode? id, JsonObject? @params, CancellationToken ct)
    {
        var uri = @params?["textDocument"]?["uri"]?.GetValue<string>();
        var line = @params?["position"]?["line"]?.GetValue<int>() ?? 0;
        var character = @params?["position"]?["character"]?.GetValue<int>() ?? 0;

        string? content = null;
        if (uri is not null)
            lock (DocsLock) Docs.TryGetValue(uri, out content);

        var result = content is not null
            ? RulesYamlAnalyzer.GetHover(content, line, character)
            : null;

        await JsonRpc.RespondAsync(output, id, result, ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private static async Task PushDiagnosticsAsync(
        Stream output, string uri, string content, CancellationToken ct)
    {
        var items = RulesYamlAnalyzer.Validate(content);
        var arr = new JsonArray();

        foreach (var d in items)
        {
            arr.Add(new JsonObject
            {
                ["range"] = Range(d.StartLine, d.StartCol, d.EndLine, d.EndCol),
                ["severity"] = d.Severity,
                ["code"] = d.Code,
                ["source"] = "dolphin",
                ["message"] = d.Message
            });
        }

        await JsonRpc.NotifyAsync(output, "textDocument/publishDiagnostics", new JsonObject
        {
            ["uri"] = uri,
            ["diagnostics"] = arr
        }, ct);
    }

    private static JsonObject Range(int sl, int sc, int el, int ec) => new()
    {
        ["start"] = new JsonObject { ["line"] = sl, ["character"] = sc },
        ["end"] = new JsonObject { ["line"] = el, ["character"] = ec }
    };
}
