using System.CommandLine;
using Dolphin.Semgrep;

namespace Dolphin.Cli;

public static class SetupCommand
{
    public static Command Build()
    {
        var cmd = new Command("setup", "Download and verify the Semgrep analysis engine");

        cmd.SetHandler(async () =>
        {
            Console.WriteLine("Setting up Semgrep...");

            var progress = new Progress<string>(msg =>
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"  {msg}");
                Console.ResetColor();
            });

            try
            {
                var (binary, version) = await Installer.GetInstalledInfoAsync();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"✓ Semgrep {version}");
                Console.ResetColor();
                Console.WriteLine($"  Binary: {binary}");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"Setup failed: {ex.Message}");
                Console.ResetColor();
                Environment.Exit(2);
            }
        });

        return cmd;
    }
}
