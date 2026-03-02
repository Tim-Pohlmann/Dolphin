using Dolphin.Semgrep;

namespace Dolphin.Tests;

public class InstallerTests
{
    [Fact]
    public async Task EnsureInstalled_ReturnsValidBinaryPath()
    {
        // This test requires network access on first run to download Semgrep.
        // On subsequent runs it uses the cached binary.
        var binaryPath = await Installer.EnsureInstalledAsync();

        Assert.True(File.Exists(binaryPath), $"Binary not found at: {binaryPath}");
    }

    [Fact]
    public async Task GetInstalledInfo_ReturnsVersionString()
    {
        var (binary, version) = await Installer.GetInstalledInfoAsync();

        Assert.False(string.IsNullOrWhiteSpace(binary));
        Assert.False(string.IsNullOrWhiteSpace(version));
        // Semgrep version output typically starts with a digit or "semgrep"
        Assert.True(
            char.IsDigit(version[0]) || version.StartsWith("semgrep", StringComparison.OrdinalIgnoreCase),
            $"Unexpected version string: {version}"
        );
    }
}
