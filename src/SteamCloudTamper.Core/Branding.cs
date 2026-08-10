namespace SteamCloudTamper.Core;

public static class Branding
{
    public const string BrandFileName = "BRAND.txt";

    public static string? LocateBrandFile(string? startDir = null)
    {
        var dirs = new[]
        {
            startDir ?? Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,
        };

        foreach (var dir in dirs)
        {
            var probe = dir;
            for (var i = 0; i < 6 && !string.IsNullOrEmpty(probe); i++)
            {
                var candidate = Path.Combine(probe, BrandFileName);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
                probe = Path.GetDirectoryName(probe);
            }
        }

        return null;
    }

    public static string RenderRawBrand(string? startDir = null)
    {
        var path = LocateBrandFile(startDir);
        if (path is null)
        {
            return string.Empty;
        }

        return File.ReadAllText(path);
    }

    public static void PrintToConsole(string? startDir = null)
    {
        var brand = RenderRawBrand(startDir);
        if (brand.Length == 0)
        {
            return;
        }

        // raw ANSI passthrough (VT enabled by the caller); stripped automatically when piped
        Console.Out.Write(Console.IsOutputRedirected ? AnsiTerminal.StripAnsi(brand) : brand);
        if (!brand.EndsWith(Environment.NewLine, StringComparison.Ordinal) && !brand.EndsWith('\n'))
        {
            Console.Out.WriteLine();
        }
        Console.Out.WriteLine();
    }
}