using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SteamCloudTamper.Core;

/// <summary>
/// Terminal plumbing for the ANSI colored BRAND.txt header and raw glyph output:
/// enables Virtual Terminal Processing on Windows consoles and outputs UTF-8 so the
/// art (ESC sequences + block glyphs) renders exactly as authored. When stdout is
/// redirected (piped/log), ANSI sequences are stripped instead so logs stay clean.
/// </summary>
public static partial class AnsiTerminal
{
    private const uint StdOutputHandle = unchecked((uint)-11);
    private const uint EnableProcessedOutput = 0x0001;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(uint nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    private static bool _initialized;

    /// <summary>Turns on VT-processing + UTF-8 for the current console (safe no-op elsewhere).</summary>
    public static void Enable()
    {
        if (_initialized) return;
        _initialized = true;

        try { Console.OutputEncoding = new UTF8Encoding(false); } catch { /* redirected/absent console */ }

        if (Console.IsOutputRedirected) return;
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            var handle = GetStdHandle(StdOutputHandle);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return;
            if (!GetConsoleMode(handle, out var mode)) return;
            SetConsoleMode(handle, mode | EnableProcessedOutput | EnableVirtualTerminalProcessing);
        }
        catch
        {
            // terminal does not support VT - output stays plain
        }
    }

    /// <summary>Writes text that may contain ANSI escapes; strips them when stdout is redirected.</summary>
    public static void Write(string ansiText)
    {
        var text = Console.IsOutputRedirected ? StripAnsi(ansiText) : ansiText;
        Console.Out.Write(text);
        if (!text.EndsWith('\n') && !text.EndsWith('\r')) Console.Out.WriteLine();
        Console.Out.Flush();
    }

    [GeneratedRegex(@"\x1B(?:\[[0-9;?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|\x1B\\)|[@-Z\\-_])")]
    private static partial Regex AnsiRegex();

    public static string StripAnsi(string text) => AnsiRegex().Replace(text, "");
}