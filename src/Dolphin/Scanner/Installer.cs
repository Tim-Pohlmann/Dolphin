using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Dolphin.Scanner;

public static class Installer
{
    /// <summary>
    /// Resolves the scanner binary (Opengrep/Semgrep), in priority order:
    /// 1. Bundled — next to the dolphin executable (placed there by BundleSemgrep MSBuild target at publish time)
    /// 2. PATH    — useful for developers who have Opengrep or Semgrep installed
    /// </summary>
    public static async Task<string> EnsureInstalledAsync()
    {
        // 1. Bundled binary (published plugin path: same directory as dolphin)
        //    Named "semgrep" on Unix, "semgrep.exe" on Windows (placed by BundleSemgrep MSBuild target).
        var processDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var bundledName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "semgrep.exe" : "semgrep";
        var bundled = Path.Combine(processDir, bundledName);
        if (File.Exists(bundled) && IsExecutable(bundled))
        {
            var version = await GetVersionAsync(bundled);
            if (version != null) return bundled;
        }

        // 2. PATH (developer / CI installs: semgrep, opengrep, or semgrep.exe on Windows)
        var inPath = FindInPath("semgrep") ?? FindInPath("opengrep");
        if (inPath != null)
        {
            var version = await GetVersionAsync(inPath);
            if (version != null) return inPath;
        }

        throw new InvalidOperationException(
            "Scanner not found. " +
            "If running from source (dotnet run), install Opengrep or Semgrep and ensure it is on your PATH. " +
            "See: https://opengrep.dev or https://semgrep.dev/docs/getting-started/"
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
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in paths)
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
            if (isWindows)
            {
                var withExe = Path.Combine(dir, name + ".exe");
                if (File.Exists(withExe)) return withExe;
            }
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
