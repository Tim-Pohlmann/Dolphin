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

    /// <summary>
    /// Resolves the src/Dolphin project path by walking up from the test
    /// output directory (e.g. tests/Dolphin.Tests/bin/Debug/net10.0/).
    /// </summary>
    private static string FindDolphinProjectPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Dolphin", "Dolphin.csproj");
            if (File.Exists(candidate))
                return Path.GetDirectoryName(candidate)!;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate src/Dolphin/Dolphin.csproj");
    }

    /// <summary>
    /// Finds the already-built Dolphin.dll by mirroring the current test output
    /// directory's config and TFM (e.g. Release/net10.0) under src/Dolphin/bin/.
    /// Using <c>dotnet exec</c> against this DLL is ~10× faster than
    /// <c>dotnet run --project</c> because it skips recompilation.
    /// </summary>
    private static string FindDolphinDllPath()
    {
        var projectPath = FindDolphinProjectPath();
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var tfm = Path.GetFileName(baseDir);
        var config = Path.GetFileName(Path.GetDirectoryName(baseDir)!);
        return Path.Combine(projectPath, "bin", config, tfm, "dolphin.dll");
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunDolphinAsync(
        string args, CancellationToken ct = default)
    {
        var dllPath = FindDolphinDllPath();
        var psi = new ProcessStartInfo("dotnet", $"exec \"{dllPath}\" {args}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

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
}
