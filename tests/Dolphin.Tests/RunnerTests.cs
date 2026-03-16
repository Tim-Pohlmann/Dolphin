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

    [TestMethod]
    public async Task RunAsync_ExitCode2_ReturnsScannerWarning()
    {
        var (tmpDir, fakeBinary) = CreateFakeScannerEnv(exitCode: 2, stderr: "some warning");
        try
        {
            var result = await Runner.RunAsync(fakeBinary, tmpDir);

            Assert.IsNotNull(result.ScannerWarning, "Expected ScannerWarning to be set for exit code 2");
            StringAssert.Contains(result.ScannerWarning, "some warning");
            Assert.AreEqual(0, result.Findings.Count);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task RunAsync_ExitCodeAbove2_Throws()
    {
        var (tmpDir, fakeBinary) = CreateFakeScannerEnv(exitCode: 3, stderr: "fatal error");
        try
        {
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => Runner.RunAsync(fakeBinary, tmpDir)
            );
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    /// <summary>
    /// Creates a temp directory with a minimal .dolphin/rules.yaml and a fake scanner script
    /// that writes empty JSON results to stdout and exits with the given code.
    /// </summary>
    private static (string tmpDir, string fakeBinary) CreateFakeScannerEnv(int exitCode, string stderr = "")
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"dolphin-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(Path.Combine(tmpDir, ".dolphin"));
        File.WriteAllText(Path.Combine(tmpDir, ".dolphin", "rules.yaml"), "rules: []");

        // Write a shell script that prints empty JSON results and exits with the given code
        var script = Path.Combine(tmpDir, "fake-scanner.sh");
        File.WriteAllText(script,
            $"#!/bin/sh\necho '{{\"results\":[]}}'\necho '{stderr}' >&2\nexit {exitCode}\n");
#pragma warning disable CA1416 // SetUnixFileMode is not supported on Windows; these tests are Unix-only
        File.SetUnixFileMode(script,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
#pragma warning restore CA1416

        return (tmpDir, script);
    }
}
