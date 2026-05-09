using System.Diagnostics;
using System.Text.Json;
using Dolphin.Cli;
using Dolphin.Scanner;

namespace Dolphin.Tests;

[TestClass]
public class HookCommandTests
{
    // ── Unit tests ─────────────────────────────────────────────────────────────

    [TestMethod]
    [DataRow("/project/.dolphin/rules.yaml")]
    [DataRow("/project/.dolphin/rules.yml")]
    [DataRow("/project/.dolphin/RULES.YAML")]
    [DataRow(".dolphin/rules.yaml")]
    public void IsRulesFile_RulesFilePaths_ReturnsTrue(string path)
    {
        Assert.IsTrue(HookCommand.IsRulesFile(path));
    }

    [TestMethod]
    [DataRow("/project/src/foo.ts")]
    [DataRow("/project/.dolphin/other.yaml")]
    [DataRow("/project/rules.yaml")]
    [DataRow("rules.yaml")]
    public void IsRulesFile_NonRulesPaths_ReturnsFalse(string path)
    {
        Assert.IsFalse(HookCommand.IsRulesFile(path));
    }

    // ── CLI integration tests ──────────────────────────────────────────────────

    [TestMethod]
    public async Task PostToolUse_RulesYamlWithSchemaError_PrintsDiagnostic()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"dolphin-hook-test-{Guid.NewGuid()}");
        var dolphinDir = Path.Combine(tmpDir, ".dolphin");
        Directory.CreateDirectory(dolphinDir);
        var rulesPath = Path.Combine(dolphinDir, "rules.yaml");
        // Missing required fields → schema validation should emit diagnostics
        await File.WriteAllTextAsync(rulesPath, "rules:\n  - id: broken\n");

        try
        {
            var hookInput = JsonSerializer.Serialize(new
            {
                session_id  = "test",
                tool_name   = "Write",
                tool_input  = new { file_path = rulesPath },
                tool_response = "ok"
            });

            var (exitCode, stdout, _) = await RunHookAsync(hookInput);

            Assert.AreEqual(0, exitCode);
            Assert.IsTrue(stdout.Contains("rules.yaml:"),
                $"Expected diagnostic output starting with 'rules.yaml:', got: {stdout}");
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task PostToolUse_ValidRulesYaml_ProducesNoOutput()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"dolphin-hook-test-{Guid.NewGuid()}");
        var dolphinDir = Path.Combine(tmpDir, ".dolphin");
        Directory.CreateDirectory(dolphinDir);
        var rulesPath = Path.Combine(dolphinDir, "rules.yaml");
        await File.WriteAllTextAsync(rulesPath, """
            rules:
              - id: no-console-log
                message: "Remove console.log"
                languages: [typescript]
                severity: WARNING
                pattern: console.log(...)
            """);

        try
        {
            var hookInput = JsonSerializer.Serialize(new
            {
                session_id  = "test",
                tool_name   = "Write",
                tool_input  = new { file_path = rulesPath },
                tool_response = "ok"
            });

            var (exitCode, stdout, _) = await RunHookAsync(hookInput);

            Assert.AreEqual(0, exitCode);
            Assert.AreEqual(string.Empty, stdout.Trim(),
                $"Expected no output for a valid rules file, got: {stdout}");
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task PostToolUse_EmptyStdin_ExitsCleanly()
    {
        var (exitCode, stdout, _) = await RunHookAsync(string.Empty);
        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(string.Empty, stdout.Trim());
    }

    [TestMethod]
    public async Task PostToolUse_NonRulesFile_ProducesNoOutput()
    {
        var hookInput = JsonSerializer.Serialize(new
        {
            session_id  = "test",
            tool_name   = "Write",
            tool_input  = new { file_path = "/some/project/src/foo.ts" },
            tool_response = "ok"
        });

        var (exitCode, stdout, _) = await RunHookAsync(hookInput);

        // Source-file check requires scanner; simply assert no crash and no rules.yaml output
        Assert.AreEqual(0, exitCode);
        Assert.IsFalse(stdout.Contains("rules.yaml:"),
            $"Expected no rules.yaml output for a non-rules file, got: {stdout}");
    }

    [TestMethod]
    public async Task PostToolUse_SourceFileWithErrorFinding_PrintsFinding()
    {
        // Graceful skip if scanner unavailable
        try { await Installer.EnsureInstalledAsync(); }
        catch { return; }

        var tmpDir = Path.Combine(Path.GetTempPath(), $"dolphin-hook-test-{Guid.NewGuid()}");
        var dolphinDir = Path.Combine(tmpDir, ".dolphin");
        var srcDir = Path.Combine(tmpDir, "src");
        Directory.CreateDirectory(dolphinDir);
        Directory.CreateDirectory(srcDir);

        var fixturesDir = Path.Combine(AppContext.BaseDirectory, "fixtures");
        File.Copy(Path.Combine(fixturesDir, "rules.yaml"), Path.Combine(dolphinDir, "rules.yaml"));
        var badFile = Path.Combine(srcDir, "bad-file.ts");
        File.Copy(Path.Combine(fixturesDir, "sample-src", "bad-file.ts"), badFile);

        try
        {
            var hookInput = JsonSerializer.Serialize(new
            {
                session_id    = "test",
                tool_name     = "Write",
                tool_input    = new { file_path = badFile },
                tool_response = "ok"
            });

            var (exitCode, stdout, _) = await RunHookAsync(hookInput);

            Assert.AreEqual(0, exitCode);
            Assert.IsTrue(stdout.Length > 0,
                $"Expected finding output for a file with ERROR violations, got empty stdout");
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    // ── Helper ─────────────────────────────────────────────────────────────────

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunHookAsync(
        string stdinData, CancellationToken ct = default)
    {
        var projectPath = TestProcessHelper.FindDolphinProjectPath();
        var config      = TestProcessHelper.CurrentConfiguration();

        var psi = new ProcessStartInfo(
            "dotnet",
            $"run --no-build --configuration {config} --project \"{projectPath}\" -- hook post-tool-use")
        {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet process");

        await process.StandardInput.WriteAsync(stdinData);
        process.StandardInput.Close();

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return (process.ExitCode, stdout, stderr);
    }
}
