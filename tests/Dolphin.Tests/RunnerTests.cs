using Dolphin.Scanner;

namespace Dolphin.Tests;

[TestClass]
public class RunnerTests
{
    private static readonly string FixturesDir = Path.Combine(
        AppContext.BaseDirectory, "fixtures"
    );

    [TestMethod]
    public async Task RunAsync_ThrowsWhenRulesFileMissing()
    {
        string scanner;
        try { scanner = await Installer.EnsureInstalledAsync(); }
        catch { Assert.Inconclusive("No scanner found in this environment"); return; }

        var emptyDir = Path.Combine(Path.GetTempPath(), $"dolphin-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(emptyDir);

        try
        {
            await Assert.ThrowsExceptionAsync<FileNotFoundException>(
                () => Runner.RunAsync(scanner, emptyDir)
            );
        }
        finally
        {
            Directory.Delete(emptyDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task RunAsync_DetectsViolationsInSampleFile()
    {
        string scanner;
        try { scanner = await Installer.EnsureInstalledAsync(); }
        catch { Assert.Inconclusive("No scanner found in this environment"); return; }

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
            var result = await Runner.RunAsync(scanner, tmpDir);

            Assert.IsTrue(result.Findings.Count > 0, "Expected at least one finding in bad-file.ts");
            Assert.IsTrue(result.Findings.Any(f => f.RuleId == "no-console-log"), "Expected finding with rule 'no-console-log'");
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task RunAsync_WithRuleIdFilter_ReturnsOnlyMatchingFindings()
    {
        string scanner;
        try { scanner = await Installer.EnsureInstalledAsync(); }
        catch { Assert.Inconclusive("No scanner found in this environment"); return; }

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
            var result = await Runner.RunAsync(scanner, tmpDir, ruleId: "no-console-log");

            Assert.IsTrue(result.Findings.Count > 0, "Expected at least one finding when filtering by no-console-log");
            foreach (var f in result.Findings)
                Assert.AreEqual("no-console-log", f.RuleId);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task RunAsync_ReturnsNoFindings_ForCleanDirectory()
    {
        string scanner;
        try { scanner = await Installer.EnsureInstalledAsync(); }
        catch { Assert.Inconclusive("No scanner found in this environment"); return; }

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
            var result = await Runner.RunAsync(scanner, tmpDir);

            Assert.AreEqual(0, result.Findings.Count);
            Assert.IsFalse(result.HasFindings);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task RunAsync_CorrectlyCategorizesSeverity()
    {
        string scanner;
        try { scanner = await Installer.EnsureInstalledAsync(); }
        catch { Assert.Inconclusive("No scanner found in this environment"); return; }

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
            var result = await Runner.RunAsync(scanner, tmpDir);

            var secretFinding = result.Findings.FirstOrDefault(f => f.RuleId == "no-hardcoded-secret");
            Assert.IsNotNull(secretFinding);
            Assert.AreEqual(Severity.Error, secretFinding.Severity);

            var consoleFinding = result.Findings.FirstOrDefault(f => f.RuleId == "no-console-log");
            Assert.IsNotNull(consoleFinding);
            Assert.AreEqual(Severity.Warning, consoleFinding.Severity);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task RunAsync_FindingsAreSortedByFilePathThenLine()
    {
        string scanner;
        try { scanner = await Installer.EnsureInstalledAsync(); }
        catch { Assert.Inconclusive("No scanner found in this environment"); return; }

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
            var result = await Runner.RunAsync(scanner, tmpDir);

            for (int i = 1; i < result.Findings.Count; i++)
            {
                var prev = result.Findings[i - 1];
                var curr = result.Findings[i];
                var cmp = string.Compare(prev.FilePath, curr.FilePath, StringComparison.OrdinalIgnoreCase);
                if (cmp == 0)
                    Assert.IsTrue(prev.Line <= curr.Line, "Findings not sorted by line number");
                else
                    Assert.IsTrue(cmp <= 0, "Findings not sorted by file path");
            }
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }
}
