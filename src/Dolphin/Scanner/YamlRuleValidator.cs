using System.Text.Json.Nodes;
using Json.Schema;
using YamlDotNet.RepresentationModel;

namespace Dolphin.Scanner;

/// <summary>
/// Validates Dolphin/Opengrep YAML rule files against the official Semgrep JSON Schema.
///
/// Uses YamlDotNet for correct YAML parsing and JsonSchema.Net for schema-based validation,
/// mapping validation errors back to YAML source line numbers.
///
/// The schema is embedded as a resource (<c>semgrep-schema.json</c>) and loaded once via a
/// lazy initialiser.  YAML is converted to a <c>JsonNode</c> tree while recording
/// JSON-Pointer → 0-based-line mappings; the tree is then serialised to <c>JsonElement</c>
/// for <c>JsonSchema.Evaluate</c>.
/// </summary>
internal static class YamlRuleValidator
{
    private static readonly Lazy<JsonSchema> _schema = new(LoadSchema);

    private static JsonSchema LoadSchema()
    {
        var assembly = typeof(YamlRuleValidator).Assembly;
        using var stream = assembly.GetManifestResourceStream("Dolphin.Scanner.semgrep-schema.json")
            ?? throw new InvalidOperationException(
                "Embedded resource 'Dolphin.Scanner.semgrep-schema.json' not found.");
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }

    /// <summary>
    /// Validates <paramref name="text"/> and returns zero or more <see cref="ValidationDiagnostic"/>
    /// records describing structural problems found.  An empty array means the file is valid.
    /// </summary>
    public static ValidationDiagnostic[] Validate(string text)
    {
        var nonAscii = FindNonAsciiDiagnostic(text);
        if (nonAscii is not null) return nonAscii;

        var diagnostics = new List<ValidationDiagnostic>();

        // ── Parse YAML and build a JSON-pointer → 0-based-line-number map ────
        YamlNode rootNode;
        var lineMap = new Dictionary<string, int>(); // JSON pointer path → 0-based line

        try
        {
            var yamlStream = new YamlStream();
            yamlStream.Load(new StringReader(text));

            if (yamlStream.Documents.Count == 0)
            {
                // Empty file — return a single "missing rules" error at line 0
                diagnostics.Add(MakeDiagnostic(0, 0, "Missing required top-level 'rules:' key."));
                return [.. diagnostics];
            }

            if (yamlStream.Documents.Count > 1)
            {
                diagnostics.Add(MakeDiagnostic(0, 0, "Rule files must contain exactly one YAML document."));
                return [.. diagnostics];
            }

            rootNode = yamlStream.Documents[0].RootNode;
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            var line = (int)Math.Clamp(ex.Start.Line - 1, 0, int.MaxValue);
            var character = (int)Math.Clamp(ex.Start.Column - 1, 0, int.MaxValue);
            diagnostics.Add(MakeDiagnostic(line, character, $"YAML syntax error: {ex.Message}"));
            return [.. diagnostics];
        }

        // ── Convert YAML DOM to JsonNode ──────────────────────────────────────
        var jsonNode = ConvertToJson(rootNode, "", lineMap);

        // ── Validate against the embedded Semgrep schema ──────────────────────
        var options = new EvaluationOptions { OutputFormat = OutputFormat.List };
        var result = EvaluateAgainstSchema(jsonNode, options);
        if (result.IsValid) return [];

        // ── Map validation errors to diagnostics ──────────────────────────────
        ProcessValidationResults(result, lineMap, diagnostics);

        return [.. diagnostics];
    }

    /// <summary>
    /// Scans <paramref name="text"/> in a single pass and returns a one-element diagnostic array
    /// pointing at the first non-ASCII character, or <c>null</c> if all characters are ASCII.
    /// </summary>
    internal static ValidationDiagnostic[]? FindNonAsciiDiagnostic(string text)
    {
        int line = 0, col = 0;
        bool skipNextLf = false; // Skip LF if we just saw CR (handles CRLF as single newline)
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch > 127)
            {
                var (codePoint, utf16Len) = DecodeNonAscii(ch, text, i);
                var pos = new ValidationPosition(line, col);
                return [new ValidationDiagnostic(
                    Range: new ValidationRange(pos, new ValidationPosition(line, col + utf16Len)),
                    Severity: 1,
                    Source: "dolphin",
                    Message: $"Non-ASCII character (U+{codePoint:X4}): Dolphin rules files must contain only ASCII characters.",
                    Pending: false)];
            }

            // Skip LF that follows CR (CRLF sequence)
            if (skipNextLf && ch == '\n')
            {
                skipNextLf = false;
                continue;
            }
            skipNextLf = false;

            if (ch == '\r')
            {
                line++;
                col = 0;
                skipNextLf = true;
            }
            else if (ch == '\n')
            {
                line++;
                col = 0;
            }
            else
            {
                col++;
            }
        }
        return null;
    }

    private static (int CodePoint, int Utf16Len) DecodeNonAscii(char ch, string text, int i)
    {
        if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            return (char.ConvertToUtf32(ch, text[i + 1]), 2);
        if (System.Text.Rune.TryCreate(ch, out var rune))
            return (rune.Value, rune.Utf16SequenceLength);
        // Unpaired surrogate — report the raw UTF-16 code unit.
        return (ch, 1);
    }

    /// <summary>
    /// JsonSchema.Net 9.x Evaluate takes JsonElement. Serialises via JsonNode.WriteTo into a
    /// UTF-8 buffer — no reflection, fully trim-safe, avoids a string allocation.
    /// </summary>
    private static EvaluationResults EvaluateAgainstSchema(JsonNode? jsonNode, EvaluationOptions options)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(4096);
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            if (jsonNode is null) writer.WriteNullValue();
            else jsonNode.WriteTo(writer);
        }
        using var doc = System.Text.Json.JsonDocument.Parse(buffer.WrittenMemory);
        return _schema.Value.Evaluate(doc.RootElement, options);
    }

    private static void ProcessValidationResults(
        EvaluationResults result,
        Dictionary<string, int> lineMap,
        List<ValidationDiagnostic> diagnostics)
    {
        var seen = new HashSet<string>(); // deduplicate identical message+line pairs

        // Track instance paths where ALL else/oneOf branches fail → missing pattern key.
        // We emit ONE human-friendly message per affected rule instead of one per branch.
        var missingPatternPaths = new HashSet<string>();

        // Pre-pass: instance paths where the `/else` combinator itself succeeded (one of its
        // branches matched). For those paths the individual branch failures are evaluation
        // noise, not a "missing pattern key" signal — e.g. a rule using `match:` still reports
        // failures for the five non-match branches.
        var passingElsePaths = new HashSet<string>();
        foreach (var d in result.Details!)
            if (d.IsValid && d.EvaluationPath.ToString().EndsWith("/else", StringComparison.Ordinal))
                passingElsePaths.Add(d.InstanceLocation.ToString());

        foreach (var detail in result.Details!.Where(d => !d.IsValid && d.Errors is not null && d.Errors.Count > 0))
        {
            var instancePath = detail.InstanceLocation.ToString();
            var evalPath     = detail.EvaluationPath.ToString();

            // Collect "else/oneOf/N" branch failures → will emit one missing-pattern message,
            // but only when the parent `/else` combinator itself failed (otherwise it's noise).
            if (ContainsIndexedSegment(evalPath, "/else/oneOf/"))
            {
                if (!passingElsePaths.Contains(instancePath))
                    missingPatternPaths.Add(instancePath);
                continue;
            }

            // Suppress noise from if-condition checks and oneOf/anyOf branch alternatives.
            if (ShouldSuppressError(evalPath)) continue;

            AddDetailDiagnostics(detail, instancePath, lineMap, seen, diagnostics);
        }

        AddMissingPatternDiagnostics(missingPatternPaths, lineMap, seen, diagnostics);
    }

    private static void AddDetailDiagnostics(
        EvaluationResults detail,
        string instancePath,
        Dictionary<string, int> lineMap,
        HashSet<string> seen,
        List<ValidationDiagnostic> diagnostics)
    {
        var line = lineMap.TryGetValue(instancePath, out var l) ? l : 0;
        foreach (var (keyword, errorNode) in detail.Errors!)
        {
            foreach (var msg in FormatKeywordError(keyword, errorNode, instancePath).Where(msg => seen.Add($"{line}:{msg}")))
                diagnostics.Add(MakeDiagnostic(line, 0, msg));
        }
    }

    private static void AddMissingPatternDiagnostics(
        HashSet<string> missingPatternPaths,
        Dictionary<string, int> lineMap,
        HashSet<string> seen,
        List<ValidationDiagnostic> diagnostics)
    {
        foreach (var path in missingPatternPaths)
        {
            var line = lineMap.TryGetValue(path, out var l) ? l : 0;
            const string msg =
                "Rule is missing a required pattern key " +
                "(e.g. 'pattern', 'patterns', 'pattern-regex', 'pattern-either', 'match').";
            if (seen.Add($"{line}:{msg}"))
                diagnostics.Add(MakeDiagnostic(line, 0, msg));
        }
    }

    // ── Error suppression ─────────────────────────────────────────────────────

    private static bool ShouldSuppressError(string evalPath)
    {
        if (evalPath.EndsWith("/if", StringComparison.Ordinal)) return true;

        if (ContainsIndexedSegment(evalPath, "/anyOf/") ||
            ContainsIndexedSegment(evalPath, "/oneOf/"))
            return true;

        return false;
    }

    /// <summary>Returns true when <paramref name="path"/> contains <paramref name="segment"/>
    /// immediately followed by a digit (e.g. "/oneOf/0" or "/else/oneOf/2").</summary>
    private static bool ContainsIndexedSegment(string path, string segment)
    {
        int idx = 0;
        while ((idx = path.IndexOf(segment, idx, StringComparison.Ordinal)) >= 0)
        {
            int after = idx + segment.Length;
            if (after < path.Length && char.IsDigit(path[after])) return true;
            idx = after;
        }
        return false;
    }

    // ── Error formatting ──────────────────────────────────────────────────────

    private static IEnumerable<string> FormatKeywordError(
        string keyword, System.Text.Json.Nodes.JsonNode? errorNode, string instancePath)
    {
        switch (keyword)
        {
            case "required":
            {
                var raw = GetStringValue(errorNode);
                if (!string.IsNullOrEmpty(raw))
                    yield return raw;
                break;
            }

            case "enum":
            {
                var field = LastSegment(instancePath);
                yield return string.IsNullOrEmpty(field)
                    ? "Invalid value. See allowed values in the Semgrep rule schema."
                    : $"Invalid value for '{field}'. See allowed values in the Semgrep rule schema.";
                break;
            }

            case "anyOf":
            case "oneOf":
            {
                var raw = GetStringValue(errorNode);
                if (!string.IsNullOrEmpty(raw))
                {
                    yield return raw;
                    break;
                }

                var field = LastSegment(instancePath);
                yield return string.IsNullOrEmpty(field)
                    ? "Value does not match the expected schema shape."
                    : $"Invalid value for '{field}': value does not match any allowed schema shape.";
                break;
            }

            case "if":
            case "then":
            case "else":
            case "allOf":
            case "not":
                break;

            default:
                var str = GetStringValue(errorNode);
                if (!string.IsNullOrEmpty(str))
                    yield return str;
                break;
        }
    }

    private static string? GetStringValue(System.Text.Json.Nodes.JsonNode? node) =>
        node?.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? node.GetValue<string>()
            : null;

    // ── YAML → JsonNode conversion ────────────────────────────────────────────

    private const char JsonPointerSep = '/';

    private static JsonNode? ConvertToJson(YamlNode node, string path, Dictionary<string, int> lineMap)
    {
        // YamlDotNet uses 1-based line numbers (long in v17); convert to 0-based int
        lineMap[path] = (int)Math.Clamp(node.Start.Line - 1, 0, int.MaxValue);

        return node switch
        {
            YamlScalarNode scalar   => ConvertScalar(scalar),
            YamlMappingNode mapping => ConvertMapping(mapping, path, lineMap),
            YamlSequenceNode seq    => ConvertSequence(seq, path, lineMap),
            _                       => null
        };
    }

    private static JsonValue? ConvertScalar(YamlScalarNode scalar)
    {
        var value = scalar.Value;
        if (value is null) return null;

        // Preserve quoted strings as-is (don't coerce "true", integers, etc.)
        if (IsQuotedStyle(scalar.Style))
            return JsonValue.Create(value);

        // Unquoted scalars: coerce true/false/null and numeric literals (not yes/no/on/off)
        if (value is "true"  or "True"  or "TRUE")  return JsonValue.Create(true);
        if (value is "false" or "False" or "FALSE") return JsonValue.Create(false);
        if (value is "null"  or "Null"  or "NULL"  or "~") return null;

        if (long.TryParse(value,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var l))
            return JsonValue.Create(l);

        if (double.TryParse(value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d)
            && double.IsFinite(d))
            return JsonValue.Create(d);

        return JsonValue.Create(value);
    }

    private static bool IsQuotedStyle(YamlDotNet.Core.ScalarStyle style) =>
        style is YamlDotNet.Core.ScalarStyle.SingleQuoted
              or YamlDotNet.Core.ScalarStyle.DoubleQuoted
              or YamlDotNet.Core.ScalarStyle.Literal
              or YamlDotNet.Core.ScalarStyle.Folded;

    private static JsonObject ConvertMapping(
        YamlMappingNode mapping, string path, Dictionary<string, int> lineMap)
    {
        var obj = new JsonObject();
        var nonScalarIndex = 0;
        foreach (var (keyNode, valueNode) in mapping)
        {
            var key = keyNode switch
            {
                YamlScalarNode scalar => scalar.Value ?? string.Empty,
                YamlSequenceNode      => $"[sequence-key-{nonScalarIndex++}]",
                YamlMappingNode       => $"{{mapping-key-{nonScalarIndex++}}}",
                _                     => $"[key-{nonScalarIndex++}]"
            };
            var childPath = path + JsonPointerSep + EscapeJsonPointerSegment(key);
            obj[key] = ConvertToJson(valueNode, childPath, lineMap);
        }
        return obj;
    }

    private static JsonArray ConvertSequence(
        YamlSequenceNode seq, string path, Dictionary<string, int> lineMap)
    {
        var arr   = new JsonArray();
        var index = 0;
        foreach (var item in seq)
        {
            var childPath = path + JsonPointerSep + index++;
            arr.Add(ConvertToJson(item, childPath, lineMap));
        }
        return arr;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the last non-numeric segment of a JSON Pointer path, unescaped per RFC 6901.
    /// </summary>
    internal static string LastSegment(string jsonPointerPath)
    {
        var segments = jsonPointerPath.Split('/');
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            if (segments[i].Length == 0) continue;
            if (IsArrayIndex(segments[i])) continue;
            return UnescapeJsonPointerSegment(segments[i]);
        }
        return string.Empty;
    }

    private static bool IsArrayIndex(string segment) =>
        segment.Length > 0 && segment.All(char.IsAsciiDigit);

    private static string EscapeJsonPointerSegment(string segment) =>
        segment.Replace("~", "~0").Replace("/", "~1");

    internal static string UnescapeJsonPointerSegment(string segment) =>
        segment.Replace("~1", "/").Replace("~0", "~");

    private static ValidationDiagnostic MakeDiagnostic(int line, int col, string message)
    {
        var pos = new ValidationPosition(line, col);
        return new ValidationDiagnostic(
            Range: new ValidationRange(pos, pos),
            Severity: 1,
            Source: "dolphin",
            Message: message,
            Pending: false);
    }
}
