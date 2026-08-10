using Microsoft.Win32;
using SteamCloudTamper.Core.Vdf;

namespace SteamCloudTamper.Core.Steam;

public static class SteamLocator
{
    /// <summary>A Logged-on-steam.exe / steam.exe process is alive right now.</summary>
    public static bool IsRunning()
    {
        try { return System.Diagnostics.Process.GetProcessesByName("steam").Length > 0; }
        catch { return false; }
    }

    /// <summary>The account Steam is currently signed in as (reads config/loginusers.vdf ActiveUser).</summary>
    public static SteamAccount? GetActiveAccount(string steamPath)
    {
        var path = Path.Combine(steamPath, "config", "loginusers.vdf");
        if (!File.Exists(path)) return null;

        try
        {
            var root = VdfParser.ParseFile(path);
            // loginusers.vdf wraps everything under "users" { ... }
            var userNodes = root["users"]?.Children ?? root.Children;
            var users = userNodes
                .Where(c => ulong.TryParse(c.Key, out _))
                .Select(c => new
                {
                    SteamId64 = ulong.Parse(c.Key),
                    Active = c["ActiveUser"]?.Value == "1",
                    AutoLogin = c["AutoLogin"]?.Value == "1",
                    Timestamp = long.TryParse(c["Timestamp"]?.Value, out var ts) ? ts : 0L,
                    Persona = c["PersonaName"]?.Value,
                })
                .OrderByDescending(u => u.Active)
                .ThenByDescending(u => u.AutoLogin)
                .ThenByDescending(u => u.Timestamp)
                .ToList();

            var user = users.FirstOrDefault();
            if (user is null) return null;

            // no network credential needed - the running client owns this session
            var id3 = SteamAccount.Id3FromSteamId(user.SteamId64);
            if (!Directory.Exists(Path.Combine(steamPath, "userdata", id3.ToString()))) return null;
            return new SteamAccount(id3, user.SteamId64, user.Persona);
        }
        catch
        {
            return null;
        }
    }

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

    /// <summary>
    /// True when OpenSteamTool has hooked this AppID via a config/lua/*.lua
    /// "addappid" bundle (OST/SteamTools-style). Those buckets never touch Valve -
    /// they are CloudRedirect-local or plain registry entries.
    /// </summary>
    public static bool IsOstRedirected(string steamPath, uint appId)
    {
        var luaDir = Path.Combine(steamPath, "config", "lua");
        if (!Directory.Exists(luaDir)) return false;
        try
        {
            foreach (var lua in Directory.EnumerateFiles(luaDir, "*.lua"))
            {
                foreach (var line in File.ReadAllLines(lua))
                {
                    var t = line.Trim();
                    if (!t.Contains("addappid", StringComparison.OrdinalIgnoreCase)) continue;
                    // addappid(<appid>) or addappid <appid>
                    var m = System.Text.RegularExpressions.Regex.Match(
                        t, @"addappid\s*[\(\s]\s*([0-9]{3,})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (m.Success && uint.TryParse(m.Groups[1].Value, out var hooked) && hooked == appId)
                        return true;
                }
            }
        }
        catch
        {
            return false;
        }
        return false;
    }

    /// <summary>
    /// True when CloudRedirect is active in this Steam install (opensteamtool.toml
    /// [cloud] enabled and the DLL sits in the Steam root). Uploads for hooked apps
    /// then land in the CR provider (folder) instead of Valve.
    /// </summary>
    public static bool IsCloudRedirectLoaded(string steamPath)
    {
        var toml = Path.Combine(steamPath, "opensteamtool.toml");
        if (!File.Exists(toml)) return false;
        try
        {
            var txt = File.ReadAllText(toml);
            if (!txt.Contains("[cloud]", StringComparison.OrdinalIgnoreCase)) return false;
            if (txt.Contains("enabled = true", StringComparison.OrdinalIgnoreCase))
            {
                var dll = Path.Combine(steamPath, "cloud_redirect.dll");
                return File.Exists(dll) || Directory.Exists(Path.Combine(steamPath, "opensteamtool"));
            }
        }
        catch
        {
            return false;
        }
        return false;
    }

    /// <summary>
    /// Where an upload for this AppID actually lands right now:
    ///  "redirected" - OST lua addappid hook (never touches Valve)
    ///  "provider"   - CloudRedirect intercepting (folder provider on this machine)
    ///  "real"       - straight to Valve UFS
    /// </summary>
    public static string SyncPosture(string steamPath, uint appId)
    {
        if (IsOstRedirected(steamPath, appId)) return "redirected";
        if (IsCloudRedirectLoaded(steamPath)) return "provider";
        return "real";
    }
}