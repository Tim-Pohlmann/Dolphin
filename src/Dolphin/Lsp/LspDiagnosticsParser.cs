using System.Text.RegularExpressions;

namespace Dolphin.Lsp;

/// <summary>
/// Parses the text output of <c>opengrep validate</c> into LSP diagnostics.
///
/// Opengrep may emit errors in two formats:
///
/// 1. Semgrep-style — message line followed by a separate location pointer:
///      Invalid rule 'no-console-log': missing required field 'message'
///        --> /path/to/file with spaces.yaml:8:5
///
/// 2. Inline-location — message and location on the same line (opengrep validate):
///      [00.20][WARNING]: invalid rule bad-rule, rules.yaml:2:4: Missing required field message
///
/// For format 1, the error line is recognised by keywords (error, invalid, …)
/// and the --> pointer line that follows provides the 1-based line/column.
/// For format 2, the location is extracted from the same line as the message
/// and the diagnostic is resolved immediately without a follow-up pointer line.
/// </summary>
internal static partial class LspDiagnosticsParser
{
    // Anchored to EOL so paths with spaces/colons are handled correctly.
    [GeneratedRegex(@"-->.*?:(\d+)(?::(\d+))?\s*$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LocationPattern();

    // Opengrep validate embeds the location inline: "..., /tmp/rules.yaml:2:4: message"
    [GeneratedRegex(@"\.ya?ml:(\d+)(?::(\d+))?", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex InlineLocationPattern();

    [GeneratedRegex(@"\b(error|invalid|missing|required|unexpected)\b", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ErrorKeywordPattern();

    public static LspDiagnostic[] Parse(string output)
    {
        var diagnostics = new List<LspDiagnostic>();

        foreach (var raw in output.Split('\n'))
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0) continue;

            var hasLocation = TryParseLocation(raw, out var lineNum, out var colNum);
            var hasKeyword  = ErrorKeywordPattern().IsMatch(trimmed);

            if (hasLocation && !hasKeyword)
            {
                // Pure location pointer line (e.g. "  --> /path/rules.yaml:8:5")
                AttachLocation(diagnostics, lineNum, colNum);
                continue;
            }

            if (hasKeyword)
            {
                var diag = MakePendingDiagnostic(trimmed);
                if (hasLocation)
                {
                    // Opengrep embeds location inline — resolve immediately
                    // rather than waiting for a follow-up --> pointer line.
                    var pos = new LspPosition(lineNum, colNum);
                    diag = diag with { Range = new LspRange(pos, pos), Pending = false };
                }
                diagnostics.Add(diag);
            }
        }

        FinalisePending(diagnostics);

        if (diagnostics.Count == 0 && output.Trim().Length > 0)
            diagnostics.Add(MakeFallbackDiagnostic(output));

        return [.. diagnostics];
    }

    private static bool TryParseLocation(string raw, out int lineNum, out int colNum)
    {
        var m = LocationPattern().Match(raw);
        if (!m.Success) m = InlineLocationPattern().Match(raw);
        if (!m.Success) { lineNum = 0; colNum = 0; return false; }

        if (!int.TryParse(m.Groups[1].Value, out var line1Based))
        {
            lineNum = 0;
            colNum = 0;
            return false;
        }

        int col1Based = 0;
        if (m.Groups[2].Success && !int.TryParse(m.Groups[2].Value, out col1Based))
        {
            lineNum = 0;
            colNum = 0;
            return false;
        }

        lineNum = Math.Max(0, line1Based - 1);
        colNum = m.Groups[2].Success ? Math.Max(0, col1Based - 1) : 0;
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
                    new LspPosition(lineNum, colNum)),
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
        new(Range: new LspRange(new LspPosition(0, 0), new LspPosition(1, 0)),
            Severity: 1, Source: "opengrep", Message: message, Pending: true);

    private static LspDiagnostic MakeFallbackDiagnostic(string output) =>
        new(Range: new LspRange(new LspPosition(0, 0), new LspPosition(1, 0)),
            Severity: 1, Source: "opengrep",
            Message: output.Trim().Split('\n')[0].TrimEnd('\r'),
            Pending: false);
}
