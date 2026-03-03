using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Dolphin.Semgrep;

public static class Installer
{
    /// <summary>
    /// Resolves the Semgrep binary, in priority order:
    /// 1. Bundled — next to the dolphin executable (placed there by BundleSemgrep MSBuild target at publish time)
    /// 2. PATH    — useful for developers who have Semgrep globally installed
    /// </summary>
    public static async Task<string> EnsureInstalledAsync()
    {
        // 1. Bundled binary (published plugin path: same directory as dolphin)
        var processDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var bundled = Path.Combine(processDir, "semgrep");
        if (File.Exists(bundled) && IsExecutable(bundled))
        {
            var version = await GetVersionAsync(bundled);
            if (version != null) return bundled;
        }

        // 2. PATH (developer / CI installs)
        var inPath = FindInPath("semgrep");
        if (inPath != null)
        {
            var version = await GetVersionAsync(inPath);
            if (version != null) return inPath;
        }

        throw new InvalidOperationException(
            "Semgrep not found. " +
            "If running from source (dotnet run), install Semgrep and ensure it is on your PATH. " +
            "See: https://semgrep.dev/docs/getting-started/"
        );
    }

    public static async Task<(string Binary, string Version)> GetInstalledInfoAsync()
    {
        var binary = await EnsureInstalledAsync();
        var version = await GetVersionAsync(binary) ?? "unknown";
        return (binary, version);
    }

    private static async Task<string?> GetVersionAsync(string binaryPath)
    {
        try
        {
            var psi = new ProcessStartInfo(binaryPath, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var proc = Process.Start(psi)!;
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return proc.ExitCode == 0 ? output.Trim() : null;
        }
        catch
        {
            return null;
        }
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

    private static bool IsExecutable(string path)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                var mode = File.GetUnixFileMode(path);
                return mode.HasFlag(UnixFileMode.UserExecute);
            }
            catch { return false; }
        }
        return true;
    }
}
