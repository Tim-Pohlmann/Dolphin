using System.Diagnostics;
using System.Text.Json;

namespace Dolphin.Scanner;

public static class Runner
{
    /// <summary>
    /// Runs the scanner against <paramref name="cwd"/> using .dolphin/rules.yaml.
    /// Optionally filters to a single rule ID.
    /// </summary>
    public static async Task<RunResult> RunAsync(
        string scannerBinary,
        string cwd,
        string? ruleId = null)
    {
        var rulesPath = Path.Combine(cwd, ".dolphin", "rules.yaml");
        if (!File.Exists(rulesPath))
            throw new FileNotFoundException(
                $"No rules file at {rulesPath}. Run the generate-rules skill first.");

        var args = new List<string>
        {
            "--config", rulesPath,
            "--json",
            "--no-git-ignore",
            "--no-rewrite-rule-ids",
            cwd
        };

        var psi = new ProcessStartInfo(scannerBinary)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = cwd
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi)!;
        // Read stdout and stderr concurrently to avoid deadlock when
        // the child fills one pipe while we're blocked reading the other.
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await Task.WhenAll(stdoutTask, stderrTask);
        await proc.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        // Exit 0 = clean, 1 = findings present, 2 = non-fatal scanner warning, 3+ = error
        if (proc.ExitCode > 2)
        {
            var detail = TryExtractErrors(stdout)
                ?? (!string.IsNullOrWhiteSpace(stderr) ? stderr.Trim()
                    : !string.IsNullOrWhiteSpace(stdout) ? stdout.Trim()
                    : "Scanner failed without returning error details.");
            throw new InvalidOperationException(
                $"Scanner exited with code {proc.ExitCode}.\n{detail}");
        }

        var scannerWarning = proc.ExitCode == 2
            ? (string.IsNullOrWhiteSpace(stderr) ? "Scanner reported a non-fatal warning." : stderr.Trim())
            : null;

        var findings = ParseFindings(stdout, cwd);
        // Filter by rule ID if requested. The check_id in the output is typically the bare rule ID
        // but may be path-prefixed by the scanner (e.g. ".dolphin.my-rule"). Match on suffix.
        if (ruleId != null)
            findings = findings
                .Where(f => f.RuleId == ruleId || f.RuleId.EndsWith("." + ruleId))
                .ToList();
        return new RunResult(findings, proc.ExitCode == 1, scannerWarning);
    }

    /// <summary>
    /// Extracts human-readable error messages from opengrep's JSON stdout, if available.
    /// Returns null if stdout is not valid JSON or contains no errors.
    /// </summary>
    private static string? TryExtractErrors(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("errors", out var errors)) return null;
            if (errors.ValueKind != JsonValueKind.Array) return null;
            var messages = errors.EnumerateArray()
                .Select(e =>
                {
                    if (e.ValueKind != JsonValueKind.Object) return null;
                    // SemgrepError is a redundant summary entry; skip it
                    if (e.TryGetProperty("type", out var t) &&
                        t.ValueKind == JsonValueKind.String &&
                        t.GetString() == "SemgrepError") return null;
                    // Opengrep uses "long_msg" for schema errors, "message" for others
                    var msgElement = e.TryGetProperty("long_msg", out var lm) && lm.ValueKind == JsonValueKind.String
                        ? lm : e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                        ? m : (JsonElement?)null;
                    var text = msgElement?.GetString();
                    if (text == null) return null;
                    if (e.TryGetProperty("spans", out var spans) && spans.ValueKind == JsonValueKind.Array)
                    {
                        var lines = spans.EnumerateArray()
                            .Select(s =>
                            {
                                if (s.ValueKind != JsonValueKind.Object) return (int?)null;
                                if (!s.TryGetProperty("start", out var st) || st.ValueKind != JsonValueKind.Object) return (int?)null;
                                if (!st.TryGetProperty("line", out var ln) || ln.ValueKind != JsonValueKind.Number) return (int?)null;
                                return ln.TryGetInt32(out var lineNumber) ? lineNumber : (int?)null;
                            })
                            .Where(l => l != null)
                            .ToList();
                        if (lines.Count > 0)
                            text += $"\n  at line {string.Join(", ", lines)}";
                    }
                    return text;
                })
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .ToList();
            return messages.Count > 0 ? string.Join("\n", messages) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<Finding> ParseFindings(string json, string cwd)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results)) return [];

        var findings = new List<Finding>();
        foreach (var r in results.EnumerateArray())
        {
            var checkId = r.GetProperty("check_id").GetString() ?? "";
            var path = r.GetProperty("path").GetString() ?? "";
            var start = r.GetProperty("start");
            var line = start.GetProperty("line").GetInt32();
            var col = start.GetProperty("col").GetInt32();
            var extra = r.GetProperty("extra");
            var message = extra.GetProperty("message").GetString() ?? "";
            var sevStr = extra.GetProperty("severity").GetString() ?? "WARNING";
            var lines = extra.TryGetProperty("lines", out var linesEl)
                ? linesEl.GetString() ?? ""
                : "";

            var severity = sevStr.ToUpper() switch
            {
                "ERROR" => Severity.Error,
                "INFO" => Severity.Info,
                _ => Severity.Warning
            };

            // Make path relative to cwd for cleaner output
            var relPath = Path.GetRelativePath(cwd, path);

            findings.Add(new Finding(checkId, severity, relPath, line, col, message, lines.Trim()));
        }

        // Sort by file path then line number
        findings.Sort((a, b) =>
        {
            var fc = string.Compare(a.FilePath, b.FilePath, StringComparison.OrdinalIgnoreCase);
            return fc != 0 ? fc : a.Line.CompareTo(b.Line);
        });

        return findings;
    }
}

public record RunResult(List<Finding> Findings, bool HasFindings, string? ScannerWarning = null);
