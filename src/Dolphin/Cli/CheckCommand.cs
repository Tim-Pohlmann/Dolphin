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
            description: "Output format: text, json, or github",
            getDefaultValue: () => "text"
        );
        formatOption.FromAmong("text", "json", "github");

        var fileOption = new Option<string?>(
            "--file",
            description: "Scan only this file instead of the entire project"
        );

        var cmd = new Command("check", "Run static analysis rules against the codebase")
        {
            cwdOption,
            ruleOption,
            formatOption,
            fileOption
        };

        cmd.SetHandler(HandleAsync, cwdOption, ruleOption, formatOption, fileOption);

        return cmd;
    }

    private static async Task HandleAsync(string cwd, string? ruleId, string format, string? file)
    {
        cwd = Path.GetFullPath(cwd);
        if (!Directory.Exists(cwd))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"Directory not found: {cwd}");
            Console.ResetColor();
            Environment.Exit(2);
            return;
        }

        if (file != null)
        {
            if (!Path.IsPathRooted(file))
                file = Path.GetFullPath(file, cwd);
            if (!File.Exists(file))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"File not found: {file}");
                Console.ResetColor();
                Environment.Exit(2);
                return;
            }
        }

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

        RunResult result;
        try
        {
            result = await Runner.RunAsync(scannerBinary, cwd, ruleId, targetFile: file);
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

        var hasErrors = result.Findings.Any(f => f.Severity == Severity.Error);
        Environment.Exit(hasErrors ? 1 : 0);
    }
}
