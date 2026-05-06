using Dolphin.Output;
using Dolphin.Scanner;

namespace Dolphin.Tests;

[TestClass]
public class FormatterTests
{
    private static string CaptureGitHub(List<Finding> findings)
    {
        var sw = new StringWriter();
        var original = Console.Out;
        Console.SetOut(sw);
        try { Formatter.Print(findings, "github"); }
        finally { Console.SetOut(original); }
        return sw.ToString();
    }

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
}
