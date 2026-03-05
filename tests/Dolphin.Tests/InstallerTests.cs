using Dolphin.Scanner;

namespace Dolphin.Tests;

public class InstallerTests
{
    [Fact]
    public async Task EnsureInstalled_ReturnsValidBinaryPath_WhenScannerAvailable()
    {
        // Skip if no scanner is available (bundled binary or on PATH).
        // In a published plugin, the BundleScanner MSBuild target guarantees presence.
        string binaryPath;
        try { binaryPath = await Installer.EnsureInstalledAsync(); }
        catch { Assert.Skip("No scanner available in this environment"); return; }

        Assert.True(File.Exists(binaryPath), $"Binary not found at: {binaryPath}");
    }

    [Fact]
    public async Task GetInstalledInfo_ReturnsVersionString_WhenScannerAvailable()
    {
        try { await Installer.EnsureInstalledAsync(); }
        catch { Assert.Skip("No scanner available in this environment"); return; }

        var (binary, version) = await Installer.GetInstalledInfoAsync();

        Assert.False(string.IsNullOrWhiteSpace(binary));
        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.True(char.IsDigit(version[0]), $"Unexpected version string: {version}");
    }
}
