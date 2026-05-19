using System.CommandLine;
using System.Text.Json;
using Dolphin.Scanner;

namespace Dolphin.Cli;

public static class HookCommand
{
    public static Command Build()
    {
        var postToolUse = new Command("post-tool-use", "Handle post-tool-use hook events from Claude Code");
        postToolUse.SetHandler(HandlePostToolUseAsync);

        var hook = new Command("hook", "Internal hook handlers for Claude Code integration");
        hook.AddCommand(postToolUse);
        return hook;
    }

    private static Task HandlePostToolUseAsync()
        => HandlePostToolUseAsync(Console.OpenStandardInput());

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
        foreach (var d in diagnostics)
            Console.WriteLine($"{displayName}:{d.Range.Start.Line + 1}:{d.Range.Start.Character + 1}: {d.Message}");
    }
}
