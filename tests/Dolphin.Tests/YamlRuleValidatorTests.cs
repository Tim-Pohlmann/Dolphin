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
        foreach (var sev in new[] { "ERROR", "WARNING", "INFO", "ERROR_TODO", "INFO_TODO" })
        {
            AssertNoErrors($"""
                rules:
                  - id: test-{sev.ToLower()}
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
    public void EmptyLanguagesList_ReturnsDiagnostic()
    {
        AssertHasError("""
            rules:
              - id: empty-lang
                message: "Empty languages"
                languages: []
                severity: ERROR
                pattern: x = 1
            """, "languages");
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
                severity: CRITICAL
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
}
