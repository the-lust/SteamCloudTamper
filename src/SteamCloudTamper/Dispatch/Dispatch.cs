using SteamCloudTamper.App;

namespace SteamCloudTamper.Dispatch;

public static class CliDispatcher
{
    public static Task<int> RunAsync(string[] args) => SteamCloudTamper.Cli.Program.Main(args);
}

public static class TuiDispatcher
{
    /// <summary>The TUI also enables the terminal itself; this is the single-booked entry.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        SteamCloudTamper.Core.AnsiTerminal.Enable();
        return await SteamCloudTamper.Tui.Program.Main();
    }
}

public static class VersionDispatcher
{
    public static int PrintVersion()
    {
        Console.WriteLine($"{AppInfo.Name} {AppInfo.Version} (net10.0, windows) - dual-face build: TUI (no args) or CLI (flags).");
        return 0;
    }
}