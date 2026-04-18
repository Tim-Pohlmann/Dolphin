using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Dolphin.Scanner;

public static class Installer
{
    /// <summary>
    /// Resolves the scanner binary (Opengrep), in priority order:
    /// 1. Bundled — next to the dolphin executable (placed there by BundleOpengrep MSBuild target at publish time)
    /// 2. PATH    — useful for developers who have Opengrep or Semgrep installed
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

        throw new InvalidOperationException(
            "Scanner not found. " +
            "If running from source (dotnet run), install Opengrep and ensure it is on your PATH. " +
            "See: https://opengrep.dev"
        );
    }

    public static async Task<(string Binary, string Version)> GetInstalledInfoAsync()
    {
        var binary = await EnsureInstalledAsync();
        var version = await GetVersionAsync(binary) ?? "unknown";
        return (binary, version);
    }

    // Bounded wait for the --version probe.  Without this, a broken or hung scanner binary
    // can block resolver callers (e.g. the LSP's ResolveUnderLockAsync) indefinitely, which
    // in turn blocks DrainValidationsAsync on shutdown.
    private static readonly TimeSpan VersionProbeTimeout = TimeSpan.FromSeconds(5);

    private static async Task<string?> GetVersionAsync(string binaryPath)
    {
        Process? proc = null;
        try
        {
            var psi = new ProcessStartInfo(binaryPath, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            proc = Process.Start(psi)!;
            using var timeoutCts = new CancellationTokenSource(VersionProbeTimeout);
            var output = await proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            await proc.WaitForExitAsync(timeoutCts.Token);
            return proc.ExitCode == 0 ? output.Trim() : null;
        }
        catch
        {
            // Includes OperationCanceledException from the timeout; kill the process so a
            // hung --version probe does not leak into a zombie/long-lived child.
            try { proc?.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            return null;
        }
        finally
        {
            proc?.Dispose();
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
