using Dolphin.Semgrep;

namespace Dolphin.Tests;

public class InstallerTests
{
    [Fact]
    public async Task EnsureInstalled_ReturnsValidBinaryPath_WhenSemgrepOnPath()
    {
        // This test only runs when Semgrep is available (bundled next to the
        // test executable, or on PATH). In a published plugin, the BundleSemgrep
        // MSBuild target guarantees the binary is present.
        var semgrepInPath = FindInPath("semgrep");
        if (semgrepInPath == null)
        {
            // Semgrep not available in this environment — skip rather than fail.
            return;
        }

        var binaryPath = await Installer.EnsureInstalledAsync();

        Assert.True(File.Exists(binaryPath), $"Binary not found at: {binaryPath}");
    }

    [Fact]
    public async Task GetInstalledInfo_ReturnsVersionString_WhenSemgrepOnPath()
    {
        var semgrepInPath = FindInPath("semgrep");
        if (semgrepInPath == null) return;

        var (binary, version) = await Installer.GetInstalledInfoAsync();

        Assert.False(string.IsNullOrWhiteSpace(binary));
        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.True(
            char.IsDigit(version[0]) || version.StartsWith("semgrep", StringComparison.OrdinalIgnoreCase),
            $"Unexpected version string: {version}"
        );
    }

    private static string? FindInPath(string name)
    {
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in paths)
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
