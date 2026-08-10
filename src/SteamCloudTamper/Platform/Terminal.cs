using SteamCloudTamper.Core;

namespace SteamCloudTamper.Platform;

/// <summary>Terminal bootstrapping shared by both faces: VT processing + UTF-8.</summary>
public static class Terminal
{
    public static void Boot()
    {
        AnsiTerminal.Enable();
    }
}