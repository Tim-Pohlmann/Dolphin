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
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("Fake scanner uses a shell script; Unix-only");
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
    public async Task RunAsync_ExitCode7_ThrowsWithJsonErrorMessage()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("Fake scanner uses a shell script; Unix-only");
        var jsonStdout = """{"results":[],"errors":[{"message":"Invalid YAML file rules.yaml: mapping values are not allowed here"}]}""";
        var (tmpDir, fakeBinary) = CreateFakeScannerEnv(exitCode: 7, stderr: "chardet noise", stdout: jsonStdout);
        try
        {
            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => Runner.RunAsync(fakeBinary, tmpDir)
            );
            StringAssert.Contains(ex.Message, "Invalid YAML file");
            StringAssert.Contains(ex.Message, "mapping values are not allowed here");
            Assert.IsFalse(ex.Message.Contains("chardet noise", StringComparison.Ordinal), "Exception message should not contain stderr noise");
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task RunAsync_ExitCodeAbove2_Throws()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("Fake scanner uses a shell script; Unix-only");
        var (tmpDir, fakeBinary) = CreateFakeScannerEnv(exitCode: 3, stderr: "fatal error");
        try
        {
            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => Runner.RunAsync(fakeBinary, tmpDir)
            );
            StringAssert.Contains(ex.Message, "fatal error");
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task RunAsync_ExitCodeAbove2_FallsBackToStderr_WhenStdoutEmpty()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("Fake scanner uses a shell script; Unix-only");
        var (tmpDir, fakeBinary) = CreateFakeScannerEnv(exitCode: 3, stderr: "fatal error", stdout: "");
        try
        {
            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => Runner.RunAsync(fakeBinary, tmpDir)
            );
            StringAssert.Contains(ex.Message, "fatal error");
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task RunAsync_ExitCodeAbove2_FallsBackToStderr_WhenStdoutIsInvalidJson()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("Fake scanner uses a shell script; Unix-only");
        var (tmpDir, fakeBinary) = CreateFakeScannerEnv(exitCode: 3, stderr: "fatal error", stdout: "not json");
        try
        {
            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => Runner.RunAsync(fakeBinary, tmpDir)
            );
            StringAssert.Contains(ex.Message, "fatal error");
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    // ── ParseFindings / ruleId filter / missing rules (no real scanner needed) ──

    [TestMethod]
    public async Task RunAsync_RulesFileMissing_ThrowsFileNotFound_WithoutInvokingBinary()
    {
        // FileNotFoundException is thrown before Process.Start, so the binary path is irrelevant.
        var tmpDir = Path.Combine(Path.GetTempPath(), $"dolphin-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tmpDir); // no .dolphin/rules.yaml

        try
        {
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(
                () => Runner.RunAsync("/nonexistent/scanner", tmpDir)
            );
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task RunAsync_WithFindings_ParsesAllFields()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("Fake scanner uses a shell script; Unix-only");

        var tmpDir = Path.Combine(Path.GetTempPath(), $"dolphin-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(Path.Combine(tmpDir, ".dolphin"));
        Directory.CreateDirectory(Path.Combine(tmpDir, "src"));
        File.WriteAllText(Path.Combine(tmpDir, ".dolphin", "rules.yaml"), "rules: []");
        File.WriteAllText(Path.Combine(tmpDir, "src", "file.ts"), "const x = 1;");

        // Build JSON mimicking real opengrep output; path must be absolute so Runner
        // can compute a relative path with Path.GetRelativePath.
        var fullPath = Path.Combine(tmpDir, "src", "file.ts").Replace("\\", "/");
        var json = $$"""
            {
              "results": [
                {
                  "check_id": "no-const",
                  "path": "{{fullPath}}",
                  "start": {"line": 1, "col": 7},
                  "extra": {
                    "message": "avoid const",
                    "severity": "ERROR",
                    "lines": "const x = 1;"
                  }
                },
                {
                  "check_id": "prefer-let",
                  "path": "{{fullPath}}",
                  "start": {"line": 1, "col": 7},
                  "extra": {
                    "message": "use let",
                    "severity": "WARNING",
                    "lines": "const x = 1;"
                  }
                },
                {
                  "check_id": "info-rule",
                  "path": "{{fullPath}}",
                  "start": {"line": 1, "col": 7},
                  "extra": {
                    "message": "note",
                    "severity": "INFO",
                    "lines": ""
                  }
                }
              ]
            }
            """;

        var script = CreateScannerScript(tmpDir, exitCode: 1, json: json);

        try
        {
            var result = await Runner.RunAsync(script, tmpDir);

            Assert.AreEqual(3, result.Findings.Count, "Expected all three findings to be parsed");

            var err = result.Findings.FirstOrDefault(f => f.RuleId == "no-const");
            Assert.IsNotNull(err);
            Assert.AreEqual(Severity.Error, err.Severity);
            Assert.AreEqual(1, err.Line);
            Assert.AreEqual(7, err.Column);
            Assert.AreEqual("avoid const", err.Message);
            Assert.AreEqual("const x = 1;", err.MatchedText);

            var warn = result.Findings.FirstOrDefault(f => f.RuleId == "prefer-let");
            Assert.IsNotNull(warn);
            Assert.AreEqual(Severity.Warning, warn.Severity);

            var info = result.Findings.FirstOrDefault(f => f.RuleId == "info-rule");
            Assert.IsNotNull(info);
            Assert.AreEqual(Severity.Info, info.Severity);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task RunAsync_WithRuleIdFilter_FakeBinary_ReturnsOnlyMatchingFindings()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("Fake scanner uses a shell script; Unix-only");

        var tmpDir = Path.Combine(Path.GetTempPath(), $"dolphin-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(Path.Combine(tmpDir, ".dolphin"));
        File.WriteAllText(Path.Combine(tmpDir, ".dolphin", "rules.yaml"), "rules: []");

        var fullPath = Path.Combine(tmpDir, "src", "file.ts").Replace("\\", "/");
        var json = $$"""
            {
              "results": [
                {
                  "check_id": "rule-a",
                  "path": "{{fullPath}}",
                  "start": {"line": 1, "col": 1},
                  "extra": {"message": "a", "severity": "ERROR"}
                },
                {
                  "check_id": "rule-b",
                  "path": "{{fullPath}}",
                  "start": {"line": 2, "col": 1},
                  "extra": {"message": "b", "severity": "WARNING"}
                }
              ]
            }
            """;
        var script = CreateScannerScript(tmpDir, exitCode: 1, json: json);

        try
        {
            var result = await Runner.RunAsync(script, tmpDir, ruleId: "rule-a");

            Assert.AreEqual(1, result.Findings.Count, "Expected only rule-a after filter");
            Assert.AreEqual("rule-a", result.Findings[0].RuleId);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    /// <summary>
    /// Creates a fake scanner script inside <paramref name="tmpDir"/> that outputs
    /// <paramref name="json"/> (or empty results) and exits with <paramref name="exitCode"/>.
    /// </summary>
    private static string CreateScannerScript(string tmpDir, int exitCode, string? json = null)
    {
        var stdoutJson = json ?? """{"results":[]}""";
        var stdoutFile = Path.Combine(tmpDir, "fake-stdout.txt");
        File.WriteAllText(stdoutFile, stdoutJson);
        var script = Path.Combine(tmpDir, "fake-scanner.sh");
        File.WriteAllText(script,
            $"#!/bin/sh\ncat '{stdoutFile}'\nexit {exitCode}\n");
#pragma warning disable CA1416
        File.SetUnixFileMode(script,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA1416
        return script;
    }

    /// <summary>
    /// Creates a temp directory with a minimal .dolphin/rules.yaml and a fake scanner script
    /// that writes the provided stdout content (or empty JSON results by default) and exits
    /// with the given code.
    /// </summary>
    private static (string tmpDir, string fakeBinary) CreateFakeScannerEnv(
        int exitCode, string stderr = "", string? stdout = null)
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"dolphin-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(Path.Combine(tmpDir, ".dolphin"));
        File.WriteAllText(Path.Combine(tmpDir, ".dolphin", "rules.yaml"), "rules: []");

        var stdoutJson = stdout ?? """{"results":[]}""";
        // Write stdout/stderr to files so the script can cat them without quoting issues
        var stdoutFile = Path.Combine(tmpDir, "fake-stdout.txt");
        var stderrFile = Path.Combine(tmpDir, "fake-stderr.txt");
        File.WriteAllText(stdoutFile, stdoutJson);
        File.WriteAllText(stderrFile, stderr);
        var script = Path.Combine(tmpDir, "fake-scanner.sh");
        File.WriteAllText(script,
            $"#!/bin/sh\ncat '{stdoutFile}'\ncat '{stderrFile}' >&2\nexit {exitCode}\n");
#pragma warning disable CA1416 // SetUnixFileMode is not supported on Windows; these tests are Unix-only
        File.SetUnixFileMode(script,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
#pragma warning restore CA1416

        return (tmpDir, script);
    }
}
