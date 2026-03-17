using System.Diagnostics;
using System.Text.Json;
using Dolphin.Scanner;

namespace Dolphin.Tests;

/// <summary>
/// End-to-end CLI tests that exercise the full pipeline:
/// argument parsing → Installer → Runner → Formatter → exit code.
///
/// Each test spawns a child process via `dotnet run` so that
/// Environment.Exit() in CheckCommand doesn't terminate the test runner.
/// </summary>
[TestClass]
public class CheckCommandTests
{
    private static readonly string FixturesDir = Path.Combine(
        AppContext.BaseDirectory, "fixtures"
    );

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunDolphinAsync(
        string args, string? prependPath = null, CancellationToken ct = default)
    {
        var projectPath = TestProcessHelper.FindDolphinProjectPath();
        var config = TestProcessHelper.CurrentConfiguration();
        // --no-build skips MSBuild recompilation. Must pass --configuration so dotnet
        // run looks in bin/{config}/ rather than defaulting to Debug (which may not exist).
        var psi = new ProcessStartInfo("dotnet", $"run --no-build --configuration {config} --project \"{projectPath}\" -- {args}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (prependPath != null)
            psi.Environment["PATH"] = prependPath + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet process");

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return (process.ExitCode, stdout, stderr);
    }

    // ──────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Check_ExitCode2_WhenDirectoryDoesNotExist()
    {
        var (exitCode, _, _) = await RunDolphinAsync(
            "check --cwd /nonexistent/dolphin-test-path-that-does-not-exist"
        );

        Assert.AreEqual(2, exitCode);
    }

    [TestMethod]
    public async Task Check_ExitCode2_WhenRulesFileMissing()
    {
        // Graceful skip if scanner unavailable
        try { await Installer.EnsureInstalledAsync(); }
        catch { return; }

        var tmpDir = Path.Combine(Path.GetTempPath(), $"dolphin-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            // No .dolphin/rules.yaml — Runner should throw FileNotFoundException → exit 2
            var (exitCode, _, _) = await RunDolphinAsync($"check --cwd \"{tmpDir}\"");

            Assert.AreEqual(2, exitCode);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task Check_ExitCode1_WhenErrorFindingsExist()
    {
        // Graceful skip if scanner unavailable
        try { await Installer.EnsureInstalledAsync(); }
        catch { return; }

        var tmpDir = Path.Combine(Path.GetTempPath(), $"dolphin-test-{Guid.NewGuid()}");
        var tmpDolphinDir = Path.Combine(tmpDir, ".dolphin");
        var tmpSrcDir = Path.Combine(tmpDir, "src");
        Directory.CreateDirectory(tmpDolphinDir);
        Directory.CreateDirectory(tmpSrcDir);

        File.Copy(
            Path.Combine(FixturesDir, "rules.yaml"),
            Path.Combine(tmpDolphinDir, "rules.yaml")
        );
        // bad-file.ts contains a no-hardcoded-secret (ERROR) finding
        File.Copy(
            Path.Combine(FixturesDir, "sample-src", "bad-file.ts"),
            Path.Combine(tmpSrcDir, "bad-file.ts")
        );

        try
        {
            var (exitCode, _, _) = await RunDolphinAsync($"check --cwd \"{tmpDir}\"");

            Assert.AreEqual(1, exitCode);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task Check_ExitCode0_WhenOnlyWarningFindingsExist()
    {
        // Graceful skip if scanner unavailable
        try { await Installer.EnsureInstalledAsync(); }
        catch { return; }

        var tmpDir = Path.Combine(Path.GetTempPath(), $"dolphin-test-{Guid.NewGuid()}");
        var tmpDolphinDir = Path.Combine(tmpDir, ".dolphin");
        var tmpSrcDir = Path.Combine(tmpDir, "src");
        Directory.CreateDirectory(tmpDolphinDir);
        Directory.CreateDirectory(tmpSrcDir);

        File.Copy(
            Path.Combine(FixturesDir, "rules.yaml"),
            Path.Combine(tmpDolphinDir, "rules.yaml")
        );
        // clean-file.ts has no violations; write an inline file with only a
        // console.log (WARNING severity) — no hardcoded secrets (ERROR).
        await File.WriteAllTextAsync(
            Path.Combine(tmpSrcDir, "warn-only.ts"),
            "export function greet(name: string) { console.log(name); }\n"
        );

        try
        {
            var (exitCode, _, _) = await RunDolphinAsync($"check --cwd \"{tmpDir}\"");

            // Warnings don't trigger exit 1; only ERRORs do.
            Assert.AreEqual(0, exitCode);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task Check_JsonFormat_OutputsValidJsonArray()
    {
        // Graceful skip if scanner unavailable
        try { await Installer.EnsureInstalledAsync(); }
        catch { return; }

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
            var (exitCode, stdout, _) = await RunDolphinAsync(
                $"check --cwd \"{tmpDir}\" --format json"
            );

            // Exit code 2 means a fatal error — scanner unavailable etc.
            Assert.AreNotEqual(2, exitCode);

            using var doc = JsonDocument.Parse(stdout.Trim());
            Assert.AreEqual(JsonValueKind.Array, doc.RootElement.ValueKind);
            Assert.IsTrue(doc.RootElement.GetArrayLength() > 0,
                "Expected at least one finding in the JSON output");
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task Check_PrintsWarningToStderr_WhenScannerExitsCode2()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("Fake scanner uses a shell script; Unix-only");
        var tmpDir = Path.Combine(Path.GetTempPath(), $"dolphin-test-{Guid.NewGuid()}");
        var fakeBinDir = Path.Combine(Path.GetTempPath(), $"dolphin-fakebin-{Guid.NewGuid()}");
        Directory.CreateDirectory(Path.Combine(tmpDir, ".dolphin"));
        Directory.CreateDirectory(fakeBinDir);
        File.WriteAllText(Path.Combine(tmpDir, ".dolphin", "rules.yaml"), "rules: []");

        // Fake opengrep: responds to --version normally, returns exit code 2 for scans
        var fakeScript = Path.Combine(fakeBinDir, "opengrep");
#pragma warning disable CA1416
        File.WriteAllText(fakeScript,
            "#!/bin/sh\nif [ \"$1\" = \"--version\" ]; then echo '1.0.0'; exit 0; fi\n" +
            "echo '{\"results\":[]}'\necho 'scan warning' >&2\nexit 2\n");
        File.SetUnixFileMode(fakeScript,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
#pragma warning restore CA1416

        try
        {
            var (exitCode, _, stderr) = await RunDolphinAsync(
                $"check --cwd \"{tmpDir}\"", prependPath: fakeBinDir);

            Assert.AreEqual(0, exitCode, "Exit code should be 0 (no ERROR findings)");
            StringAssert.Contains(stderr, "Warning:");
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
            Directory.Delete(fakeBinDir, recursive: true);
        }
    }
}
