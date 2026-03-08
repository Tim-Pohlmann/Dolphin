using System.Text.Json.Nodes;

namespace Dolphin.Lsp;

/// <summary>
/// Parses OpenGrep rules YAML and provides diagnostics, completions, and hover documentation.
///
/// Supports the Opengrep/Semgrep rule schema:
///   https://opengrep.dev/docs/writing-rules/rule-syntax
/// </summary>
internal static class RulesYamlAnalyzer
{
    // ── Schema knowledge ──────────────────────────────────────────────────────────

    private static readonly string[] KnownLanguages =
    [
        "apex", "bash", "c", "cairo", "clojure", "cpp", "csharp", "dart",
        "dockerfile", "elixir", "generic", "go", "hack", "html", "java",
        "javascript", "json", "jsonnet", "julia", "kotlin", "lisp", "lua",
        "ocaml", "php", "promql", "proto", "python", "r", "ruby", "rust",
        "scala", "scheme", "solidity", "swift", "terraform", "typescript",
        "tsx", "vue", "xml", "yaml"
    ];

    private static readonly string[] ValidSeverities = ["ERROR", "WARNING", "INFO"];

    private static readonly string[] RequiredRuleKeys = ["id", "message", "languages", "severity"];

    private static readonly string[] PatternKeys =
        ["pattern", "patterns", "pattern-regex", "pattern-not-regex", "pattern-either"];

    private static readonly string[] AllRuleKeys =
    [
        "id", "message", "languages", "severity",
        "pattern", "patterns", "pattern-regex", "pattern-not-regex", "pattern-either",
        "fix", "metadata"
    ];

    private static readonly string[] PatternsItemKeys =
    [
        "pattern", "pattern-not", "pattern-inside", "pattern-not-inside",
        "pattern-either", "pattern-regex", "pattern-not-regex",
        "focus-metavariable", "metavariable-regex", "metavariable-pattern",
        "metavariable-comparison"
    ];

    private static readonly Dictionary<string, string> FieldDocs = new()
    {
        ["id"] =
            "**id** — Unique rule identifier. Use kebab-case.\n\nExample: `no-console-log`",
        ["message"] =
            "**message** — Description shown to developers when this rule triggers.\n\nExample: `Remove console.log before committing.`",
        ["languages"] =
            "**languages** — Languages this rule applies to.\n\nExample: `[typescript, javascript]`\n\nSupported: " +
            string.Join(", ", KnownLanguages),
        ["severity"] =
            "**severity** — Rule severity level.\n\n" +
            "- `ERROR` — Fails CI (exit code 1)\n" +
            "- `WARNING` — Shown in output, does not fail CI\n" +
            "- `INFO` — Informational only",
        ["pattern"] =
            "**pattern** — A single Opengrep pattern. Supports metavariables (`$X`), ellipsis (`...`), and typed metavariables.\n\nExample:\n```yaml\npattern: console.log(...)\n```",
        ["patterns"] =
            "**patterns** — A list of pattern clauses that must **all** match (AND logic).\n\nSupports: `pattern`, `pattern-not`, `pattern-inside`, `metavariable-regex`, etc.",
        ["pattern-regex"] =
            "**pattern-regex** — A PCRE regex matched against the raw source text.",
        ["pattern-not-regex"] =
            "**pattern-not-regex** — Exclude matches where the source matches this regex.",
        ["pattern-either"] =
            "**pattern-either** — A list of patterns where **any** one must match (OR logic).",
        ["fix"] =
            "**fix** — Autofix template. Opengrep applies this with `--autofix`.\n\nExample: `$OBJ.getLogger()`",
        ["metadata"] =
            "**metadata** — Optional structured metadata (category, technology, confidence, impact, etc.).",
        ["pattern-not"] =
            "**pattern-not** — Exclude matches where this sub-pattern also matches.",
        ["pattern-inside"] =
            "**pattern-inside** — Only report code that is inside this surrounding pattern.",
        ["pattern-not-inside"] =
            "**pattern-not-inside** — Exclude matches inside this surrounding pattern.",
        ["focus-metavariable"] =
            "**focus-metavariable** — Report only the binding of this metavariable, not the entire match.",
        ["metavariable-regex"] =
            "**metavariable-regex** — Constrain a metavariable with a PCRE regex.\n\nExample:\n```yaml\nmetavariable-regex:\n  metavariable: $KEY\n  regex: '(?i)password'\n```",
        ["metavariable-pattern"] =
            "**metavariable-pattern** — Constrain a metavariable with a nested Opengrep pattern.",
        ["metavariable-comparison"] =
            "**metavariable-comparison** — Compare metavariable values numerically or as strings.",
    };

    // ── YAML line model ───────────────────────────────────────────────────────────

    /// <summary>A single parsed line from a YAML file.</summary>
    private record YamlLine(
        int Index,          // 0-based line number
        int Indent,         // leading space count (the `- ` prefix indent)
        bool IsList,        // line contains `<indent>- ` prefix
        string? Key,        // key before the colon separator
        string? InlineValue,// value after `key: ` (null if block or absent)
        int KeyCol,         // 0-based column where key text begins
        int KeyEndCol,      // 0-based column after last key character (== colon position)
        int ValCol          // 0-based column where value begins (-1 if no value)
    );

    private static YamlLine ParseLine(string text, int index)
    {
        int indent = 0;
        while (indent < text.Length && text[indent] == ' ') indent++;

        int pos = indent;

        // Early-out for empty and comment lines
        if (pos >= text.Length || text[pos] == '#')
            return new YamlLine(index, indent, false, null, null, pos, pos, -1);

        // List-item prefix: `- ` (possibly followed by more spaces)
        bool isList = false;
        if (pos < text.Length - 1 && text[pos] == '-' && text[pos + 1] == ' ')
        {
            isList = true;
            pos += 2;
            while (pos < text.Length && text[pos] == ' ') pos++;
        }
        else if (pos == text.Length - 1 && text[pos] == '-')
        {
            // Bare `-` at end of line (empty list item)
            return new YamlLine(index, indent, true, null, null, pos + 1, pos + 1, -1);
        }

        int keyCol = pos;

        // Find the key-value colon separator (ignoring colons inside quotes/flow collections)
        int depth = 0;
        int colonPos = -1;
        bool inQuote = false;
        char quoteChar = '\0';

        for (int i = pos; i < text.Length; i++)
        {
            char c = text[i];
            if (inQuote)
            {
                if (c == quoteChar) inQuote = false;
                continue;
            }
            if (c == '\'' || c == '"') { inQuote = true; quoteChar = c; continue; }
            if (c == '[' || c == '{') { depth++; continue; }
            if (c == ']' || c == '}') { depth--; continue; }
            if (c == '#' && depth == 0) break; // inline comment

            if (c == ':' && depth == 0)
            {
                // Valid separator: colon at end of string, or followed by space/newline
                if (i + 1 >= text.Length || text[i + 1] == ' ' || text[i + 1] == '\r')
                {
                    colonPos = i;
                    break;
                }
            }
        }

        if (colonPos < 0)
        {
            // Pure value (flow element, block-scalar content, etc.)
            var raw = text[pos..].TrimEnd();
            return new YamlLine(index, indent, isList, null, raw.Length > 0 ? raw : null, keyCol, keyCol, keyCol);
        }

        string key = text[pos..colonPos].Trim();
        int keyEndCol = colonPos; // exclusive

        // Find the value start (first non-space after the colon)
        int valStart = colonPos + 1;
        while (valStart < text.Length && text[valStart] == ' ') valStart++;

        string? value = null;
        int valCol = -1;
        if (valStart < text.Length && text[valStart] != '#' && text[valStart] != '\r')
        {
            value = text[valStart..].TrimEnd();
            if (value.Length == 0) value = null;
            else valCol = valStart;
        }

        return new YamlLine(index, indent, isList, key, value, keyCol, keyEndCol, valCol);
    }

    private static YamlLine[] ParseLines(string content)
    {
        var raw = content.Split('\n');
        var result = new YamlLine[raw.Length];
        for (int i = 0; i < raw.Length; i++)
            result[i] = ParseLine(raw[i].TrimEnd('\r'), i);
        return result;
    }

    // ── Diagnostics ───────────────────────────────────────────────────────────────

    internal record DiagnosticItem(
        int StartLine, int StartCol, int EndLine, int EndCol,
        string Message, string Code,
        int Severity  // 1=Error 2=Warning 3=Information
    );

    public static DiagnosticItem[] Validate(string content)
    {
        var diags = new List<DiagnosticItem>();
        var lines = ParseLines(content);

        // Find top-level `rules:` key
        var rulesLine = lines.FirstOrDefault(l => l.Key == "rules" && l.Indent == 0);
        if (rulesLine is null)
        {
            bool hasContent = lines.Any(l => l.Key is not null || l.InlineValue is not null);
            if (hasContent)
                diags.Add(Diag(0, 0, 0, 5,
                    "Missing required top-level key `rules:`. Opengrep rule files must start with `rules:`.",
                    "DLPH001", 1));
            return [.. diags];
        }

        // Find the indent of direct children (list items under `rules:`)
        int? childIndent = null;
        for (int i = rulesLine.Index + 1; i < lines.Length; i++)
        {
            var l = lines[i];
            if (l.Key is null && l.InlineValue is null) continue;
            if (l.Indent <= rulesLine.Indent) break;
            if (l.IsList) { childIndent = l.Indent; break; }
        }

        // Collect rule section boundaries (each starting list item under `rules:`)
        var sections = new List<(int Start, int End)>();
        if (childIndent.HasValue)
        {
            int rli = childIndent.Value;
            int? secStart = null;

            for (int i = rulesLine.Index + 1; i <= lines.Length; i++)
            {
                bool eof = i == lines.Length;

                if (!eof)
                {
                    var l = lines[i];
                    bool blank = l.Key is null && l.InlineValue is null;
                    if (!blank && l.Indent <= rulesLine.Indent) eof = true;
                }

                if (!eof && lines[i].IsList && lines[i].Indent == rli)
                {
                    if (secStart.HasValue) sections.Add((secStart.Value, i - 1));
                    secStart = i;
                }
                else if ((eof || (!eof && lines[i].Indent <= rulesLine.Indent)) && secStart.HasValue)
                {
                    sections.Add((secStart.Value, i - 1));
                    break;
                }
            }
        }

        if (sections.Count == 0)
        {
            diags.Add(Diag(rulesLine.Index, rulesLine.KeyCol, rulesLine.Index, rulesLine.KeyEndCol,
                "`rules:` list is empty — add at least one rule.", "DLPH005", 2));
            return [.. diags];
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (start, end) in sections)
            ValidateRule(lines, start, end, seenIds, diags);

        return [.. diags];
    }

    private static void ValidateRule(
        YamlLine[] lines, int start, int end,
        HashSet<string> seenIds, List<DiagnosticItem> diags)
    {
        // The rule's field indent level equals the start line's key column
        int fieldIndent = lines[start].KeyCol;

        // Collect top-level fields of this rule
        var fields = new Dictionary<string, YamlLine>(StringComparer.Ordinal);

        // Start line may itself carry a key (commonly `id:`)
        if (lines[start].Key is { } startKey)
            fields[startKey] = lines[start];

        for (int i = start + 1; i <= end; i++)
        {
            var l = lines[i];
            if (l.Key is null || l.IsList) continue;
            if (l.Indent == fieldIndent && !fields.ContainsKey(l.Key))
                fields[l.Key] = l;
        }

        // Required fields
        foreach (var req in RequiredRuleKeys)
        {
            if (!fields.ContainsKey(req))
                diags.Add(Diag(start, lines[start].KeyCol, start, lines[start].KeyEndCol,
                    $"Rule is missing required field `{req}:`.", "DLPH001", 1));
        }

        // At least one pattern key
        if (!PatternKeys.Any(fields.ContainsKey))
            diags.Add(Diag(start, lines[start].KeyCol, start, lines[start].KeyEndCol,
                "Rule must have at least one pattern key: `pattern`, `patterns`, `pattern-regex`, `pattern-not-regex`, or `pattern-either`.",
                "DLPH004", 1));

        // Validate `severity:`
        if (fields.TryGetValue("severity", out var sevLine))
        {
            var sev = sevLine.InlineValue?.Trim('"', '\'');
            if (sev is not null && !ValidSeverities.Contains(sev, StringComparer.OrdinalIgnoreCase))
            {
                int sc = sevLine.ValCol >= 0 ? sevLine.ValCol : sevLine.KeyCol;
                diags.Add(Diag(sevLine.Index, sc, sevLine.Index, sc + sev.Length,
                    $"Invalid severity `{sev}`. Must be ERROR, WARNING, or INFO.", "DLPH002", 1));
            }
        }

        // Validate `languages:`
        if (fields.TryGetValue("languages", out var langLine))
        {
            var langVal = langLine.InlineValue;
            if (langVal is null)
            {
                // Block-sequence style: check for child list items
                bool hasAny = false;
                for (int i = langLine.Index + 1; i <= end; i++)
                {
                    var l = lines[i];
                    if (l.Indent <= langLine.Indent) break;
                    if (l.IsList) { hasAny = true; break; }
                }
                if (!hasAny)
                    diags.Add(Diag(langLine.Index, langLine.KeyCol, langLine.Index, langLine.KeyEndCol,
                        "`languages:` must have at least one language.", "DLPH003", 1));
            }
            else if (langVal.Trim() is "[]" or "")
            {
                diags.Add(Diag(langLine.Index, langLine.KeyCol, langLine.Index, langLine.KeyEndCol,
                    "`languages:` must have at least one language.", "DLPH003", 1));
            }
            else if (langVal.StartsWith('['))
            {
                // Validate inline flow sequence language names
                var inner = langVal.TrimStart('[').TrimEnd(']');
                foreach (var part in inner.Split(','))
                {
                    var lang = part.Trim().Trim('"', '\'');
                    if (lang.Length > 0 && !KnownLanguages.Contains(lang, StringComparer.OrdinalIgnoreCase))
                    {
                        int sc = langLine.ValCol >= 0 ? langLine.ValCol : langLine.KeyCol;
                        diags.Add(Diag(langLine.Index, sc, langLine.Index, sc + langVal.Length,
                            $"Unknown language `{lang}`. See https://opengrep.dev/docs/writing-rules/rule-syntax#language-extensions-and-tags",
                            "DLPH003", 2));
                    }
                }
            }
        }

        // Validate unique `id:`
        if (fields.TryGetValue("id", out var idLine))
        {
            var id = idLine.InlineValue?.Trim('"', '\'');
            if (id is not null && !seenIds.Add(id))
            {
                int sc = idLine.ValCol >= 0 ? idLine.ValCol : idLine.KeyCol;
                diags.Add(Diag(idLine.Index, sc, idLine.Index, sc + id.Length,
                    $"Duplicate rule ID `{id}`. Rule IDs must be unique within the file.", "DLPH006", 1));
            }
        }
    }

    private static DiagnosticItem Diag(int sl, int sc, int el, int ec, string msg, string code, int sev) =>
        new(sl, sc, el, ec, msg, code, sev);

    // ── Completions ───────────────────────────────────────────────────────────────

    public static JsonNode GetCompletions(string content, int lineIdx, int character)
    {
        var rawLines = content.Split('\n');
        if (lineIdx >= rawLines.Length)
            return EmptyCompletionList();

        var lines = ParseLines(content);
        var rawLine = rawLines[lineIdx].TrimEnd('\r');
        var beforeCursor = rawLine[..Math.Min(character, rawLine.Length)];
        var cur = lines[lineIdx];

        // For blank/whitespace lines, use the cursor column as effective indent
        bool isBlank = cur.Key is null && cur.InlineValue is null && !cur.IsList;
        int effectiveIndent = isBlank ? character : cur.Indent;
        int effectiveKeyCol = isBlank ? character : cur.KeyCol;

        // Strip to content after any `- ` prefix for context checks
        var strippedBefore = beforeCursor.TrimStart().TrimStart('-').TrimStart(' ');

        // 1. Value completion: `severity: <cursor>`
        if (strippedBefore.StartsWith("severity:", StringComparison.OrdinalIgnoreCase))
            return MakeList(ValidSeverities.Select(s =>
                MakeItem(s, 12, SeverityDetail(s), null, s)));

        // 2. Language value: `languages: <cursor>` (inline)
        if (strippedBefore.StartsWith("languages:", StringComparison.OrdinalIgnoreCase))
            return MakeList(KnownLanguages.Select(l => MakeItem(l, 12, "Language identifier", null, l)));

        // 3. Inside `languages:` block sequence (block-style list)
        if (IsInsideBlock(lines, lineIdx, effectiveIndent, "languages"))
            return MakeList(KnownLanguages.Select(l => MakeItem(l, 12, "Language identifier", null, l)));

        // 4. Inside `patterns:` list — offer pattern operators
        if (IsInsideBlock(lines, lineIdx, effectiveIndent, "patterns"))
            return MakeList(PatternsItemKeys.Select(k =>
                MakeItem(k + ":", 10, null, FieldDocs.GetValueOrDefault(k), k + ": ")));

        // 5. Inside a rule block — offer rule field names
        if (IsInsideRuleBlock(lines, lineIdx, effectiveIndent, effectiveKeyCol))
            return MakeList(AllRuleKeys.Select(k =>
                MakeItem(k + ":", 10, null, FieldDocs.GetValueOrDefault(k), k + ": ")));

        // 6. Top-level — offer `rules:`
        if (effectiveIndent == 0)
            return MakeList([MakeItem("rules:", 10, "Top-level list of Opengrep rules",
                null, "rules:\n  - id: ")]);

        return EmptyCompletionList();
    }

    /// <summary>Returns true if the current position (lineIdx, indent) is inside a block keyed by `key`.</summary>
    private static bool IsInsideBlock(YamlLine[] lines, int lineIdx, int indent, string key)
    {
        for (int i = lineIdx - 1; i >= 0; i--)
        {
            var l = lines[i];
            if (l.Key is null && l.InlineValue is null) continue; // skip blank/comment
            if (l.Indent < indent)
                return l.Key is not null && string.Equals(l.Key, key, StringComparison.Ordinal);
            if (l.Indent == indent && !l.IsList)
                return false; // sibling key, different block
        }
        return false;
    }

    /// <summary>Returns true if the position is inside a rule block (child of a `rules:` list item).</summary>
    private static bool IsInsideRuleBlock(YamlLine[] lines, int lineIdx, int indent, int keyCol)
    {
        // If the current line is itself a list item, check whether its parent is `rules:`
        // (handles typing a new rule field at the `  - ` level).
        if (lineIdx < lines.Length && lines[lineIdx].IsList)
        {
            for (int j = lineIdx - 1; j >= 0; j--)
            {
                var p = lines[j];
                if (p.Key is null && p.InlineValue is null) continue;
                if (p.Indent < lines[lineIdx].Indent)
                    return string.Equals(p.Key, "rules", StringComparison.Ordinal);
            }
            return false;
        }

        // Walk upward looking for the nearest list item at lower indent
        for (int i = lineIdx - 1; i >= 0; i--)
        {
            var l = lines[i];
            if (l.Key is null && l.InlineValue is null) continue;

            if (l.IsList && l.Indent < indent)
            {
                // Found a parent list item; check that its parent is `rules:`
                for (int j = i - 1; j >= 0; j--)
                {
                    var p = lines[j];
                    if (p.Key is null && p.InlineValue is null) continue;
                    if (p.Indent < l.Indent)
                        return string.Equals(p.Key, "rules", StringComparison.Ordinal);
                }
                return false;
            }

            if (!l.IsList && l.Indent < indent)
                return false; // entered a non-list block
        }

        return false;
    }

    private static JsonObject MakeItem(string label, int kind, string? detail, string? mdDoc, string? insertText)
    {
        var item = new JsonObject { ["label"] = label, ["kind"] = kind };
        if (detail is not null) item["detail"] = detail;
        if (mdDoc is not null)
            item["documentation"] = new JsonObject { ["kind"] = "markdown", ["value"] = mdDoc };
        if (insertText is not null) item["insertText"] = insertText;
        return item;
    }

    private static JsonObject MakeList(IEnumerable<JsonNode> items)
    {
        var arr = new JsonArray();
        foreach (var i in items) arr.Add(i);
        return new JsonObject { ["isIncomplete"] = false, ["items"] = arr };
    }

    private static JsonObject EmptyCompletionList() =>
        new() { ["isIncomplete"] = false, ["items"] = new JsonArray() };

    private static string SeverityDetail(string s) => s switch
    {
        "ERROR" => "Fails CI (exit code 1)",
        "WARNING" => "Shown in output, does not fail CI",
        "INFO" => "Informational only",
        _ => ""
    };

    // ── Hover ─────────────────────────────────────────────────────────────────────

    public static JsonNode? GetHover(string content, int lineIdx, int character)
    {
        var lines = ParseLines(content);
        if (lineIdx >= lines.Length) return null;

        var line = lines[lineIdx];
        if (line.Key is null) return null;

        // Only trigger hover when cursor is over the key text
        if (character < line.KeyCol || character > line.KeyEndCol) return null;

        if (!FieldDocs.TryGetValue(line.Key, out var doc)) return null;

        return new JsonObject
        {
            ["contents"] = new JsonObject { ["kind"] = "markdown", ["value"] = doc },
            ["range"] = MakeRange(lineIdx, line.KeyCol, lineIdx, line.KeyEndCol)
        };
    }

    private static JsonObject MakeRange(int sl, int sc, int el, int ec) => new()
    {
        ["start"] = new JsonObject { ["line"] = sl, ["character"] = sc },
        ["end"] = new JsonObject { ["line"] = el, ["character"] = ec }
    };
}
