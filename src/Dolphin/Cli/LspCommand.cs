using System.CommandLine;
using Dolphin.Lsp;

namespace Dolphin.Cli;

public static class LspCommand
{
    public static Command Build()
    {
        var stdioOption = new Option<bool>(
            "--stdio",
            description: "Communicate over stdio (required for editor integration)"
        );

        var cmd = new Command("lsp", "Start the OpenGrep rules YAML language server")
        {
            stdioOption
        };

        cmd.SetHandler(async (stdio) =>
        {
            if (!stdio)
            {
                Console.Error.WriteLine("Usage: dolphin lsp --stdio");
                Console.Error.WriteLine("The --stdio flag is required. Configure your editor to invoke: dolphin lsp --stdio");
                Environment.Exit(2);
                return;
            }

            await LspServer.RunAsync();
        }, stdioOption);

        return cmd;
    }
}
