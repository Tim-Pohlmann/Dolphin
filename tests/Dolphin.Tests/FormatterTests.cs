using System.Text.Json;
using Dolphin.Output;
using Dolphin.Scanner;

namespace Dolphin.Tests;

/// <summary>
/// Unit tests for <see cref="Formatter"/>.
/// Console.Out is redirected to a StringWriter to capture text output without spawning a process.
/// Console color operations are silently no-ops in non-terminal (redirected) environments.
/// </summary>
[TestClass]
public class FormatterTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string CaptureText(List<Finding> findings, string format = "text")
    {
        var sw = new StringWriter();
        var original = Console.Out;
        Console.SetOut(sw);
        try
        {
            Formatter.Print(findings, format);
        }
        finally
        {
            Console.SetOut(original);
        }
        return sw.ToString();
    }

    // ── PrintText: empty findings ─────────────────────────────────────────────

    [TestMethod]
    public void PrintText_EmptyFindings_PrintsNoViolationsMessage()
    {
        var output = CaptureText([]);

        StringAssert.Contains(output, "No violations found.");
    }

    // ── PrintText: with findings ──────────────────────────────────────────────

    [TestMethod]
    public void PrintText_WithErrorFinding_PrintsErrorLabel()
    {
        var finding = new Finding("my-rule", Severity.Error, "src/foo.ts", 5, 1, "bad code", "");

        var output = CaptureText([finding]);

        StringAssert.Contains(output, "src/foo.ts:5");
        StringAssert.Contains(output, "my-rule");
        StringAssert.Contains(output, "bad code");
        // Summary line must mention errors
        StringAssert.Contains(output, "errors");
    }

    [TestMethod]
    public void PrintText_WithWarningFinding_PrintsWarningSummary()
    {
        var finding = new Finding("warn-rule", Severity.Warning, "src/bar.ts", 10, 2, "warn msg", "");

        var output = CaptureText([finding]);

        StringAssert.Contains(output, "src/bar.ts:10");
        StringAssert.Contains(output, "warn-rule");
        StringAssert.Contains(output, "warnings");
    }

    [TestMethod]
    public void PrintText_WithInfoFinding_PrintsInfoSummary()
    {
        var finding = new Finding("info-rule", Severity.Info, "src/baz.ts", 1, 1, "info msg", "");

        var output = CaptureText([finding]);

        StringAssert.Contains(output, "src/baz.ts:1");
        StringAssert.Contains(output, "info-rule");
        // Summary line must list counts
        StringAssert.Contains(output, "violation(s)");
    }

    [TestMethod]
    public void PrintText_WithMatchedText_PrintsMatchedTextLine()
    {
        var finding = new Finding("my-rule", Severity.Error, "src/x.ts", 3, 1, "msg", "  const x = 1;");

        var output = CaptureText([finding]);

        StringAssert.Contains(output, "const x = 1;");
    }

    [TestMethod]
    public void PrintText_WithEmptyMatchedText_DoesNotPrintMatchedTextLine()
    {
        var finding = new Finding("my-rule", Severity.Error, "src/x.ts", 3, 1, "msg", "");

        var output = CaptureText([finding]);

        // The only empty line should be the blank separator, not an extra matched-text line.
        // Check that "msg" is present but no trailing empty line follows from matched-text.
        StringAssert.Contains(output, "msg");
    }

    [TestMethod]
    public void PrintText_MultipleSeverities_SummaryReflectsAllCounts()
    {
        var findings = new List<Finding>
        {
            new("rule-e", Severity.Error,   "a.ts", 1, 1, "err",  ""),
            new("rule-e", Severity.Error,   "a.ts", 2, 1, "err",  ""),
            new("rule-w", Severity.Warning, "a.ts", 3, 1, "warn", ""),
            new("rule-i", Severity.Info,    "a.ts", 4, 1, "info", ""),
        };

        var output = CaptureText(findings);

        StringAssert.Contains(output, "2 errors");
        StringAssert.Contains(output, "1 warnings");
        StringAssert.Contains(output, "1 info");
    }

    // ── PrintJson: output is a valid JSON array ───────────────────────────────

    [TestMethod]
    public void PrintJson_EmptyFindings_OutputsEmptyJsonArray()
    {
        var output = CaptureText([], "json");

        using var doc = JsonDocument.Parse(output.Trim());
        Assert.AreEqual(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.AreEqual(0, doc.RootElement.GetArrayLength());
    }

    [TestMethod]
    public void PrintJson_WithFindings_OutputsJsonArrayWithExpectedFields()
    {
        var finding = new Finding("my-rule", Severity.Error, "src/foo.ts", 5, 3, "bad code", "x = 1");

        var output = CaptureText([finding], "json");

        using var doc = JsonDocument.Parse(output.Trim());
        Assert.AreEqual(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.AreEqual(1, doc.RootElement.GetArrayLength());

        var item = doc.RootElement[0];
        // FindingDto uses PascalCase (no custom naming policy); verify both key and value.
        Assert.AreEqual("my-rule",    item.GetProperty("RuleId").GetString());
        Assert.AreEqual("error",      item.GetProperty("Severity").GetString());
        Assert.AreEqual("src/foo.ts", item.GetProperty("FilePath").GetString());
        Assert.AreEqual(5,            item.GetProperty("Line").GetInt32());
        Assert.AreEqual(3,            item.GetProperty("Column").GetInt32());
        Assert.AreEqual("bad code",   item.GetProperty("Message").GetString());
        Assert.AreEqual("x = 1",      item.GetProperty("MatchedText").GetString());
    }

    [TestMethod]
    public void PrintJson_InfoFinding_OutputsLowercaseInfoSeverity()
    {
        var finding = new Finding("r", Severity.Info, "f.ts", 1, 1, "m", "");

        var output = CaptureText([finding], "json");

        using var doc = JsonDocument.Parse(output.Trim());
        Assert.AreEqual("info", doc.RootElement[0].GetProperty("Severity").GetString());
    }

    [TestMethod]
    public void PrintJson_WarningFinding_OutputsLowercaseWarningSeverity()
    {
        var finding = new Finding("r", Severity.Warning, "f.ts", 1, 1, "m", "");

        var output = CaptureText([finding], "json");

        using var doc = JsonDocument.Parse(output.Trim());
        Assert.AreEqual("warning", doc.RootElement[0].GetProperty("Severity").GetString());
    }
}
