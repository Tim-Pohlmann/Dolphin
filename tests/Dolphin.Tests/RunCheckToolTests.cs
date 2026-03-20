using System.Text.RegularExpressions;
using Dolphin.Mcp.Tools;
using Dolphin.Scanner;

namespace Dolphin.Tests;

[TestClass]
public class RunCheckToolBuildOutputTests
{
    [TestMethod]
    public void BuildOutput_NoFindings_NoWarning_ReturnsCleanMessage()
    {
        var result = new RunResult([], HasFindings: false);
        Assert.AreEqual("✓ No violations found.", RunCheckTool.BuildOutput(result));
    }

    [TestMethod]
    public void BuildOutput_NoFindings_WithWarning_IncludesWarningPrefix()
    {
        var result = new RunResult([], HasFindings: false, ScannerWarning: "partial scan");
        var output = RunCheckTool.BuildOutput(result);
        StringAssert.Contains(output, "⚠ Scanner warning: partial scan");
        StringAssert.Contains(output, "✓ No violations found.");
    }

    [TestMethod]
    public void BuildOutput_WithFindings_WithWarning_IncludesWarningAndViolations()
    {
        var finding = new Finding("my-rule", Severity.Error, "src/foo.ts", 1, 1, "bad code", "foo()");
        var result = new RunResult([finding], HasFindings: true, ScannerWarning: "scan incomplete");
        var output = RunCheckTool.BuildOutput(result);
        StringAssert.Contains(output, "⚠ Scanner warning: scan incomplete");
        StringAssert.Contains(output, "violation(s)");
        StringAssert.Contains(output, "my-rule");
    }

    [TestMethod]
    public void BuildOutput_WithFindings_NoWarning_DoesNotContainWarningPrefix()
    {
        var finding = new Finding("my-rule", Severity.Warning, "src/foo.ts", 1, 1, "msg", "");
        var result = new RunResult([finding], HasFindings: true);
        var output = RunCheckTool.BuildOutput(result);
        Assert.IsFalse(output.Contains("⚠"), "Should not contain warning symbol when ScannerWarning is null");
    }

    [TestMethod]
    public void BuildOutput_FormatsFindings_AsGnuDiagnosticStyle()
    {
        var finding = new Finding("my-rule", Severity.Error, "src/foo.ts", 5, 12, "bad code", "");
        var result = new RunResult([finding], HasFindings: true);
        var output = RunCheckTool.BuildOutput(result);
        StringAssert.Contains(output, "src/foo.ts:5:12: error: bad code [my-rule]");
    }

    [TestMethod]
    public void BuildOutput_SummaryLine_UsesNotesForInfoSeverity()
    {
        var finding = new Finding("my-rule", Severity.Info, "src/foo.ts", 1, 1, "msg", "");
        var result = new RunResult([finding], HasFindings: true);
        var output = RunCheckTool.BuildOutput(result);
        StringAssert.Contains(output, "src/foo.ts:1:1: note: msg [my-rule]");
        StringAssert.Contains(output, "1 notes");
    }
}

/// <summary>
/// Tests for the RunCheckTool MCP tool method — the string it returns is
/// exactly what Claude receives when it calls the run_check tool.
/// </summary>
[TestClass]
public partial class RunCheckToolTests
{
    private static readonly string FixturesDir = Path.Combine(
        AppContext.BaseDirectory, "fixtures"
    );

    [TestMethod]
    public async Task RunCheck_ReturnsError_WhenDirectoryDoesNotExist()
    {
        var tool = new RunCheckTool();

        var result = await tool.RunCheck("/nonexistent/dolphin-test-path-that-does-not-exist");

        StringAssert.StartsWith(result, "Error:");
        StringAssert.Contains(result, "directory not found");
    }

    [TestMethod]
    public async Task RunCheck_ReturnsError_WhenRulesFileMissing()
    {
        try { await Installer.EnsureInstalledAsync(); }
        catch { Assert.Inconclusive("No scanner available in this environment"); return; }

        var tmpDir = Path.Combine(Path.GetTempPath(), $"dolphin-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            var tool = new RunCheckTool();
            var result = await tool.RunCheck(tmpDir);

            StringAssert.StartsWith(result, "Error:");
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task RunCheck_ReturnsNoViolationsMessage_ForCleanDirectory()
    {
        try { await Installer.EnsureInstalledAsync(); }
        catch { Assert.Inconclusive("No scanner available in this environment"); return; }

        var tmpDir = Path.Combine(Path.GetTempPath(), $"dolphin-test-{Guid.NewGuid()}");
        var tmpDolphinDir = Path.Combine(tmpDir, ".dolphin");
        var tmpSrcDir = Path.Combine(tmpDir, "src");
        Directory.CreateDirectory(tmpDolphinDir);
        Directory.CreateDirectory(tmpSrcDir);

        File.Copy(
            Path.Combine(FixturesDir, "rules.yaml"),
            Path.Combine(tmpDolphinDir, "rules.yaml")
        );
        File.Copy(
            Path.Combine(FixturesDir, "sample-src", "clean-file.ts"),
            Path.Combine(tmpSrcDir, "clean-file.ts")
        );

        try
        {
            var tool = new RunCheckTool();
            var result = await tool.RunCheck(tmpDir);

            Assert.AreEqual("✓ No violations found.", result);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task RunCheck_ReturnsFormattedViolations_WhenFindingsExist()
    {
        try { await Installer.EnsureInstalledAsync(); }
        catch { Assert.Inconclusive("No scanner available in this environment"); return; }

        var tmpDir = Path.Combine(Path.GetTempPath(), $"dolphin-test-{Guid.NewGuid()}");
        var tmpDolphinDir = Path.Combine(tmpDir, ".dolphin");
        var tmpSrcDir = Path.Combine(tmpDir, "src");
        Directory.CreateDirectory(tmpDolphinDir);
        Directory.CreateDirectory(tmpSrcDir);

        File.Copy(
            Path.Combine(FixturesDir, "rules.yaml"),
            Path.Combine(tmpDolphinDir, "rules.yaml")
        );
        File.Copy(
            Path.Combine(FixturesDir, "sample-src", "bad-file.ts"),
            Path.Combine(tmpSrcDir, "bad-file.ts")
        );

        try
        {
            var tool = new RunCheckTool();
            var result = await tool.RunCheck(tmpDir);

            StringAssert.Contains(result, "violation(s)");
            StringAssert.Contains(result, "no-hardcoded-secret");
            StringAssert.Contains(result, "no-console-log");
            StringAssert.Contains(result, ": error:");
            StringAssert.Contains(result, ": warning:");
            StringAssert.Matches(result, GnuDiagnosticPrefix());
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task RunCheck_SummaryLine_ReflectsCorrectCounts()
    {
        try { await Installer.EnsureInstalledAsync(); }
        catch { Assert.Inconclusive("No scanner available in this environment"); return; }

        var tmpDir = Path.Combine(Path.GetTempPath(), $"dolphin-test-{Guid.NewGuid()}");
        var tmpDolphinDir = Path.Combine(tmpDir, ".dolphin");
        var tmpSrcDir = Path.Combine(tmpDir, "src");
        Directory.CreateDirectory(tmpDolphinDir);
        Directory.CreateDirectory(tmpSrcDir);

        File.Copy(
            Path.Combine(FixturesDir, "rules.yaml"),
            Path.Combine(tmpDolphinDir, "rules.yaml")
        );
        File.Copy(
            Path.Combine(FixturesDir, "sample-src", "bad-file.ts"),
            Path.Combine(tmpSrcDir, "bad-file.ts")
        );

        try
        {
            var tool = new RunCheckTool();
            var result = await tool.RunCheck(tmpDir);

            // bad-file.ts has 1 ERROR (no-hardcoded-secret) and 1 WARNING (no-console-log)
            StringAssert.Contains(result, "1 errors");
            StringAssert.Contains(result, "1 warnings");
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }
}

public partial class RunCheckToolTests
{
    [GeneratedRegex(@"bad-file\.ts:\d+:\d+:")]
    private static partial Regex GnuDiagnosticPrefix();
}
