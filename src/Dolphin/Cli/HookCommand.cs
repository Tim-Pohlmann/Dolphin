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

    private static async Task HandlePostToolUseAsync()
    {
        var json = await Console.In.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(json)) return;

        string? filePath;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("tool_input", out var input)) return;
            if (!input.TryGetProperty("file_path", out var fp)) return;
            filePath = fp.GetString();
        }
        catch (JsonException)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(filePath)) return;

        var normalizedPath = filePath.Replace('\\', '/');
        if (normalizedPath.EndsWith("/.dolphin/rules.yaml", StringComparison.OrdinalIgnoreCase)
         || normalizedPath.EndsWith("/.dolphin/rules.yml", StringComparison.OrdinalIgnoreCase))
            await ValidateRulesFileAsync(filePath);
    }

    private static async Task ValidateRulesFileAsync(string filePath)
    {
        if (!File.Exists(filePath)) return;

        var text = await File.ReadAllTextAsync(filePath);
        var diagnostics = YamlRuleValidator.Validate(text);

        if (diagnostics.Length == 0) return;

        foreach (var d in diagnostics)
            Console.WriteLine($"rules.yaml:{d.Range.Start.Line + 1}: {d.Message}");
    }
}
