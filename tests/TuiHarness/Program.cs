using System.Text;

namespace TuiHarness;

/// <summary>
/// Drives the real TUI code path from a real interactive console.
/// Console.SetIn only swaps the reader - the console handles stay attached,
/// so the TUI still believes it is interactive. Any exception Main lets
/// escape is written to harness-crash.txt instead of dying silently.
/// Usage: TuiHarness.exe <keys-file> [<wait-ms-after>]
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var keysFile = args.Length > 0 ? args[0] : "keys.txt";
        var waitAfter = args.Length > 1 && int.TryParse(args[1], out var w) ? w : 15000;

        var text = File.Exists(keysFile) ? File.ReadAllText(keysFile) : "";
        Console.SetIn(new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(text))));

        File.WriteAllText("harness-boot.txt", $"booted {DateTime.UtcNow:HH:mm:ss}, keys={text.Replace("\n", "\\n")}\n");
        try
        {
            var rc = await SteamCloudTamper.Tui.Program.Main();
            File.WriteAllText("harness-rc.txt", $"main returned rc={rc}\n");
        }
        catch (Exception ex)
        {
            File.WriteAllText("harness-crash.txt", ex.ToString());
        }

        // keep the console alive briefly so the window does not vanish before we inspect
        await Task.Delay(waitAfter);
        return 0;
    }
}
