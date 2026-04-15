using System.Text.Json.Nodes;
using Json.Schema;
using YamlDotNet.RepresentationModel;

namespace Dolphin.Lsp;

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
        using var stream = assembly.GetManifestResourceStream("Dolphin.Lsp.semgrep-schema.json")
            ?? throw new InvalidOperationException(
                "Embedded resource 'Dolphin.Lsp.semgrep-schema.json' not found.");
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }

    /// <summary>
    /// Validates <paramref name="text"/> and returns zero or more <see cref="LspDiagnostic"/>
    /// records describing structural problems found.  An empty array means the file is valid.
    /// </summary>
    public static LspDiagnostic[] Validate(string text)
    {
        var diagnostics = new List<LspDiagnostic>();

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
            diagnostics.Add(MakeDiagnostic(line, 0, $"YAML syntax error: {ex.Message}"));
            return [.. diagnostics];
        }

        // ── Convert YAML DOM to JsonNode ──────────────────────────────────────
        var jsonNode = ConvertToJson(rootNode, "", lineMap);

        // ── Validate against the embedded Semgrep schema ──────────────────────
        var options = new EvaluationOptions { OutputFormat = OutputFormat.List };
        // JsonSchema.Net 9.x Evaluate takes JsonElement.  Serialise via JsonNode.WriteTo
        // into a UTF-8 buffer — no reflection, fully trim-safe, avoids a string allocation.
        EvaluationResults result;
        {
            var buffer = new System.Buffers.ArrayBufferWriter<byte>(4096);
            using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
            {
                if (jsonNode is null) writer.WriteNullValue();
                else jsonNode.WriteTo(writer);
            }
            using var doc = System.Text.Json.JsonDocument.Parse(buffer.WrittenMemory);
            result = _schema.Value.Evaluate(doc.RootElement, options);
        }
        if (result.IsValid) return [];

        // ── Map validation errors to LSP diagnostics ──────────────────────────
        ProcessValidationResults(result, lineMap, diagnostics);

        return [.. diagnostics];
    }

    private static void ProcessValidationResults(
        EvaluationResults result,
        Dictionary<string, int> lineMap,
        List<LspDiagnostic> diagnostics)
    {
        var seen = new HashSet<string>(); // deduplicate identical message+line pairs

        // Track instance paths where ALL else/oneOf branches fail → missing pattern key.
        // We emit ONE human-friendly message per affected rule instead of one per branch.
        var missingPatternPaths = new HashSet<string>();

        foreach (var detail in result.Details!.Where(d => !d.IsValid && d.Errors is not null && d.Errors.Count > 0))
        {
            var instancePath = detail.InstanceLocation.ToString();
            var evalPath     = detail.EvaluationPath.ToString();

            // Collect "else/oneOf/N" branch failures → will emit one missing-pattern message.
            if (ContainsIndexedSegment(evalPath, "/else/oneOf/"))
            {
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
        List<LspDiagnostic> diagnostics)
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
        List<LspDiagnostic> diagnostics)
    {
        foreach (var path in missingPatternPaths)
        {
            var line = lineMap.TryGetValue(path, out var l) ? l : 0;
            const string msg =
                "Rule is missing a required pattern key " +
                "(e.g. 'pattern', 'patterns', 'pattern-regex', 'pattern-either').";
            if (seen.Add($"{line}:{msg}"))
                diagnostics.Add(MakeDiagnostic(line, 0, msg));
        }
    }

    // ── Error suppression ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns true for errors that are evaluation noise rather than real validation failures.
    ///
    /// JsonSchema.Net in List output includes failures from:
    /// <list type="bullet">
    ///   <item><c>if</c> condition evaluations (path ends with "/if")</item>
    ///   <item>Individual <c>anyOf</c> / <c>oneOf</c> branch alternatives that do not match</item>
    /// </list>
    /// These should not surface as user-visible LSP diagnostics.
    /// </summary>
    private static bool ShouldSuppressError(string evalPath)
    {
        // (a) Skip errors from 'if' condition evaluations.
        //     In List output the path ends at the '/if' segment itself.
        if (evalPath.EndsWith("/if", StringComparison.Ordinal)) return true;

        // (b) Skip errors from anyOf/oneOf indexed branch alternatives.
        //     These represent "this branch doesn't match" — expected during branch selection.
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
        string keyword, JsonNode? errorNode, string instancePath)
    {
        switch (keyword)
        {
            case "required":
            {
                // Error node is a string like "Required properties ["id"] are not present"
                var raw = errorNode?.ToString();
                if (!string.IsNullOrEmpty(raw))
                    yield return raw;
                break;
            }

            case "enum":
            {
                // Include the field name since the raw schema message doesn't mention it
                var field = LastSegment(instancePath);
                yield return $"Invalid value for '{field}'. See allowed values in the Semgrep rule schema.";
                break;
            }

            // All combinator / meta keywords are filtered before we get here, but
            // list them defensively so they don't fall into the default case.
            case "if":
            case "then":
            case "else":
            case "allOf":
            case "anyOf":
            case "oneOf":
            case "not":
                break;

            default:
                // Emit unknown keyword errors only when the error is a plain string
                var str = errorNode?.GetValueKind() == System.Text.Json.JsonValueKind.String
                    ? errorNode.GetValue<string>()
                    : null;
                if (!string.IsNullOrEmpty(str))
                    yield return str;
                break;
        }
    }

    // ── YAML → JsonNode conversion ────────────────────────────────────────────

    // RFC 6901 JSON Pointer segment separator
    private const char JsonPointerSep = '/';

    private static JsonNode? ConvertToJson(YamlNode node, string path, Dictionary<string, int> lineMap)
    {
        // YamlDotNet uses 1-based line numbers (long in v17); convert to 0-based int for LSP
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

        // Unquoted scalars: apply YAML 1.1 type coercion rules
        if (value is "true"  or "True"  or "TRUE")  return JsonValue.Create(true);
        if (value is "false" or "False" or "FALSE") return JsonValue.Create(false);
        if (value is "null"  or "Null"  or "NULL"  or "~") return null;

        if (long.TryParse(value,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var l))
            return JsonValue.Create(l);

        if (double.TryParse(value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d))
            return JsonValue.Create(d);

        return JsonValue.Create(value);
    }

    /// <summary>
    /// Returns true when the scalar is quoted or uses a block style, meaning its value should
    /// be preserved as a string without YAML type-coercion (e.g. "true" stays a string).
    /// </summary>
    private static bool IsQuotedStyle(YamlDotNet.Core.ScalarStyle style) =>
        style is YamlDotNet.Core.ScalarStyle.SingleQuoted
              or YamlDotNet.Core.ScalarStyle.DoubleQuoted
              or YamlDotNet.Core.ScalarStyle.Literal
              or YamlDotNet.Core.ScalarStyle.Folded;

    private static JsonObject ConvertMapping(
        YamlMappingNode mapping, string path, Dictionary<string, int> lineMap)
    {
        var obj = new JsonObject();
        var nonScalarIndex = 0; // ensures unique keys for non-scalar map entries
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

    private static string LastSegment(string jsonPointerPath)
    {
        var idx = jsonPointerPath.LastIndexOf('/');
        return idx >= 0 ? jsonPointerPath[(idx + 1)..] : jsonPointerPath;
    }

    /// <summary>
    /// Escapes a JSON Pointer segment per RFC 6901:
    /// '~' → '~0', '/' → '~1'.
    /// </summary>
    private static string EscapeJsonPointerSegment(string segment) =>
        segment.Replace("~", "~0").Replace("/", "~1");

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
}
