using SteamCloudTamper.Dispatch;

namespace SteamCloudTamper;

/// <summary>
/// Single binary, dual face:
///   SteamCloudTamper.exe                 -> TUI (interactive console only)
///   SteamCloudTamper.exe --tui           -> force TUI
///   SteamCloudTamper.exe <command> [..]  -> CLI (all commands; --cli forces it)
///   SteamCloudTamper.exe --version       -> version info
/// All work is delegated through the same projects the plugin DLL lanes use.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var interactive = !Console.IsOutputRedirected && !Console.IsInputRedirected;

        if (args.Length > 0 && (args[0] == "--version" || args[0] == "-v"))
            return VersionDispatcher.PrintVersion();

        if (args.Length > 0 && args[0] == "--tui")
            return await TuiDispatcher.RunAsync(args[1..]);

        if (args.Length > 0 && args[0] == "--cli")
            return await CliDispatcher.RunAsync(args[1..]);

        if (args.Length == 0 && interactive)
            return await TuiDispatcher.RunAsync([]);

        return await CliDispatcher.RunAsync(args);
    }
}