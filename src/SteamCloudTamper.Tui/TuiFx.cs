using Spectre.Console;

namespace SteamCloudTamper.Tui;

/// <summary>
/// Claude-style display effects: gradients, glow pulses, sine waves.
/// Everything degrades gracefully - SCT_TUI_FLAT=1 kills all of it, and every
/// animation is a plain overwrite-in-place so redirects/odd terminals can't break.
/// </summary>
public static class TuiFx
{
    public static readonly bool Flat = Environment.GetEnvironmentVariable("SCT_TUI_FLAT") == "1";

    /// <summary>Claude amber-ish accent.</summary>
    public static readonly Color Amber = new(0xE8, 0x9C, 0x1C);
    /// <summary>Claude lilac-ish accent.</summary>
    public static readonly Color Lilac = new(0xC8, 0x8B, 0xE0);
    /// <summary>Steam teal accent (data/secondary).</summary>
    public static readonly Color Teal = new(0x3D, 0xB8, 0xE8);
    /// <summary>Deep background-ish purple for depth lines.</summary>
    public static readonly Color Deep = new(0x5B, 0x3E, 0x8E);

    private static readonly string[] Blocks = ["▁", "▂", "▃", "▄", "▅", "▆", "▇", "█", "▇", "▆", "▅", "▄", "▃", "▂"];

    /// <summary>Lerp color between two endpoints; hex markup per character (no palette limits).</summary>
    public static string Gradient(string text, Color from, Color to)
    {
        if (Flat) return Markup.Escape(text);
        var sb = new System.Text.StringBuilder(text.Length * 16);
        var n = Math.Max(1, text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var t = n <= 1 ? 1d : (double)i / (n - 1);
            var (r, g, b) = Lerp(from, to, t);
            sb.Append($"[#{r:x2}{g:x2}{b:x2}]{EscapeChar(text[i])}[/]");
        }
        return sb.ToString();
    }

    /// <summary>Brand gradient (amber -&gt; lilac) for headings.</summary>
    public static string Brand(string text) => Gradient(text, Amber, Lilac);

    /// <summary>Data gradient (teal -&gt; amber) for values.</summary>
    public static string Data(string text) => Gradient(text, Teal, Amber);

    /// <summary>
    /// "Glow": per-character hue wave over the string, phase advances with time.
    /// Recomputed per render = alive without a background animator.
    /// </summary>
    public static string Glow(string text)
    {
        if (Flat) return Markup.Escape(text);
        var phase = (DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond) / 400d;
        var sb = new System.Text.StringBuilder(text.Length * 16);
        for (var i = 0; i < text.Length; i++)
        {
            var t = 0.5 + 0.5 * Math.Sin((2 * Math.PI * i / Math.Max(1, text.Length)) + phase);
            var (r, g, b) = Lerp(Amber, Lilac, t);
            sb.Append($"[#{r:x2}{g:x2}{b:x2}]{EscapeChar(text[i])}[/]");
        }
        return sb.ToString();
    }

    /// <summary>Selection-prompt / table title with the brand gradient.</summary>
    public static string Title(string text) => $"[bold]{Brand(text)}[/]";

    /// <summary>Fancy rule (footer/header separator). Renders as a single line - safe everywhere.</summary>
    public static void Rule(string hint)
    {
        if (Flat) { AnsiConsole.WriteLine(hint); return; }
        AnsiConsole.Write(new Rule($"[dim]{Markup.Escape(hint)}[/]").RuleStyle(new Style(foreground: Deep)));
    }

    /// <summary>One sine-wave bar line, gradient from amber to lilac, phase from time (live look).</summary>
    public static string SineLine(int width = 36)
    {
        if (Flat) return "";
        var phase = (DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond) / 150d;
        var sb = new System.Text.StringBuilder(width * 12);
        for (var i = 0; i < width; i++)
        {
            var wave = Math.Sin((i / (double)width) * Math.PI * 2 - phase) * 0.5 + 0.5; // 0..1
            var ch = Blocks[(int)(wave * (Blocks.Length - 1))];
            var (r, g, b) = Lerp(Amber, Lilac, wave);
            sb.Append($"[#{r:x2}{g:x2}{b:x2}]{ch}[/]");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Boot splash: gradient wordmark + animated sine bar + tagline, then it's gone.
    /// Overwrites in place (\r), never scrolls mid-animation; skipped when redirected.
    /// </summary>
    public static void Splash()
    {
        if (Flat || Console.IsOutputRedirected || Console.IsInputRedirected) return;
        try
        {
            AnsiConsole.Write(new FigletText("STEAM CLOUD SAVER").Color(Amber).Justify(Justify.Center));
            Reveal();
        }
        catch
        {
            // effects are purely cosmetic - never let them take the app down
            Console.WriteLine();
            Console.WriteLine();
        }
    }

    /// <summary>Post-brand reveal: animated sine bar + gradient tagline, then it's gone.</summary>
    public static void Reveal()
    {
        if (Flat || Console.IsOutputRedirected || Console.IsInputRedirected) return;
        try
        {
            var width = Math.Min(48, Console.WindowWidth - 2);
            var bar = new System.Text.StringBuilder(width * 12 + 8);
            var frames = 14;
            for (var f = 0; f < frames; f++)
            {
                bar.Clear();
                var phase = f / (double)frames * Math.PI * 2;
                for (var i = 0; i < width; i++)
                {
                    var wave = Math.Sin((i / (double)width) * Math.PI * 2 - phase) * 0.5 + 0.5;
                    var ch = Blocks[(int)(wave * (Blocks.Length - 1))];
                    var (r, g, b) = Lerp(Amber, Lilac, wave);
                    bar.Append($"[#{r:x2}{g:x2}{b:x2}]{ch}[/]");
                }
                AnsiConsole.Write(new Markup(bar.ToString()));
                AnsiConsole.Write(new Markup("\r"));
                Thread.Sleep(70);
            }
            Console.WriteLine();
            AnsiConsole.Write(new Markup(Gradient("park · tag · ferry · survive — the unowned-cloud save locker", Teal, Amber)));
            Console.WriteLine();
            Console.WriteLine();
        }
        catch
        {
            // effects are purely cosmetic - never let them take the app down
            Console.WriteLine();
            Console.WriteLine();
        }
    }

    private static string EscapeChar(char c) =>
        c switch
        {
            '[' => @"[[]",
            ']' => @"[]]",
            _ => c.ToString(),
        };

    private static (int R, int G, int B) Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return (
            (int)Math.Round(a.R + (b.R - a.R) * t),
            (int)Math.Round(a.G + (b.G - a.G) * t),
            (int)Math.Round(a.B + (b.B - a.B) * t));
    }
}