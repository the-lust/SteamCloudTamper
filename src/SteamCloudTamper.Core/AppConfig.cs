using System.Text;
using System.Text.Json;

namespace SteamCloudTamper.Core;

public sealed class AppConfig
{
    public string? SteamPathOverride { get; set; }

    public bool DryRun { get; set; } = true;

    public HashSet<uint> GuardedAppIds { get; set; } = [];

    public Dictionary<string, string> Hints { get; set; } = [];

    public List<uint> KnownOwnedAppIds { get; set; } = [];

    /// <summary>
    /// AppID proxy map (Ace SLS style): game appid -> proxy appid. Key 0 = the
    /// default proxy for ANY unowned game. e.g. { 588650: 480 } parks Dead Cells
    /// inside your Spacewar bucket under "sls-588650/..." paths.
    /// </summary>
    public Dictionary<uint, uint> CloudProxies { get; set; } = [];

    /// <summary>Resolves the proxy for a game: per-game entry, else the 0 default. 0 = none.</summary>
    public uint ResolveProxy(uint gameAppId)
    {
        if (CloudProxies.TryGetValue(gameAppId, out var p)) return p;
        if (CloudProxies.TryGetValue(0, out var d)) return d;
        return 0;
    }

    public string? CookieFile { get; set; }

    public static AppConfig Load(string path)
    {
        if (!File.Exists(path)) return new AppConfig();
        try
        {
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOpts) ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }

    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(this, JsonOpts);
        File.WriteAllText(path, json);
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public HashSet<uint> GetOwnedSet() => new(KnownOwnedAppIds);
}

public static class BlankSaveKit
{
    private static readonly byte[] JsonBlank = """{"sct_blank":true,"created_by":"SteamCloudTamper"}"""u8.ToArray();

    public static byte[] CreateBlank(string fileName, byte[]? template = null)
    {
        if (template is not null) return template;

        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".json" => JsonBlank,
            ".txt" or ".log" or ".cfg" or ".ini" or ".xml" or ".dat" or ".sav" => JsonBlank.AsSpan().ToArray(),
            _ => [0x00, 0x00, 0x00, 0x00],
        };
    }
}