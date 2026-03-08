namespace Dolphin.Tests;

/// <summary>
/// Shared helpers for tests that spawn Dolphin as a child process.
/// </summary>
internal static class TestProcessHelper
{
    /// <summary>
    /// Resolves the src/Dolphin project path by walking up from the test
    /// output directory (e.g. tests/Dolphin.Tests/bin/Debug/net10.0/).
    /// </summary>
    internal static string FindDolphinProjectPath()
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
    /// Returns the build configuration (e.g. "Release" or "Debug") inferred from
    /// AppContext.BaseDirectory, which has the form ...bin/{config}/{tfm}/.
    /// </summary>
    internal static string CurrentConfiguration()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFileName(Path.GetDirectoryName(baseDir)!) is { } c && c.Length > 0 ? c : "Release";
    }
}
