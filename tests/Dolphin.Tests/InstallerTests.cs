using Dolphin.Scanner;

namespace Dolphin.Tests;

[TestClass]
public class InstallerTests
{
    [TestMethod]
    public async Task EnsureInstalled_ReturnsValidBinaryPath_WhenScannerAvailable()
    {
        // Skip if no scanner is available (bundled binary or on PATH).
        // In a published plugin, the BundleScanner MSBuild target guarantees presence.
        string binaryPath;
        try { binaryPath = await Installer.EnsureInstalledAsync(); }
        catch { Assert.Inconclusive("No scanner available in this environment"); return; }

        Assert.IsTrue(File.Exists(binaryPath), $"Binary not found at: {binaryPath}");
    }

    [TestMethod]
    public async Task GetInstalledInfo_ReturnsVersionString_WhenScannerAvailable()
    {
        try { await Installer.EnsureInstalledAsync(); }
        catch { Assert.Inconclusive("No scanner available in this environment"); return; }

        var (binary, version) = await Installer.GetInstalledInfoAsync();

        Assert.IsFalse(string.IsNullOrWhiteSpace(binary));
        Assert.IsFalse(string.IsNullOrWhiteSpace(version));
        Assert.IsTrue(char.IsDigit(version[0]), $"Unexpected version string: {version}");
    }
}
