using System.ComponentModel;
using System.Text;
using Dolphin.Scanner;
using ModelContextProtocol.Server;

namespace Dolphin.Mcp.Tools;

[McpServerToolType]
public sealed class RunCheckTool
{
    [McpServerTool, Description(
        "Run Dolphin static analysis rules against a codebase. " +
        "Returns a summary of all violations found. " +
        "Optionally filter to a single rule ID."
    )]
    public async Task<string> RunCheck(
        [Description("Absolute path to the project root directory to scan")]
        string cwd,
        [Description("Optional: only run the rule with this ID")]
        string? ruleId = null)
    {
        if (!Directory.Exists(cwd))
            return $"Error: directory not found: {cwd}";

        string scannerBinary;
        try
        {
            scannerBinary = await Installer.EnsureInstalledAsync();
        }
        catch (Exception ex)
        {
            return $"Error: could not locate scanner: {ex.Message}";
        }

        RunResult result;
        try
        {
            result = await Runner.RunAsync(scannerBinary, cwd, ruleId);
        }
        catch (FileNotFoundException ex)
        {
            return $"Error: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Error running scanner: {ex.Message}";
        }

        if (result.Findings.Count == 0)
            return "✓ No violations found.";

        var sb = new StringBuilder();
        sb.AppendLine($"Found {result.Findings.Count} violation(s):\n");

        foreach (var f in result.Findings)
        {
            var sev = f.Severity.ToString().ToUpper();
            sb.AppendLine($"  {f.FilePath}:{f.Line}  [{sev}]  {f.Message}  [{f.RuleId}]");
            if (!string.IsNullOrEmpty(f.MatchedText))
                sb.AppendLine($"    {f.MatchedText}");
        }

        var errors = result.Findings.Count(f => f.Severity == Severity.Error);
        var warnings = result.Findings.Count(f => f.Severity == Severity.Warning);
        var infos = result.Findings.Count(f => f.Severity == Severity.Info);
        sb.AppendLine($"\nSummary: {errors} errors, {warnings} warnings, {infos} info");

        return sb.ToString();
    }
}
