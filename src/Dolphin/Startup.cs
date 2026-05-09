using System.CommandLine;
using Dolphin.Cli;
using Dolphin.Mcp;

namespace Dolphin;

internal static class Startup
{
    internal static async Task<int> RunAsync(string[] args, Stream? inputStream = null, Stream? outputStream = null)
    {
        if (args is ["serve", "--stdio"])
        {
            await McpServer.RunAsync();
            return 0;
        }

        var root = new RootCommand("Dolphin — custom static analysis powered by Opengrep")
        {
            CheckCommand.Build(),
            HookCommand.Build()
        };

        root.Name = "dolphin";
        return await root.InvokeAsync(args);
    }
}
