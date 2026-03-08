using System.Text.RegularExpressions;

namespace Dolphin.Lsp;

/// <summary>
/// Parses Opengrep rule IDs from a rules YAML document and returns them
/// as LSP symbol information for the document outline / go-to-symbol feature.
/// </summary>
internal static class DocumentSymbols
{
    // Matches "  - id: my-rule-name" with optional leading whitespace.
    private static readonly Regex IdLine = new(@"^\s*-\s+id:\s*(\S+)", RegexOptions.Compiled);

    public static RuleSymbol[] Parse(string uri, string text)
    {
        var symbols = new List<RuleSymbol>();
        var lines = text.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            var m = IdLine.Match(lines[i]);
            if (!m.Success) continue;

            var name = m.Groups[1].Value;
            var col = lines[i].IndexOf(name, StringComparison.Ordinal);
            var start = new LspPosition(i, col);
            var end = new LspPosition(i, col + name.Length);
            symbols.Add(new RuleSymbol(name, start, end));
        }

        return [.. symbols];
    }
}

internal record RuleSymbol(string Name, LspPosition Start, LspPosition End);
