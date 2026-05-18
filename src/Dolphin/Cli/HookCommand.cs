using System.CommandLine;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dolphin.Scanner;

namespace Dolphin.Cli;

[JsonSerializable(typeof(string))]
internal partial class HookCommandJsonContext : JsonSerializerContext { }

public static class HookCommand
{
    public static Command Build()
    {
        var postToolUse = new Command("post-tool-use", "Handle post-tool-use hook events from Claude Code");
        postToolUse.SetHandler(() => HandlePostToolUseAsync(Console.OpenStandardInput()));

        var hook = new Command("hook", "Internal hook handlers for Claude Code integration");
        hook.AddCommand(postToolUse);
        return hook;
    }

    internal static async Task HandlePostToolUseAsync(Stream stdin)
    {
        string? filePath;
        try
        {
            using var doc = await JsonDocument.ParseAsync(stdin);
            if (!doc.RootElement.TryGetProperty("tool_input", out var input)) return;
            if (!input.TryGetProperty("file_path", out var fp)) return;
            if (fp.ValueKind != JsonValueKind.String) return;
            filePath = fp.GetString();
        }
        catch (JsonException) { return; }

        if (string.IsNullOrWhiteSpace(filePath)) return;

        if (IsRulesFile(filePath))
            await ValidateRulesFileAsync(filePath);
        else
            await CheckSourceFileAsync(filePath);
    }

    internal static bool IsRulesFile(string filePath)
    {
        var fileName  = Path.GetFileName(filePath);
        var dirPath   = Path.GetDirectoryName(filePath) ?? string.Empty;
        var parentDir = Path.GetFileName(dirPath);
        return parentDir.Equals(".dolphin", StringComparison.OrdinalIgnoreCase)
            && (fileName.Equals("rules.yaml", StringComparison.OrdinalIgnoreCase)
             || fileName.Equals("rules.yml",  StringComparison.OrdinalIgnoreCase));
    }

    private static async Task ValidateRulesFileAsync(string filePath)
    {
        string text;
        try { text = await File.ReadAllTextAsync(filePath); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        catch (ArgumentException) { return; }
        catch (NotSupportedException) { return; }

        var diagnostics = YamlRuleValidator.Validate(text);

        if (diagnostics.Length == 0) return;

        var displayName = Path.GetFileName(filePath);
        var sb = new StringBuilder();
        foreach (var d in diagnostics)
            sb.AppendLine($"{displayName}:{d.Range.Start.Line + 1}:{d.Range.Start.Character + 1}: {d.Message}");
        WriteHookContext(sb.ToString().TrimEnd());
    }

    private static async Task CheckSourceFileAsync(string filePath)
    {
        if (!File.Exists(filePath)) return;

        var dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir)) return;

        var cwd = FindProjectRoot(dir);
        if (cwd == null) return;

        string scannerBinary;
        try { scannerBinary = await Installer.EnsureInstalledAsync(); }
        catch { return; }

        RunResult result;
        try { result = await Runner.RunAsync(scannerBinary, cwd, targetFile: filePath); }
        catch { return; }

        if (result.Findings.Count == 0) return;

        var sb = new StringBuilder();
        foreach (var f in result.Findings)
        {
            var sev = f.Severity switch
            {
                Severity.Error   => "ERROR",
                Severity.Warning => "WARN",
                _                => "INFO"
            };
            sb.AppendLine($"{f.FilePath}:{f.Line}  {sev}  {f.Message}  [{f.RuleId}]");
            if (!string.IsNullOrEmpty(f.MatchedText))
                sb.AppendLine($"    {f.MatchedText}");
        }
        var errors   = result.Findings.Count(f => f.Severity == Severity.Error);
        var warnings = result.Findings.Count(f => f.Severity == Severity.Warning);
        var infos    = result.Findings.Count(f => f.Severity == Severity.Info);
        sb.Append($"Found {result.Findings.Count} violation(s): {errors} errors, {warnings} warnings, {infos} info");
        if (result.ScannerWarning != null)
            sb.Append($"\nWarning: {result.ScannerWarning}");
        WriteHookContext(sb.ToString());
    }

    private static void WriteHookContext(string text)
    {
        var escaped = JsonSerializer.Serialize(text, HookCommandJsonContext.Default.String);
        Console.WriteLine($"{{\"hookSpecificOutput\":{{\"hookEventName\":\"PostToolUse\",\"additionalContext\":{escaped}}}}}");
    }

    private static string? FindProjectRoot(string startDir)
    {
        var dir = startDir;
        while (!string.IsNullOrEmpty(dir))
        {
            var dolphinDir = Path.Combine(dir, ".dolphin");
            if (File.Exists(Path.Combine(dolphinDir, "rules.yaml"))
             || File.Exists(Path.Combine(dolphinDir, "rules.yml"))) return dir;
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent!;
        }
        return null;
    }
}
