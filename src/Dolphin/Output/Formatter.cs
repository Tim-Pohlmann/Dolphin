using System.Text.Json;
using System.Text.Json.Serialization;
using Dolphin.Semgrep;

namespace Dolphin.Output;

// Named DTO for trim-safe JSON serialization
public record FindingDto(
    string RuleId,
    string Severity,
    string FilePath,
    int Line,
    int Column,
    string Message,
    string MatchedText
);

[JsonSerializable(typeof(List<FindingDto>))]
internal partial class FormatterJsonContext : JsonSerializerContext { }

public static class Formatter
{
    public static void Print(List<Finding> findings, string format)
    {
        if (format == "json")
        {
            PrintJson(findings);
            return;
        }

        PrintText(findings);
    }

    private static void PrintText(List<Finding> findings)
    {
        if (findings.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("✓ No violations found.");
            Console.ResetColor();
            return;
        }

        int errors = 0, warnings = 0, infos = 0;

        foreach (var f in findings)
        {
            var (label, color) = f.Severity switch
            {
                Severity.Error   => (" ERROR ", ConsoleColor.Red),
                Severity.Warning => (" WARN  ", ConsoleColor.Yellow),
                _                => (" INFO  ", ConsoleColor.Cyan)
            };

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write($"  {f.FilePath}:{f.Line}");
            Console.ResetColor();
            Console.Write("  ");
            Console.BackgroundColor = color;
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write(label);
            Console.ResetColor();
            Console.Write($"  {f.Message}  ");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"[{f.RuleId}]");
            Console.ResetColor();

            if (!string.IsNullOrEmpty(f.MatchedText))
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"    {f.MatchedText}");
                Console.ResetColor();
            }

            switch (f.Severity)
            {
                case Severity.Error:   errors++;   break;
                case Severity.Warning: warnings++; break;
                default:               infos++;    break;
            }
        }

        Console.WriteLine();
        Console.Write($"Found {findings.Count} violation(s): ");
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Write($"{errors} errors");
        Console.ResetColor();
        Console.Write(", ");
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write($"{warnings} warnings");
        Console.ResetColor();
        Console.Write(", ");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write($"{infos} info");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static void PrintJson(List<Finding> findings)
    {
        var output = findings.Select(f => new FindingDto(
            f.RuleId,
            f.Severity.ToString().ToLower(),
            f.FilePath,
            f.Line,
            f.Column,
            f.Message,
            f.MatchedText
        )).ToList();

        Console.WriteLine(JsonSerializer.Serialize(
            output,
            FormatterJsonContext.Default.ListFindingDto
        ));
    }
}
