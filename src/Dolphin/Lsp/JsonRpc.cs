using System.Text;
using System.Text.Json.Nodes;

namespace Dolphin.Lsp;

/// <summary>
/// Reads and writes LSP/JSON-RPC 2.0 messages with Content-Length framing over stdio.
/// All logging goes to stderr; stdout is reserved for JSON-RPC responses.
/// </summary>
internal static class JsonRpc
{
    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    /// <summary>Reads one JSON-RPC message from the stream. Returns null on EOF.</summary>
    public static async Task<JsonObject?> ReadAsync(Stream input, CancellationToken ct)
    {
        int contentLength = -1;

        // Read HTTP-style headers terminated by a blank line
        while (true)
        {
            string? line = await ReadLineAsync(input, ct);
            if (line is null) return null; // EOF

            if (line.Length == 0) break; // blank line = end of headers

            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line["Content-Length:".Length..].Trim(), out int len))
            {
                contentLength = len;
            }
        }

        if (contentLength <= 0) return null;

        var body = new byte[contentLength];
        int offset = 0;
        while (offset < contentLength)
        {
            int n = await input.ReadAsync(body.AsMemory(offset, contentLength - offset), ct);
            if (n == 0) return null; // unexpected EOF in body
            offset += n;
        }

        try { return JsonNode.Parse(body)?.AsObject(); }
        catch { return null; }
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var buf = new List<byte>(64);
        var one = new byte[1];

        while (true)
        {
            int n = await stream.ReadAsync(one.AsMemory(0, 1), ct);
            if (n == 0) return null; // EOF

            if (one[0] == (byte)'\n')
            {
                if (buf.Count > 0 && buf[^1] == (byte)'\r')
                    buf.RemoveAt(buf.Count - 1);
                return Encoding.UTF8.GetString([.. buf]);
            }

            buf.Add(one[0]);
        }
    }

    /// <summary>Writes a JSON-RPC message with Content-Length framing. Thread-safe.</summary>
    public static async Task WriteAsync(Stream output, JsonObject message, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
        var header = Encoding.UTF8.GetBytes($"Content-Length: {body.Length}\r\n\r\n");

        await WriteLock.WaitAsync(ct);
        try
        {
            await output.WriteAsync(header, ct);
            await output.WriteAsync(body, ct);
            await output.FlushAsync(ct);
        }
        finally { WriteLock.Release(); }
    }

    /// <summary>Sends a successful response to a request.</summary>
    public static Task RespondAsync(Stream output, JsonNode? id, JsonNode? result, CancellationToken ct) =>
        WriteAsync(output, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result
        }, ct);

    /// <summary>Sends a server-to-client notification (no id, no response expected).</summary>
    public static Task NotifyAsync(Stream output, string method, JsonNode? @params, CancellationToken ct) =>
        WriteAsync(output, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = @params
        }, ct);

    /// <summary>Sends an error response to a request.</summary>
    public static Task RespondErrorAsync(Stream output, JsonNode? id, int code, string message, CancellationToken ct) =>
        WriteAsync(output, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message
            }
        }, ct);
}
