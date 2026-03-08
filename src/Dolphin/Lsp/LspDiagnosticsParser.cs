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
internal static partial class LspDiagnosticsParser
{
    // Anchored to EOL so paths with spaces/colons are handled correctly.
    [GeneratedRegex(@"-->.+:(\d+)(?::(\d+))?\s*$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LocationPattern();

    [GeneratedRegex(@"error|invalid|missing|required|unexpected", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ErrorKeywordPattern();

    public static LspDiagnostic[] Parse(string output)
    {
        var diagnostics = new List<LspDiagnostic>();

        foreach (var raw in output.Split('\n'))
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0) continue;

            if (TryParseLocation(raw, out var lineNum, out var colNum))
            {
                AttachLocation(diagnostics, lineNum, colNum);
                continue;
            }

            if (ErrorKeywordPattern().IsMatch(trimmed))
                diagnostics.Add(MakePendingDiagnostic(trimmed));
        }

        FinalisePending(diagnostics);

        if (diagnostics.Count == 0 && output.Trim().Length > 0)
            diagnostics.Add(MakeFallbackDiagnostic(output));

        return [.. diagnostics];
    }

    private static bool TryParseLocation(string raw, out int lineNum, out int colNum)
    {
        var m = LocationPattern().Match(raw);
        if (!m.Success) { lineNum = 0; colNum = 0; return false; }

        lineNum = Math.Max(0, int.Parse(m.Groups[1].Value) - 1);
        colNum  = m.Groups[2].Success ? Math.Max(0, int.Parse(m.Groups[2].Value) - 1) : 0;
        return true;
    }

    private static void AttachLocation(List<LspDiagnostic> diagnostics, int lineNum, int colNum)
    {
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
    }

    private static void FinalisePending(List<LspDiagnostic> diagnostics)
    {
        for (int i = 0; i < diagnostics.Count; i++)
            if (diagnostics[i].Pending)
                diagnostics[i] = diagnostics[i] with { Pending = false };
    }

    private static LspDiagnostic MakePendingDiagnostic(string message) =>
        new(Range: new LspRange(new LspPosition(0, 0), new LspPosition(0, int.MaxValue)),
            Severity: 1, Source: "opengrep", Message: message, Pending: true);

    private static LspDiagnostic MakeFallbackDiagnostic(string output) =>
        new(Range: new LspRange(new LspPosition(0, 0), new LspPosition(0, int.MaxValue)),
            Severity: 1, Source: "opengrep",
            Message: output.Trim().Split('\n')[0],
            Pending: false);
}
