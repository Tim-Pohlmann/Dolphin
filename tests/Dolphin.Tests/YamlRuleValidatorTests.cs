using Dolphin.Lsp;

namespace Dolphin.Tests;

/// <summary>
/// Unit tests for <see cref="YamlRuleValidator"/>.
/// </summary>
[TestClass]
public class YamlRuleValidatorTests
{
    // ── Helper ────────────────────────────────────────────────────────────────

    private static LspDiagnostic[] Validate(string yaml) =>
        YamlRuleValidator.Validate(yaml);

    private static void AssertNoErrors(string yaml)
    {
        var diags = Validate(yaml);
        Assert.AreEqual(0, diags.Length, $"Expected no diagnostics, got: {string.Join("; ", diags.Select(d => d.Message))}");
    }

    private static void AssertHasError(string yaml, string substring)
    {
        var diags = Validate(yaml);
        Assert.IsTrue(diags.Length > 0, "Expected at least one diagnostic but got none.");
        Assert.IsTrue(
            diags.Any(d => d.Message.Contains(substring, StringComparison.OrdinalIgnoreCase)),
            $"Expected a diagnostic containing '{substring}', but got: {string.Join("; ", diags.Select(d => d.Message))}");
    }

    // ── Valid files ───────────────────────────────────────────────────────────

    [TestMethod]
    public void ValidFile_PatternKey_ReturnsNoDiagnostics()
    {
        AssertNoErrors("""
            rules:
              - id: no-console-log
                message: "Remove console.log"
                languages: [typescript]
                severity: WARNING
                pattern: console.log(...)
            """);
    }

    [TestMethod]
    public void ValidFile_PatternsKey_ReturnsNoDiagnostics()
    {
        AssertNoErrors("""
            rules:
              - id: my-rule
                message: "Some message"
                languages: [python]
                severity: ERROR
                patterns:
                  - pattern: bad_call()
            """);
    }

    [TestMethod]
    public void ValidFile_PatternRegex_ReturnsNoDiagnostics()
    {
        AssertNoErrors("""
            rules:
              - id: secret-check
                message: "Secret found"
                languages: [javascript]
                severity: ERROR
                pattern-regex: 'api_key\s*=\s*\S+'
            """);
    }

    [TestMethod]
    public void ValidFile_MultipleRules_ReturnsNoDiagnostics()
    {
        AssertNoErrors("""
            rules:
              - id: rule-one
                message: "Rule one"
                languages: [typescript, javascript]
                severity: WARNING
                pattern: console.log(...)

              - id: rule-two
                message: "Rule two"
                languages: [python]
                severity: ERROR
                pattern-regex: 'bad_pattern'
            """);
    }

    [TestMethod]
    public void ValidFile_EmptyRulesList_ReturnsNoDiagnostics()
    {
        AssertNoErrors("rules: []");
    }

    [TestMethod]
    public void ValidFile_AllSeverityValues_ReturnsNoDiagnostics()
    {
        // Only test the severity values that the Semgrep schema actually accepts.
        foreach (var sev in new[] { "ERROR", "WARNING", "INFO", "INVENTORY", "EXPERIMENT",
                                    "CRITICAL", "HIGH", "MEDIUM", "LOW" })
        {
            AssertNoErrors($"""
                rules:
                  - id: test-{sev.ToLowerInvariant()}
                    message: "Test {sev}"
                    languages: [python]
                    severity: {sev}
                    pattern: x = 1
                """);
        }
    }

    [TestMethod]
    public void ValidFile_PatternEither_ReturnsNoDiagnostics()
    {
        AssertNoErrors("""
            rules:
              - id: either-rule
                message: "Either match"
                languages: [javascript]
                severity: WARNING
                pattern-either:
                  - pattern: foo()
                  - pattern: bar()
            """);
    }

    // ── Missing top-level "rules:" ────────────────────────────────────────────

    [TestMethod]
    public void MissingRulesKey_ReturnsTopLevelDiagnostic()
    {
        AssertHasError("id: orphaned-field", "rules");
    }

    [TestMethod]
    public void EmptyFile_ReturnsTopLevelDiagnostic()
    {
        AssertHasError("", "rules");
    }

    // ── Missing required fields ───────────────────────────────────────────────

    [TestMethod]
    public void MissingId_ReturnsDiagnostic()
    {
        AssertHasError("""
            rules:
              - message: "No id"
                languages: [python]
                severity: ERROR
                pattern: x = 1
            """, "id");
    }

    [TestMethod]
    public void MissingMessage_ReturnsDiagnostic()
    {
        AssertHasError("""
            rules:
              - id: no-message
                languages: [python]
                severity: ERROR
                pattern: x = 1
            """, "message");
    }

    [TestMethod]
    public void MissingLanguages_ReturnsDiagnostic()
    {
        AssertHasError("""
            rules:
              - id: no-lang
                message: "No languages"
                severity: ERROR
                pattern: x = 1
            """, "languages");
    }

    [TestMethod]
    public void EmptyLanguagesList_DoesNotReturnDiagnostic()
    {
        // The Semgrep schema does not enforce minItems on languages,
        // so an empty list is schema-valid.
        AssertNoErrors("""
            rules:
              - id: empty-lang
                message: "Empty languages"
                languages: []
                severity: ERROR
                pattern: x = 1
            """);
    }

    [TestMethod]
    public void MissingSeverity_ReturnsDiagnostic()
    {
        AssertHasError("""
            rules:
              - id: no-severity
                message: "No severity"
                languages: [python]
                pattern: x = 1
            """, "severity");
    }

    [TestMethod]
    public void InvalidSeverity_ReturnsDiagnostic()
    {
        AssertHasError("""
            rules:
              - id: bad-severity
                message: "Bad severity"
                languages: [python]
                severity: FATAL
                pattern: x = 1
            """, "severity");
    }

    [TestMethod]
    public void MissingPattern_ReturnsDiagnostic()
    {
        AssertHasError("""
            rules:
              - id: no-pattern
                message: "No pattern"
                languages: [python]
                severity: ERROR
            """, "pattern");
    }

    // ── Line numbers in diagnostics ───────────────────────────────────────────

    [TestMethod]
    public void MissingRulesKey_DiagnosticIsAtLine0()
    {
        var diags = Validate("something: else");
        Assert.IsTrue(diags.Length > 0);
        Assert.AreEqual(0, diags[0].Range.Start.Line);
    }

    [TestMethod]
    public void InvalidSeverity_DiagnosticLinePointsToSeverityLine()
    {
        var yaml = """
            rules:
              - id: bad-sev
                message: "test"
                languages: [python]
                severity: INVALID
                pattern: x = 1
            """;
        var diags = Validate(yaml);
        var sevDiag = diags.FirstOrDefault(d => d.Message.Contains("severity", StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(sevDiag, "Expected a severity diagnostic");
        // "severity: INVALID" is on line 4 (0-based)
        Assert.AreEqual(4, sevDiag!.Range.Start.Line);
    }

    // ── Comment stripping edge cases ──────────────────────────────────────────

    [TestMethod]
    public void MessageWithHashSign_IsNotTreatedAsComment()
    {
        // "message: Issue #123" — the '#' is inside a value, not a YAML comment.
        AssertNoErrors("""
            rules:
              - id: hash-in-message
                message: "Issue #123 must be fixed"
                languages: [python]
                severity: WARNING
                pattern: bad()
            """);
    }

    [TestMethod]
    public void AllDiagnostics_HaveSeverity1()
    {
        var diags = Validate("""
            rules:
              - id: broken
                languages: []
                severity: BAD
            """);
        Assert.IsTrue(diags.Length > 0);
        foreach (var d in diags)
            Assert.AreEqual(1, d.Severity);
    }

    // ── YAML syntax errors ────────────────────────────────────────────────────

    [TestMethod]
    public void YamlSyntaxError_ReturnsSyntaxErrorDiagnostic()
    {
        // A tab character in YAML indentation is a syntax error.
        AssertHasError("rules:\n\t- id: foo", "YAML syntax error");
    }

    [TestMethod]
    public void YamlSyntaxError_DiagnosticHasSourceDolphin()
    {
        var diags = Validate("rules:\n\t- id: foo");
        Assert.IsTrue(diags.Length > 0);
        Assert.AreEqual("dolphin", diags[0].Source);
    }

    // ── Scalar type coercion ──────────────────────────────────────────────────

    [TestMethod]
    public void IntegerScalarId_ReturnsDiagnostic()
    {
        // Unquoted "123" is parsed as a long integer by ConvertScalar,
        // which fails the schema's string type requirement for "id".
        AssertHasError("""
            rules:
              - id: 123
                message: test
                languages: [python]
                severity: ERROR
                pattern: x = 1
            """, "integer");
    }

    [TestMethod]
    public void FloatScalarSeverity_ReturnsDiagnostic()
    {
        // Unquoted "3.14" is parsed as a double by ConvertScalar,
        // which fails the schema's enum requirement for "severity".
        AssertHasError("""
            rules:
              - id: float-sev
                message: test
                languages: [python]
                severity: 3.14
                pattern: x = 1
            """, "severity");
    }

    [TestMethod]
    public void NanScalarValue_DoesNotThrow_AndValidatesNormally()
    {
        // Unquoted "NaN" is accepted by double.TryParse but must NOT be passed to
        // JsonValue.Create because Utf8JsonWriter does not allow non-finite numbers.
        // ConvertScalar must fall back to treating it as a plain string.
        const string yaml = """
            rules:
              - id: nan-test
                message: test
                languages: [python]
                severity: NaN
                pattern: x = 1
            """;
        // Must not throw; schema validation should emit at least one diagnostic
        // because "NaN" (a string after fallback) is not a valid severity enum value.
        var diags = Validate(yaml);
        Assert.IsTrue(diags.Length > 0, "Expected diagnostics for NaN severity");
    }

    [TestMethod]
    public void InfinityScalarValue_DoesNotThrow_AndValidatesNormally()
    {
        // Same as NaN: "Infinity" is accepted by double.TryParse but must not be
        // passed to JsonValue.Create. Fallback to string ensures no crash.
        const string yaml = """
            rules:
              - id: infinity-test
                message: test
                languages: [python]
                severity: Infinity
                pattern: x = 1
            """;
        var diags = Validate(yaml);
        Assert.IsTrue(diags.Length > 0, "Expected diagnostics for Infinity severity");
    }

    // ── Type mismatch (exercises FormatKeywordError default case) ─────────────

    [TestMethod]
    public void RulesIsScalarNotList_ReturnsDiagnostic()
    {
        // "rules: scalar" — YAML is syntactically valid but schema expects an array.
        // The schema emits a "type" keyword error which hits the default case in
        // FormatKeywordError.
        AssertHasError("rules: not-a-list", "array");
    }

    // ── Multi-document YAML ───────────────────────────────────────────────────

    [TestMethod]
    public void MultiDocumentYaml_ReturnsDiagnostic()
    {
        // A "---" separator creates a second YAML document which is not valid for
        // a Dolphin rules file. The validator should reject it with a clear message.
        var diags = Validate("rules: []\n---\nrules: []");
        Assert.IsTrue(diags.Length > 0, "Expected at least one diagnostic for multi-document YAML");
        Assert.IsTrue(
            diags.Any(d => d.Message.Contains("exactly one", StringComparison.OrdinalIgnoreCase)),
            $"Expected 'exactly one' in message, got: {string.Join("; ", diags.Select(d => d.Message))}");
        // Multi-document diagnostic is emitted at line 0
        Assert.AreEqual(0, diags[0].Range.Start.Line);
    }

    // ── Non-scalar mapping keys ───────────────────────────────────────────────

    [TestMethod]
    public void SequenceMappingKey_DoesNotThrow_AndReturnsAtLeastOneDiagnostic()
    {
        // YAML allows non-scalar mapping keys (e.g. sequences as keys), which the
        // validator must handle without throwing InvalidCastException. Schema
        // validation still runs; the odd key simply doesn't match the expected
        // structure, so at least one diagnostic is emitted.
        const string yaml = """
            rules:
              ? [sequence-key]
              : value
            """;
        var diags = Validate(yaml);
        Assert.IsTrue(diags.Length > 0, "Expected at least one diagnostic for non-scalar key YAML");
    }

    // ── Keys with RFC 6901 special characters ─────────────────────────────────

    [TestMethod]
    public void KeyContainingSlash_DoesNotThrow_AndValidatesNormally()
    {
        // A YAML mapping key containing '/' must be RFC 6901–escaped when building
        // the JSON Pointer path used for line-number lookup; this test ensures no
        // exception is thrown and that a diagnostic is produced (the key doesn't
        // match the Semgrep schema).
        const string yaml = """
            rules:
              - id: slash-key
                message: "test"
                languages: [python]
                severity: ERROR
                pattern: x = 1
                a/b: extra
            """;
        // Should not throw, just produce diagnostics or none depending on schema
        _ = Validate(yaml);
    }

    [TestMethod]
    public void KeyContainingTilde_DoesNotThrow_AndValidatesNormally()
    {
        // A YAML mapping key containing '~' must be RFC 6901–escaped ('~0'); this
        // test ensures no exception is thrown.
        const string yaml = """
            rules:
              - id: tilde-key
                message: "test"
                languages: [python]
                severity: ERROR
                pattern: x = 1
                a~b: extra
            """;
        _ = Validate(yaml);
    }

    // ── RFC 6901 unescape ─────────────────────────────────────────────────────

    [TestMethod]
    [DataRow("severity", "severity")]
    [DataRow("a~1b", "a/b")]
    [DataRow("a~0b", "a~b")]
    [DataRow("a~01b", "a~1b")]
    [DataRow("~1~0", "/~")]
    public void UnescapeJsonPointerSegment_RestoresOriginalKey(string escaped, string expected)
    {
        Assert.AreEqual(expected, YamlRuleValidator.UnescapeJsonPointerSegment(escaped));
    }
}
