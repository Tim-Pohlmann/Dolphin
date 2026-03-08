using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Dolphin.Tests;

/// <summary>
/// Smoke tests for the MCP server's JSON-RPC stdio protocol.
/// Spawns `dolphin serve --stdio` as a child process and speaks the
/// MCP wire protocol directly — no Claude client required.
/// </summary>
[TestClass]
public class McpProtocolTests
{
    private static Process StartServer()
    {
        var projectPath = TestProcessHelper.FindDolphinProjectPath();
        var config = TestProcessHelper.CurrentConfiguration();
        var psi = new ProcessStartInfo("dotnet", $"run --no-build --configuration {config} --project \"{projectPath}\" -- serve --stdio")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        return Process.Start(psi)!;
    }

    /// <summary>Writes a JSON-RPC message as a single line to the server's stdin.</summary>
    private static void Send(Process proc, string json)
    {
        proc.StandardInput.WriteLine(json);
        proc.StandardInput.Flush();
    }

    /// <summary>
    /// Reads lines from stdout until a response (a message with an "id" field) is found.
    /// Notifications (no "id") are silently skipped.
    /// </summary>
    private static async Task<JsonObject> ReceiveAsync(Process proc, CancellationToken ct)
    {
        while (true)
        {
            var line = await proc.StandardOutput.ReadLineAsync(ct);
            if (line == null)
                throw new EndOfStreamException("MCP server closed stdout unexpectedly");

            if (string.IsNullOrWhiteSpace(line))
                continue;

            var node = JsonNode.Parse(line);
            if (node is JsonObject obj && obj.ContainsKey("id"))
                return obj;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task McpServer_ToolsList_ContainsRunCheckWithCwdParameter()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var proc = StartServer();
        try
        {
            // 1. Initialize
            Send(proc, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}""");
            await ReceiveAsync(proc, cts.Token);

            // 2. Confirm initialization
            Send(proc, """{"jsonrpc":"2.0","method":"notifications/initialized"}""");

            // 3. List tools
            Send(proc, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
            var response = await ReceiveAsync(proc, cts.Token);

            Assert.IsNull(response["error"]);

            var tools = response["result"]?["tools"]?.AsArray();
            Assert.IsNotNull(tools);

            var runCheck = tools.FirstOrDefault(t => t?["name"]?.GetValue<string>() == "run_check");
            Assert.IsNotNull(runCheck, "run_check tool not found in tools/list response");

            // Verify the required "cwd" parameter is declared in the input schema
            var properties = runCheck["inputSchema"]?["properties"];
            Assert.IsNotNull(properties);
            Assert.IsNotNull(properties["cwd"], "run_check schema missing required 'cwd' property");
        }
        finally
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            using var killCts = new CancellationTokenSource(5000);
            try { await proc.WaitForExitAsync(killCts.Token); } catch { }
        }
    }

    [TestMethod]
    public async Task McpServer_ToolCall_RunCheck_ReturnsErrorStringForInvalidDirectory()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var proc = StartServer();
        try
        {
            // Initialize
            Send(proc, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}""");
            await ReceiveAsync(proc, cts.Token);
            Send(proc, """{"jsonrpc":"2.0","method":"notifications/initialized"}""");

            // Call run_check with a path that does not exist
            Send(proc, """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"run_check","arguments":{"cwd":"/nonexistent/dolphin-mcp-test-path"}}}""");
            var response = await ReceiveAsync(proc, cts.Token);

            // The tool returns errors as text content, not JSON-RPC errors
            Assert.IsNull(response["error"]);

            var content = response["result"]?["content"]?.AsArray();
            Assert.IsNotNull(content);
            Assert.IsTrue(content.Count > 0);

            var text = content[0]?["text"]?.GetValue<string>();
            Assert.IsNotNull(text);
            StringAssert.StartsWith(text, "Error:");
        }
        finally
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            using var killCts = new CancellationTokenSource(5000);
            try { await proc.WaitForExitAsync(killCts.Token); } catch { }
        }
    }
}
