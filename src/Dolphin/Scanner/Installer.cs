using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Dolphin.Scanner;

public static class Installer
{
    /// <summary>
    /// Resolves the scanner binary (Opengrep), in priority order:
    /// 1. Bundled — next to the dolphin executable (placed there by BundleOpengrep MSBuild target at publish time)
    /// 2. PATH    — useful for developers who have Opengrep or Semgrep installed
    /// 3. Auto-download — fetches the correct platform binary from Opengrep GitHub Releases on first use
    /// </summary>
    public static async Task<string> EnsureInstalledAsync()
    {
        // 1. Bundled binary (published plugin path: same directory as dolphin)
        //    Named "opengrep" on Unix, "opengrep.exe" on Windows (placed by BundleOpengrep MSBuild target).
        var processDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var bundledName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "opengrep.exe" : "opengrep";
        var bundled = Path.Combine(processDir, bundledName);
        if (File.Exists(bundled) && IsExecutable(bundled))
        {
            var version = await GetVersionAsync(bundled);
            if (version != null) return bundled;
        }

        // 2. PATH (developer / CI installs: opengrep or semgrep)
        var inPath = FindInPath("opengrep") ?? FindInPath("semgrep");
        if (inPath != null)
        {
            var version = await GetVersionAsync(inPath);
            if (version != null) return inPath;
        }

        // 3. Auto-download the correct platform binary next to the dolphin executable.
        var downloaded = await DownloadAsync(processDir, bundledName);
        if (downloaded != null) return downloaded;

        throw new InvalidOperationException(
            "Scanner not found and auto-download failed. " +
            "Install Opengrep manually and ensure it is on your PATH. " +
            "See: https://opengrep.dev"
        );
    }

    public static async Task<(string Binary, string Version)> GetInstalledInfoAsync()
    {
        var binary = await EnsureInstalledAsync();
        var version = await GetVersionAsync(binary) ?? "unknown";
        return (binary, version);
    }

    private static async Task<string?> DownloadAsync(string targetDir, string targetName)
    {
        var binName = GetPlatformBinaryName();
        if (binName == null) return null;

        var url = $"https://github.com/opengrep/opengrep/releases/latest/download/{binName}";
        var dest = Path.Combine(targetDir, targetName);

        Console.Error.WriteLine($"Downloading Opengrep (LGPL 2.1) from {url} ...");

        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("dolphin");
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            // Write to a temp file first, then move atomically
            var tmp = dest + ".tmp";
            await using (var fs = File.Create(tmp))
                await response.Content.CopyToAsync(fs);

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                File.SetUnixFileMode(tmp, File.GetUnixFileMode(tmp) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);

            File.Move(tmp, dest, overwrite: true);
            Console.Error.WriteLine($"Opengrep downloaded to {dest}");

            var version = await GetVersionAsync(dest);
            return version != null ? dest : null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Auto-download failed: {ex.Message}");
            return null;
        }
    }

    private static string? GetPlatformBinaryName()
    {
        var arch = RuntimeInformation.ProcessArchitecture;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return arch == Architecture.Arm64 ? "opengrep_manylinux_aarch64" : "opengrep_manylinux_x86";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return arch == Architecture.Arm64 ? "opengrep_osx_arm64" : "opengrep_osx_x86";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "opengrep_windows_x86.exe";

        return null;
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
