using Dolphin.Lsp;

namespace Dolphin.Tests;

[TestClass]
public class LspDiagnosticsParserTests
{
    // ── Happy path ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void Parse_ErrorWithLocation_ReturnsCorrectDiagnostic()
    {
        var output = """
            Invalid rule 'no-console-log': missing required field 'message'
              --> /tmp/rules.yaml:8:5
            """;

        var diags = LspDiagnosticsParser.Parse(output);

        Assert.AreEqual(1, diags.Length);
        Assert.AreEqual("opengrep", diags[0].Source);
        Assert.AreEqual(1, diags[0].Severity);
        Assert.AreEqual(7, diags[0].Range.Start.Line);      // 1-indexed 8 → 0-indexed 7
        Assert.AreEqual(4, diags[0].Range.Start.Character); // 1-indexed 5 → 0-indexed 4
        Assert.IsTrue(diags[0].Message.Contains("missing required field"));
    }

    [TestMethod]
    public void Parse_ErrorWithLocationButNoColumn_DefaultsColumnToZero()
    {
        var output = """
            Invalid rule 'foo': error
              --> /tmp/rules.yaml:3
            """;

        var diags = LspDiagnosticsParser.Parse(output);

        Assert.AreEqual(1, diags.Length);
        Assert.AreEqual(2, diags[0].Range.Start.Line);
        Assert.AreEqual(0, diags[0].Range.Start.Character);
    }

    // ── Path edge cases ────────────────────────────────────────────────────────

    [TestMethod]
    public void Parse_PathWithSpaces_ParsesLocationCorrectly()
    {
        var output = """
            Invalid rule 'x': missing required field 'message'
              --> /home/my user/my rules.yaml:12:3
            """;

        var diags = LspDiagnosticsParser.Parse(output);

        Assert.AreEqual(1, diags.Length);
        Assert.AreEqual(11, diags[0].Range.Start.Line);
        Assert.AreEqual(2, diags[0].Range.Start.Character);
    }

    [TestMethod]
    public void Parse_WindowsPath_ParsesLocationCorrectly()
    {
        var output = """
            Invalid rule 'x': error
              --> C:\Users\My Name\rules.yaml:5:1
            """;

        var diags = LspDiagnosticsParser.Parse(output);

        Assert.AreEqual(1, diags.Length);
        Assert.AreEqual(4, diags[0].Range.Start.Line);
        Assert.AreEqual(0, diags[0].Range.Start.Character);
    }

    // ── No location ────────────────────────────────────────────────────────────

    [TestMethod]
    public void Parse_ErrorWithoutLocation_DefaultsToLineZero()
    {
        const string output = "Error: unexpected field 'xyz'";

        var diags = LspDiagnosticsParser.Parse(output);

        Assert.AreEqual(1, diags.Length);
        Assert.AreEqual(0, diags[0].Range.Start.Line);
        Assert.AreEqual(0, diags[0].Range.Start.Character);
        Assert.IsTrue(diags[0].Message.Contains("unexpected field"));
    }

    // ── Multiple errors ────────────────────────────────────────────────────────

    [TestMethod]
    public void Parse_MultipleErrors_EachGetsItsLocation()
    {
        var output = """
            Invalid rule 'rule-a': missing required field 'id'
              --> /tmp/rules.yaml:2:1
            Invalid rule 'rule-b': missing required field 'message'
              --> /tmp/rules.yaml:10:1
            """;

        var diags = LspDiagnosticsParser.Parse(output);

        Assert.AreEqual(2, diags.Length);
        Assert.AreEqual(1, diags[0].Range.Start.Line);
        Assert.AreEqual(9, diags[1].Range.Start.Line);
    }

    // ── Clean / empty output ───────────────────────────────────────────────────

    [TestMethod]
    public void Parse_EmptyOutput_ReturnsEmpty()
    {
        var diags = LspDiagnosticsParser.Parse("");
        Assert.AreEqual(0, diags.Length);
    }

    [TestMethod]
    public void Parse_WhitespaceOnlyOutput_ReturnsEmpty()
    {
        var diags = LspDiagnosticsParser.Parse("   \n\t\n  ");
        Assert.AreEqual(0, diags.Length);
    }

    // ── Fallback ───────────────────────────────────────────────────────────────

    [TestMethod]
    public void Parse_UnrecognisedNonEmptyOutput_ReturnsFallbackDiagnostic()
    {
        // Output that doesn't match any keyword — treated as opaque error.
        const string output = "Something went completely sideways";

        var diags = LspDiagnosticsParser.Parse(output);

        Assert.AreEqual(1, diags.Length);
        Assert.AreEqual("opengrep", diags[0].Source);
        Assert.AreEqual(0, diags[0].Range.Start.Line);
        StringAssert.Contains(diags[0].Message, "Something went completely sideways");
    }

    // ── Opengrep validate inline-location format ───────────────────────────────

    [TestMethod]
    public void Parse_OpengrepValidateInlineLine_ExtractsLocationFromSameLine()
    {
        // opengrep validate embeds the file:line:col in the same line as the message
        // (no separate --> line), e.g. after tmp-path replacement by the server:
        const string output =
            "[00.20][WARNING]: invalid rule bad-rule, rules.yaml:2:4: Missing required field message";

        var diags = LspDiagnosticsParser.Parse(output);

        Assert.AreEqual(1, diags.Length);
        Assert.AreEqual(1, diags[0].Range.Start.Line);      // 2 → 0-indexed 1
        Assert.AreEqual(3, diags[0].Range.Start.Character); // 4 → 0-indexed 3
        StringAssert.Contains(diags[0].Message, "invalid");
    }

    [TestMethod]
    public void Parse_OpengrepValidateInlineLine_LineOnlyNoColumn()
    {
        const string output =
            "[00.70][ERROR]: Opengrep match found at line rules.yaml:5";

        var diags = LspDiagnosticsParser.Parse(output);

        Assert.AreEqual(1, diags.Length);
        Assert.AreEqual(4, diags[0].Range.Start.Line);      // 5 → 0-indexed 4
        Assert.AreEqual(0, diags[0].Range.Start.Character); // no column → 0
    }
}
