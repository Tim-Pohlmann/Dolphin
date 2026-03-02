using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Dolphin.Semgrep;

public static class Installer
{
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".dolphin", "bin"
    );

    private static readonly string CachedBinary = Path.Combine(CacheDir, "semgrep");

    // GitHub Releases base URL for Semgrep binaries
    private const string ReleaseBaseUrl =
        "https://github.com/semgrep/semgrep/releases/latest/download";

    public static async Task<string> EnsureInstalledAsync(IProgress<string>? progress = null)
    {
        // 1. Check cached binary
        if (File.Exists(CachedBinary) && IsExecutable(CachedBinary))
        {
            var version = await GetVersionAsync(CachedBinary);
            if (version != null) return CachedBinary;
        }

        // 2. Check PATH
        var inPath = FindInPath("semgrep");
        if (inPath != null)
        {
            var version = await GetVersionAsync(inPath);
            if (version != null) return inPath;
        }

        // 3. Download from GitHub Releases
        var binaryName = GetPlatformBinaryName();
        var url = $"{ReleaseBaseUrl}/{binaryName}";
        progress?.Report($"Downloading Semgrep from {url}...");

        Directory.CreateDirectory(CacheDir);

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("dolphin/1.0");
        // Follow redirects (GitHub Releases redirects to S3)
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var tmpPath = CachedBinary + ".tmp";
        await using (var fs = File.Create(tmpPath))
        await using (var stream = await response.Content.ReadAsStreamAsync())
        {
            await stream.CopyToAsync(fs);
        }

        File.Move(tmpPath, CachedBinary, overwrite: true);
        SetExecutable(CachedBinary);

        var installedVersion = await GetVersionAsync(CachedBinary)
            ?? throw new InvalidOperationException("Semgrep downloaded but failed to run.");

        progress?.Report($"Semgrep {installedVersion} installed at {CachedBinary}");
        return CachedBinary;
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

    private static string GetPlatformBinaryName()
    {
        var os = RuntimeInformation.OSDescription.ToLower();
        var arch = RuntimeInformation.ProcessArchitecture;

        if (os.Contains("linux"))
            return arch == Architecture.Arm64 ? "semgrep-linux-aarch64" : "semgrep-linux-x86_64";

        if (os.Contains("darwin") || os.Contains("mac"))
            return arch == Architecture.Arm64 ? "semgrep-osx-arm64" : "semgrep-osx-x86_64";

        throw new PlatformNotSupportedException(
            $"No Semgrep binary available for {RuntimeInformation.OSDescription} ({arch}). " +
            "Install Semgrep manually: https://semgrep.dev/docs/getting-started/"
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

    private static bool IsExecutable(string path)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var info = new FileInfo(path);
            // Check Unix execute bit via UnixFileMode
            try
            {
                var mode = File.GetUnixFileMode(path);
                return mode.HasFlag(UnixFileMode.UserExecute);
            }
            catch { return false; }
        }
        return true;
    }

    private static void SetExecutable(string path)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                var mode = File.GetUnixFileMode(path);
                File.SetUnixFileMode(path,
                    mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
            catch { /* best effort */ }
        }
    }
}
