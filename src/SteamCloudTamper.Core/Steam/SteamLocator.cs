using Microsoft.Win32;
using SteamCloudTamper.Core.Vdf;

namespace SteamCloudTamper.Core.Steam;

public static class SteamLocator
{
    public static string? DetectInstallPath()
    {
        var candidates = new[]
        {
            Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null),
            Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath", null),
            Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null),
        };

        foreach (var c in candidates)
        {
            if (c is string s && Directory.Exists(s)) return s;
        }

        var guessed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        return Directory.Exists(guessed) ? guessed : null;
    }

    public static List<string> ListLibraries(string steamPath)
    {
        var libs = new List<string> { Path.Combine(steamPath, "steamapps") };
        var path = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(path)) return libs;

        var root = VdfParser.ParseFile(path);
        foreach (var child in root.Children.OrderBy(c => int.TryParse(c.Key, out _) ? int.Parse(c.Key) : int.MaxValue))
        {
            var value = child["path"]?.Value;
            if (value is not null) libs.Add(Path.Combine(value, "steamapps"));
        }

        return libs;
    }

    public static List<SteamAccount> ListAccounts(string steamPath)
    {
        var accounts = new List<SteamAccount>();
        var userdata = Path.Combine(steamPath, "userdata");
        if (!Directory.Exists(userdata)) return accounts;

        foreach (var dir in Directory.EnumerateDirectories(userdata))
        {
            var name = Path.GetFileName(dir);
            if (!uint.TryParse(name, out var id3)) continue;

            var display = ReadAccountName(steamPath, id3);
            accounts.Add(new SteamAccount(id3, SteamAccount.SteamIdFor(id3), display));
        }

        return accounts;
    }

    private static string? ReadAccountName(string steamPath, uint uid)
    {
        var cfg = Path.Combine(steamPath, "userdata", uid.ToString(), "config", "localconfig.vdf");
        if (!File.Exists(cfg)) return null;
        try
        {
            var root = VdfParser.ParseFile(cfg);
            return root["UserLocalConfigStore"]?["friends"]?["PersonaName"]?.Value;
        }
        catch
        {
            return null;
        }
    }

    public static List<LocalAppState> ScanLocalApps(string steamPath, uint? accountId, IReadOnlySet<uint>? owned = null)
    {
        var results = new List<LocalAppState>();
        var userdata = Path.Combine(steamPath, "userdata");
        if (!Directory.Exists(userdata)) return results;

        foreach (var userDir in Directory.EnumerateDirectories(userdata))
        {
            var dirName = Path.GetFileName(userDir);
            if (!uint.TryParse(dirName, out var uid3)) continue;
            if (accountId is not null && uid3 != accountId) continue;

            foreach (var appDir in Directory.EnumerateDirectories(userDir))
            {
                var name = Path.GetFileName(appDir);
                if (!uint.TryParse(name, out var appId)) continue;

                var remote = Path.Combine(appDir, "remote");
                var rc = Path.Combine(appDir, "remotecache.vdf");
                long bytes = 0;
                var count = 0;
                if (Directory.Exists(remote))
                {
                    foreach (var f in Directory.EnumerateFiles(remote))
                    {
                        var fn = Path.GetFileName(f);
                        if (fn.Equals("remotecache.vdf", StringComparison.OrdinalIgnoreCase)) continue;
                        try { bytes += new FileInfo(f).Length; } catch { }
                        count++;
                    }
                }

                if (count == 0 && !File.Exists(rc)) continue;

                results.Add(new LocalAppState(
                    appId,
                    appDir,
                    File.Exists(rc) ? rc : null,
                    Directory.Exists(remote) ? remote : null,
                    bytes,
                    count,
                    uid3));
            }
        }

        return results;
    }
}