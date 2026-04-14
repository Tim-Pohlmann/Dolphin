namespace Dolphin.Lsp;

/// <summary>
/// Native C# validator for Dolphin/Opengrep YAML rule files.
///
/// Parses the file line-by-line (no NuGet YAML library — project is PublishTrimmed=true)
/// and returns LSP diagnostics for structural problems without spawning an external process.
///
/// A valid rules file must satisfy:
///   - Top-level "rules:" key with a sequence of mappings.
///   - Each rule must have: id, message, languages (non-empty), severity (one of the
///     recognised values), and at least one pattern key.
/// </summary>
internal static class YamlRuleValidator
{
    // Recognised severity values (case-sensitive, as Opengrep requires).
    private static readonly HashSet<string> ValidSeverities =
        ["ERROR", "WARNING", "INFO", "ERROR_TODO", "INFO_TODO"];

    // Pattern keys accepted as the "has a pattern" check.
    private static readonly HashSet<string> PatternKeys =
    [
        "pattern",
        "patterns",
        "pattern-either",
        "pattern-regex",
        "pattern-inside",
        "pattern-not-inside",
        "pattern-not",
        "r2c-internal-project-depends-on",
    ];

    /// <summary>
    /// Validates <paramref name="text"/> and returns zero or more <see cref="LspDiagnostic"/>
    /// records describing structural problems found.  An empty array means the file is valid.
    /// </summary>
    public static LspDiagnostic[] Validate(string text)
    {
        var diagnostics = new List<LspDiagnostic>();
        var lines = SplitLines(text);

        // ── Step 1: must have a top-level "rules:" key ────────────────────────
        int rulesLineIndex = FindTopLevelRulesKey(lines);
        if (rulesLineIndex < 0)
        {
            diagnostics.Add(MakeDiagnostic(0, 0, "Missing required top-level 'rules:' key."));
            return [.. diagnostics];
        }

        // ── Step 2: collect each rule block and validate it ───────────────────
        var rules = CollectRuleBlocks(lines, rulesLineIndex);
        if (rules.Count == 0)
        {
            // "rules:" present but sequence is empty — that is technically valid per the
            // schema, but opengrep validate also accepts it (exit 0).  No diagnostics.
            return [];
        }

        foreach (var rule in rules)
            ValidateRule(rule, diagnostics);

        return [.. diagnostics];
    }

    // ── Line-splitting ────────────────────────────────────────────────────────

    /// <summary>
    /// Splits <paramref name="text"/> into lines, stripping the CR/LF terminators.
    /// Returns 0-based line index alongside each line's content.
    /// </summary>
    private static List<(int LineIndex, string Content)> SplitLines(string text)
    {
        var result = new List<(int, string)>();
        int start = 0;
        int lineIndex = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || text[i] == '\n' || text[i] == '\r')
            {
                result.Add((lineIndex, text[start..i]));
                lineIndex++;
                if (i < text.Length && text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++; // skip LF of CRLF
                start = i + 1;
            }
        }
        return result;
    }

    // ── Top-level "rules:" detection ──────────────────────────────────────────

    private static int FindTopLevelRulesKey(List<(int LineIndex, string Content)> lines)
    {
        foreach (var (lineIndex, content) in lines)
        {
            var trimmed = content.TrimEnd();
            if (trimmed == "rules:" || trimmed.StartsWith("rules: ", StringComparison.Ordinal))
                return lineIndex;
        }
        return -1;
    }

    // ── Rule block collection ─────────────────────────────────────────────────

    /// <summary>
    /// Collects each rule block (the lines under a "  - id: …" entry under rules:).
    /// A rule block starts at the "  - " line and ends just before the next "  - " line
    /// or the end of the file.
    /// </summary>
    private static List<RuleBlock> CollectRuleBlocks(
        List<(int LineIndex, string Content)> lines, int rulesLineIndex)
    {
        var result = new List<RuleBlock>();

        // Find lines that start a new rule: exactly 2-space indent + "- " at the top level.
        // We look for the pattern /^  - / after the "rules:" line.
        RuleBlock? current = null;

        foreach (var (lineIndex, content) in lines)
        {
            if (lineIndex <= rulesLineIndex) continue;

            // Detect a new rule entry: starts with "  - " (2 spaces then "- ") or is "  -\n".
            if (content.Length >= 4 && content[0] == ' ' && content[1] == ' '
                && content[2] == '-' && (content[3] == ' ' || content[3] == '\t'))
            {
                if (current is not null) result.Add(current);
                current = new RuleBlock(lineIndex);
                current.Lines.Add((lineIndex, content));
                continue;
            }

            // A line at column 0 that is not empty and not a comment terminates the rules block.
            if (content.Length > 0 && content[0] != ' ' && content[0] != '\t' && content[0] != '#')
                break;

            current?.Lines.Add((lineIndex, content));
        }

        if (current is not null) result.Add(current);
        return result;
    }

    // ── Per-rule validation ───────────────────────────────────────────────────

    private static void ValidateRule(RuleBlock rule, List<LspDiagnostic> diagnostics)
    {
        bool hasId        = false;
        bool hasMessage   = false;
        bool hasLanguages = false;
        bool hasSeverity  = false;
        bool hasPattern   = false;

        int  idLine        = rule.StartLine;
        int? languagesLine = null;
        int? severityLine  = null;

        string? severityValue = null;

        foreach (var (lineIndex, content) in rule.Lines)
        {
            // Strip inline YAML comments: '#' is a comment only when preceded by whitespace
            // (or at the start of the line). This avoids incorrectly stripping '#' that
            // appears inside a quoted string value such as `message: "Issue #123"`.
            var effective = content;
            for (int ci = 0; ci < content.Length; ci++)
            {
                if (content[ci] == '#' && (ci == 0 || content[ci - 1] == ' ' || content[ci - 1] == '\t'))
                {
                    effective = content[..ci];
                    break;
                }
            }

            var trimmed = effective.TrimEnd();

            // id: …
            if (TryGetSimpleValue(trimmed, "id", out _))
            {
                hasId = true;
                idLine = lineIndex;
            }

            // message: …
            if (TryGetSimpleValue(trimmed, "message", out _))
                hasMessage = true;

            // languages: […] — inline flow sequence or block sequence start
            if (TryGetSimpleValue(trimmed, "languages", out var langsValue))
            {
                languagesLine = lineIndex;
                // Non-empty inline list: value is something other than [] or whitespace.
                var afterColon = (langsValue ?? string.Empty).Trim();
                if (afterColon.Length > 0 && afterColon != "[]")
                    hasLanguages = true;
                // If afterColon is empty the languages may be on following lines (block seq).
                // Treat that as having languages; improper handling would cause false positives.
                if (afterColon.Length == 0)
                    hasLanguages = true; // block sequence — assume non-empty
            }

            // severity: …
            if (TryGetSimpleValue(trimmed, "severity", out var sev))
            {
                severityLine = lineIndex;
                severityValue = sev?.Trim();
                hasSeverity = !string.IsNullOrWhiteSpace(severityValue);
            }

            // pattern keys
            foreach (var key in PatternKeys)
            {
                if (trimmed.TrimStart().StartsWith(key + ":", StringComparison.Ordinal) ||
                    trimmed.TrimStart().StartsWith(key + " ", StringComparison.Ordinal))
                {
                    hasPattern = true;
                    break;
                }
            }
        }

        // ── Emit diagnostics for missing / invalid fields ─────────────────────

        if (!hasId)
            diagnostics.Add(MakeDiagnostic(rule.StartLine, 0, "Rule is missing required field 'id'."));

        if (!hasMessage)
            diagnostics.Add(MakeDiagnostic(idLine, 0, $"Rule '{GetRuleId(rule)}' is missing required field 'message'."));

        if (!hasLanguages)
            diagnostics.Add(MakeDiagnostic(languagesLine ?? idLine, 0,
                $"Rule '{GetRuleId(rule)}' is missing required field 'languages' (must be a non-empty list)."));

        if (!hasSeverity)
        {
            diagnostics.Add(MakeDiagnostic(severityLine ?? idLine, 0,
                $"Rule '{GetRuleId(rule)}' is missing required field 'severity'."));
        }
        else if (!ValidSeverities.Contains(severityValue!))
        {
            diagnostics.Add(MakeDiagnostic(severityLine!.Value, 0,
                $"Rule '{GetRuleId(rule)}' has invalid severity '{severityValue}'. " +
                $"Expected one of: {string.Join(", ", ValidSeverities)}."));
        }

        if (!hasPattern)
            diagnostics.Add(MakeDiagnostic(idLine, 0,
                $"Rule '{GetRuleId(rule)}' is missing a pattern key (e.g. 'pattern', 'patterns', 'pattern-regex', …)."));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Tries to extract the scalar value after <c>key:</c> from a trimmed YAML line.
    /// For example <c>"    severity: ERROR"</c> → key="severity", value="ERROR".
    /// Also handles lines that start with a YAML list item prefix, e.g.
    /// <c>"  - id: foo"</c> → key="id", value="foo".
    /// Returns false if the key is not present.
    /// </summary>
    private static bool TryGetSimpleValue(string trimmedLine, string key, out string? value)
    {
        // Strip leading whitespace, then also strip a YAML list-item prefix ("- ")
        // so that "  - id: foo" is treated the same as "    id: foo".
        var stripped = trimmedLine.TrimStart();
        if (stripped.StartsWith("- ", StringComparison.Ordinal))
            stripped = stripped[2..];
        else if (stripped == "-")
            stripped = string.Empty;

        if (stripped.StartsWith(key + ":", StringComparison.Ordinal))
        {
            value = stripped[(key.Length + 1)..].Trim(' ', '"', '\'');
            return true;
        }
        value = null;
        return false;
    }

    private static string GetRuleId(RuleBlock rule)
    {
        foreach (var (_, content) in rule.Lines)
        {
            if (TryGetSimpleValue(content.TrimEnd(), "id", out var id) && id is not null)
                return id;
        }
        return "<unknown>";
    }

    private static LspDiagnostic MakeDiagnostic(int line, int col, string message)
    {
        var pos = new LspPosition(line, col);
        return new LspDiagnostic(
            Range: new LspRange(pos, pos),
            Severity: 1,
            Source: "dolphin",
            Message: message,
            Pending: false);
    }

    // ── Inner types ───────────────────────────────────────────────────────────

    private sealed class RuleBlock(int startLine)
    {
        public int StartLine { get; } = startLine;
        public List<(int LineIndex, string Content)> Lines { get; } = [];
    }
}
