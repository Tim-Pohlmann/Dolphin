using System.Diagnostics;
using System.Text.Json;

namespace Dolphin.Scanner;

public static class Runner
{
    /// <summary>
    /// Runs the scanner against <paramref name="cwd"/> using .dolphin/rules.yaml (or rules.yml).
    /// Optionally filters to a single rule ID or scans a single file.
    /// </summary>
    public static async Task<RunResult> RunAsync(
        string scannerBinary,
        string cwd,
        string? ruleId = null,
        string? targetFile = null)
    {
        var rulesPath = ResolveRulesPath(cwd);

        if (targetFile != null && !Path.IsPathRooted(targetFile))
            targetFile = Path.GetFullPath(targetFile, cwd);

        var args = new List<string>
        {
            "--config", rulesPath,
            "--json",
            "--no-git-ignore",
            "--no-rewrite-rule-ids",
            targetFile ?? cwd
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
                .Where(f => f.RuleId == ruleId || f.RuleId.EndsWith("." + ruleId, StringComparison.Ordinal))
                .ToList();
        return new RunResult(findings, proc.ExitCode == 1, scannerWarning);
    }

    private static string ResolveRulesPath(string cwd)
    {
        var yamlPath = Path.Combine(cwd, ".dolphin", "rules.yaml");
        if (File.Exists(yamlPath)) return yamlPath;
        var ymlPath = Path.Combine(cwd, ".dolphin", "rules.yml");
        if (File.Exists(ymlPath)) return ymlPath;
        throw new FileNotFoundException(
            $"No rules file at {yamlPath} (or rules.yml). Run the generate-rules skill first.");
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
            var allErrors = errors.EnumerateArray().ToList();
            // SemgrepError is a redundant summary of detail errors; prefer detail errors
            var messages = allErrors
                .Select(FormatErrorEntry)
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .ToList();
            // Fall back to SemgrepError summary if no detail errors were present
            if (messages.Count == 0)
                messages = allErrors
                    .Select(FormatSemgrepEntry)
                    .Where(m => !string.IsNullOrWhiteSpace(m))
                    .ToList();
            return messages.Count > 0 ? string.Join("\n", messages) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? FormatErrorEntry(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (e.TryGetProperty("type", out var t) &&
            t.ValueKind == JsonValueKind.String &&
            t.GetString() == "SemgrepError") return null;
        var text = GetErrorMessage(e);
        if (text == null) return null;
        var lines = GetSpanLineNumbers(e);
        if (lines.Count > 0)
        {
            var lineLabel = lines.Count == 1 ? "line" : "lines";
            text += $"\n  at {lineLabel} {string.Join(", ", lines)}";
        }
        return text;
    }

    private static string? FormatSemgrepEntry(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty("type", out var t) ||
            t.ValueKind != JsonValueKind.String ||
            t.GetString() != "SemgrepError") return null;
        return GetErrorMessage(e);
    }

    private static string? GetErrorMessage(JsonElement e)
    {
        // Opengrep uses "long_msg" for schema errors, "message" for others
        if (e.TryGetProperty("long_msg", out var lm) && lm.ValueKind == JsonValueKind.String)
            return lm.GetString();
        if (e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
            return m.GetString();
        return null;
    }

    private static List<int> GetSpanLineNumbers(JsonElement e)
    {
        if (!e.TryGetProperty("spans", out var spans) || spans.ValueKind != JsonValueKind.Array)
            return [];
        return spans.EnumerateArray()
            .Select(GetSpanStartLine)
            .Where(l => l != null)
            .Select(l => l!.Value)
            .Distinct()
            .OrderBy(l => l)
            .ToList();
    }

    private static int? GetSpanStartLine(JsonElement s)
    {
        if (s.ValueKind != JsonValueKind.Object) return null;
        if (!s.TryGetProperty("start", out var st) || st.ValueKind != JsonValueKind.Object) return null;
        if (!st.TryGetProperty("line", out var ln) || ln.ValueKind != JsonValueKind.Number) return null;
        return ln.TryGetInt32(out var lineNumber) ? lineNumber : null;
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
