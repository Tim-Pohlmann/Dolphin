using Dolphin.Output;
using Dolphin.Scanner;

namespace Dolphin.Tests;

[TestClass]
public class FormatterTests
{
    private static string Capture(List<Finding> findings, string format)
    {
        var sw = new StringWriter();
        var original = Console.Out;
        Console.SetOut(sw);
        try { Formatter.Print(findings, format); }
        finally { Console.SetOut(original); }
        return sw.ToString();
    }

    private static string CaptureGitHub(List<Finding> findings) => Capture(findings, "github");
    private static string CaptureText(List<Finding> findings)   => Capture(findings, "text");
    private static string CaptureJson(List<Finding> findings)   => Capture(findings, "json");

    // ── Severity mapping ───────────────────────────────────────────────────────

    [TestMethod]
    public void Print_Github_ErrorSeverity_EmitsErrorLevel()
    {
        var findings = new List<Finding> { new("rule-id", Severity.Error, "src/foo.cs", 10, 3, "msg", "") };
        var output = CaptureGitHub(findings);
        StringAssert.StartsWith(output, "::error ");
    }

    [TestMethod]
    public void Print_Github_WarningSeverity_EmitsWarningLevel()
    {
        var findings = new List<Finding> { new("rule-id", Severity.Warning, "src/foo.cs", 1, 1, "msg", "") };
        var output = CaptureGitHub(findings);
        StringAssert.StartsWith(output, "::warning ");
    }

    [TestMethod]
    public void Print_Github_InfoSeverity_EmitsNoticeLevel()
    {
        var findings = new List<Finding> { new("rule-id", Severity.Info, "src/foo.cs", 1, 1, "msg", "") };
        var output = CaptureGitHub(findings);
        StringAssert.StartsWith(output, "::notice ");
    }

    // ── Annotation fields ──────────────────────────────────────────────────────

    [TestMethod]
    public void Print_Github_EmitsAllFields()
    {
        var findings = new List<Finding> { new("my-rule", Severity.Error, "src/bar.cs", 42, 7, "something bad", "") };
        var output = CaptureGitHub(findings);
        StringAssert.Contains(output, "file=src/bar.cs");
        StringAssert.Contains(output, "line=42");
        StringAssert.Contains(output, "col=7");
        StringAssert.Contains(output, "title=my-rule");
        StringAssert.Contains(output, "::something bad");
    }

    // ── Message escaping ───────────────────────────────────────────────────────

    [TestMethod]
    public void Print_Github_PercentInMessage_IsEscaped()
    {
        var findings = new List<Finding> { new("r", Severity.Info, "f.cs", 1, 1, "100% done", "") };
        var output = CaptureGitHub(findings);
        StringAssert.Contains(output, "100%25 done");
    }

    [TestMethod]
    public void Print_Github_CarriageReturnInMessage_IsEscaped()
    {
        var findings = new List<Finding> { new("r", Severity.Info, "f.cs", 1, 1, "line1\rline2", "") };
        var output = CaptureGitHub(findings);
        StringAssert.Contains(output, "line1%0Dline2");
    }

    [TestMethod]
    public void Print_Github_NewlineInMessage_IsEscaped()
    {
        var findings = new List<Finding> { new("r", Severity.Info, "f.cs", 1, 1, "line1\nline2", "") };
        var output = CaptureGitHub(findings);
        StringAssert.Contains(output, "line1%0Aline2");
    }

    // ── Property value escaping ────────────────────────────────────────────────

    [TestMethod]
    public void Print_Github_CommaInFilePath_IsEscaped()
    {
        var findings = new List<Finding> { new("rule", Severity.Error, "src/a,b.cs", 1, 1, "msg", "") };
        var output = CaptureGitHub(findings);
        StringAssert.Contains(output, "file=src/a%2Cb.cs");
    }

    [TestMethod]
    public void Print_Github_ColonInFilePath_IsEscaped()
    {
        var findings = new List<Finding> { new("rule", Severity.Error, "C:/src/foo.cs", 1, 1, "msg", "") };
        var output = CaptureGitHub(findings);
        StringAssert.Contains(output, "file=C%3A/src/foo.cs");
    }

    [TestMethod]
    public void Print_Github_CommaInRuleId_IsEscaped()
    {
        var findings = new List<Finding> { new("rule,id", Severity.Warning, "f.cs", 1, 1, "msg", "") };
        var output = CaptureGitHub(findings);
        StringAssert.Contains(output, "title=rule%2Cid");
    }

    // ── Empty / multiple ───────────────────────────────────────────────────────

    [TestMethod]
    public void Print_Github_NoFindings_ProducesNoOutput()
    {
        var output = CaptureGitHub([]);
        Assert.AreEqual("", output);
    }

    [TestMethod]
    public void Print_Github_MultipleFindings_EmitsOneLineEach()
    {
        var findings = new List<Finding>
        {
            new("rule-a", Severity.Error,   "a.cs", 1, 1, "err",  ""),
            new("rule-b", Severity.Warning, "b.cs", 2, 2, "warn", ""),
        };
        var output = CaptureGitHub(findings);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual(2, lines.Length);
        StringAssert.StartsWith(lines[0], "::error ");
        StringAssert.StartsWith(lines[1], "::warning ");
    }

    // ── Text format ────────────────────────────────────────────────────────────

    [TestMethod]
    public void Print_Text_WithErrorFinding_ContainsFilenameAndRule()
    {
        var findings = new List<Finding> { new("my-rule", Severity.Error, "src/foo.cs", 5, 1, "bad thing", "") };
        var output = CaptureText(findings);
        StringAssert.Contains(output, "src/foo.cs:5");
        StringAssert.Contains(output, "[my-rule]");
    }

    [TestMethod]
    public void Print_Text_WithMatchedText_PrintsMatchedText()
    {
        var findings = new List<Finding> { new("r", Severity.Warning, "f.cs", 1, 1, "msg", "matched snippet") };
        var output = CaptureText(findings);
        StringAssert.Contains(output, "matched snippet");
    }

    [TestMethod]
    public void Print_Text_SummaryCountsAllSeverities()
    {
        var findings = new List<Finding>
        {
            new("r", Severity.Error,   "a.cs", 1, 1, "e", ""),
            new("r", Severity.Warning, "b.cs", 2, 1, "w", ""),
            new("r", Severity.Info,    "c.cs", 3, 1, "i", ""),
        };
        var output = CaptureText(findings);
        StringAssert.Contains(output, "Found 3 violation(s)");
        StringAssert.Contains(output, "1 errors");
        StringAssert.Contains(output, "1 warnings");
        StringAssert.Contains(output, "1 info");
    }

    // ── JSON format ────────────────────────────────────────────────────────────

    [TestMethod]
    public void Print_Json_NoFindings_PrintsEmptyArray()
    {
        var output = CaptureJson([]);
        Assert.AreEqual("[]", output.Trim());
    }

    [TestMethod]
    public void Print_Json_WithFinding_SerializesAllFields()
    {
        var findings = new List<Finding> { new("rule-x", Severity.Error, "foo.cs", 5, 3, "something", "snippet") };
        var output = CaptureJson(findings);
        StringAssert.Contains(output, "\"RuleId\":\"rule-x\"");
        StringAssert.Contains(output, "\"Severity\":\"error\"");
        StringAssert.Contains(output, "\"FilePath\":\"foo.cs\"");
        StringAssert.Contains(output, "\"Line\":5");
        StringAssert.Contains(output, "\"Column\":3");
        StringAssert.Contains(output, "\"Message\":\"something\"");
        StringAssert.Contains(output, "\"MatchedText\":\"snippet\"");
    }
}
