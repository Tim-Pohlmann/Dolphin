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

        var failOnOption = new Option<string>(
            "--fail-on",
            description: "Lowest severity that causes a non-zero exit: error, warning, or info",
            getDefaultValue: () => "error"
        );
        failOnOption.FromAmong("error", "warning", "info");

        var cmd = new Command("check", "Run static analysis rules against the codebase")
        {
            cwdOption,
            ruleOption,
            formatOption,
            fileOption,
            failOnOption
        };

        cmd.SetHandler(
            async (cwd, ruleId, format, file, failOn) =>
                Environment.Exit(await HandleAsync(cwd, ruleId, format, file, failOn)),
            cwdOption, ruleOption, formatOption, fileOption, failOnOption);

        return cmd;
    }

    internal static async Task<int> HandleAsync(
        string cwd, string? ruleId, string format, string? file, string failOn = "error")
    {
        cwd = Path.GetFullPath(cwd);
        if (!Directory.Exists(cwd))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            await Console.Error.WriteLineAsync($"Directory not found: {cwd}");
            Console.ResetColor();
            return 2;
        }

        if (file != null)
        {
            if (!Path.IsPathRooted(file))
                file = Path.GetFullPath(file, cwd);
            if (!File.Exists(file))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                await Console.Error.WriteLineAsync($"File not found: {file}");
                Console.ResetColor();
                return 2;
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
            await Console.Error.WriteLineAsync($"Failed to locate scanner: {ex.Message}");
            Console.ResetColor();
            return 2;
        }

        RunResult result;
        try
        {
            result = await Runner.RunAsync(scannerBinary, cwd, ruleId, targetFile: file);
        }
        catch (FileNotFoundException ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            await Console.Error.WriteLineAsync(ex.Message);
            Console.ResetColor();
            return 2;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            await Console.Error.WriteLineAsync($"Analysis failed: {ex.Message}");
            Console.ResetColor();
            return 2;
        }

        if (result.ScannerWarning != null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            await Console.Error.WriteLineAsync($"Warning: {result.ScannerWarning}");
            Console.ResetColor();
        }

        Formatter.Print(result.Findings, format);

        // Lower enum value == higher severity (Error=0, Warning=1, Info=2).
        // Fail if any finding is at least as severe as the configured threshold.
        var threshold = failOn switch
        {
            "warning" => Severity.Warning,
            "info" => Severity.Info,
            _ => Severity.Error
        };

        return result.Findings.Any(f => f.Severity <= threshold) ? 1 : 0;
    }
}
