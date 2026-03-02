using System.ComponentModel;
using Dolphin.Semgrep;
using ModelContextProtocol.Server;

namespace Dolphin.Mcp.Tools;

[McpServerToolType]
public sealed class SetupSemgrepTool
{
    [McpServerTool, Description("Download and verify the Semgrep analysis engine. Returns the installed version.")]
    public async Task<string> SetupSemgrep()
    {
        var (binary, version) = await Installer.GetInstalledInfoAsync();
        return $"Semgrep {version} ready at {binary}";
    }
}
