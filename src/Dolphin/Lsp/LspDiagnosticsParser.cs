using System.Text.RegularExpressions;

namespace Dolphin.Lsp;

/// <summary>
/// Parses the text output of `opengrep validate` into LSP diagnostics.
///
/// Opengrep emits structured errors like:
///   Invalid rule 'no-console-log': missing required field 'message'
///     --> /path/to/file with spaces.yaml:8:5
///
/// The error message line is recognised by keywords (error, invalid, …).
/// The location pointer line (-->) follows immediately and provides the
/// 1-based line and optional column, anchored at the end of the line so
/// that file paths containing spaces or colons are handled correctly.
/// </summary>
internal static class LspDiagnosticsParser
{
    public static LspDiagnostic[] Parse(string output)
    {
        var diagnostics = new List<LspDiagnostic>();
        var lines = output.Split('\n');

        foreach (var raw in lines)
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0) continue;

            // Location pointer: "  --> /any/path (spaces ok):LINE:COL"
            // Anchored to end-of-line so the last :number:number is captured
            // regardless of spaces or colons in the path.
            var locMatch = Regex.Match(raw, @"-->.+:(\d+)(?::(\d+))?\s*$");
            if (locMatch.Success)
            {
                var lineNum = Math.Max(0, int.Parse(locMatch.Groups[1].Value) - 1);
                var colNum = locMatch.Groups[2].Success
                    ? Math.Max(0, int.Parse(locMatch.Groups[2].Value) - 1)
                    : 0;

                // Attach the precise location to the preceding pending diagnostic.
                if (diagnostics.Count > 0 && diagnostics[^1].Pending)
                {
                    diagnostics[^1] = diagnostics[^1] with
                    {
                        Range = new LspRange(
                            new LspPosition(lineNum, colNum),
                            new LspPosition(lineNum, int.MaxValue)),
                        Pending = false,
                    };
                }
                continue;
            }

            // Error message line — recognised by common opengrep keywords.
            if (Regex.IsMatch(trimmed, @"error|invalid|missing|required|unexpected",
                    RegexOptions.IgnoreCase))
            {
                diagnostics.Add(new LspDiagnostic(
                    Range: new LspRange(new LspPosition(0, 0), new LspPosition(0, int.MaxValue)),
                    Severity: 1,
                    Source: "opengrep",
                    Message: trimmed,
                    Pending: true));
            }
        }

        // Finalise any diagnostics that never got a location (leave at line 0).
        for (int i = 0; i < diagnostics.Count; i++)
            if (diagnostics[i].Pending)
                diagnostics[i] = diagnostics[i] with { Pending = false };

        // Fallback: non-zero exit but no recognisable error lines.
        if (diagnostics.Count == 0 && output.Trim().Length > 0)
        {
            diagnostics.Add(new LspDiagnostic(
                Range: new LspRange(new LspPosition(0, 0), new LspPosition(0, int.MaxValue)),
                Severity: 1,
                Source: "opengrep",
                Message: output.Trim().Split('\n')[0],
                Pending: false));
        }

        return [.. diagnostics];
    }
}
