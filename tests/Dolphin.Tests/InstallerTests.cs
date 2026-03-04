using Dolphin.Semgrep;

namespace Dolphin.Tests;

public class InstallerTests
{
    [Fact]
    public async Task EnsureInstalled_ReturnsValidBinaryPath_WhenScannerOnPath()
    {
        // This test only runs when a scanner is available (bundled next to the
        // test executable, or on PATH). In a published plugin, the BundleSemgrep
        // MSBuild target guarantees the binary is present.
        if (FindScanner() == null) return; // skip — no scanner in this environment

        var binaryPath = await Installer.EnsureInstalledAsync();

        Assert.True(File.Exists(binaryPath), $"Binary not found at: {binaryPath}");
    }

    [Fact]
    public async Task GetInstalledInfo_ReturnsVersionString_WhenScannerOnPath()
    {
        if (FindScanner() == null) return;

        var (binary, version) = await Installer.GetInstalledInfoAsync();

        Assert.False(string.IsNullOrWhiteSpace(binary));
        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.True(
            char.IsDigit(version[0]),
            $"Unexpected version string: {version}"
        );
    }

    private static string? FindScanner()
    {
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var name in new[] { "semgrep", "opengrep" })
            foreach (var dir in paths)
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }
}
