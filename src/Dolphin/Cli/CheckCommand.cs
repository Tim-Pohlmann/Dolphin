using System.CommandLine;
using Dolphin.Output;
using Dolphin.Scanner;

namespace Dolphin.Cli;

public static class CheckCommand
{
    public static Command Build()
    {
        var cwdOption = new Option<string>(
            "--cwd",
            description: "Project root directory to scan",
            getDefaultValue: () => Directory.GetCurrentDirectory()
        );

        var ruleOption = new Option<string?>(
            "--rule",
            description: "Run only the rule with this ID"
        );

        var formatOption = new Option<string>(
            "--format",
            description: "Output format: text or json",
            getDefaultValue: () => "text"
        );

        var cmd = new Command("check", "Run static analysis rules against the codebase")
        {
            cwdOption,
            ruleOption,
            formatOption
        };

        cmd.SetHandler(async (cwd, ruleId, format) =>
        {
            // Resolve and validate cwd
            cwd = Path.GetFullPath(cwd);
            if (!Directory.Exists(cwd))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"Directory not found: {cwd}");
                Console.ResetColor();
                Environment.Exit(2);
                return;
            }

            // Locate scanner binary (bundled next to dolphin, or on PATH for dev builds)
            string scannerBinary;
            try
            {
                scannerBinary = await Installer.EnsureInstalledAsync();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"Failed to locate scanner: {ex.Message}");
                Console.ResetColor();
                Environment.Exit(2);
                return;
            }

            // Run analysis
            RunResult result;
            try
            {
                result = await Runner.RunAsync(scannerBinary, cwd, ruleId);
            }
            catch (FileNotFoundException ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Error.WriteLine(ex.Message);
                Console.ResetColor();
                Environment.Exit(2);
                return;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"Analysis failed: {ex.Message}");
                Console.ResetColor();
                Environment.Exit(2);
                return;
            }

            if (result.ScannerWarning != null)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Error.WriteLine($"Warning: {result.ScannerWarning}");
                Console.ResetColor();
            }

            Formatter.Print(result.Findings, format);

            // Exit 1 if any ERROR-severity findings, 0 otherwise
            var hasErrors = result.Findings.Any(f => f.Severity == Severity.Error);
            Environment.Exit(hasErrors ? 1 : 0);

        }, cwdOption, ruleOption, formatOption);

        return cmd;
    }
}
