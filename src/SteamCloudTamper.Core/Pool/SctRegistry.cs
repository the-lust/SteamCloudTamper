using System.Text.Json;

namespace SteamCloudTamper.Core.Pool;

public sealed record GameSlot(
    uint GameAppId,
    uint StorageAppId,
    string StoredName,
    string OriginalName,
    long Size,
    DateTime ParkedAt,
    string? BarcodePayload,
    string Status)
{
    public static GameSlot New(uint game, uint storage, string stored, string original, long size, string barcode)
        => new(game, storage, stored, original, size, DateTime.UtcNow, barcode, "parked");
}

/// <summary>
/// The single shared truth: which game's save lives where.
/// Read/written by SCT CLI/TUI, SteamCloudSave.dll, and emulator lanes.
/// </summary>
public sealed class SctRegistry
{
    public const string MagicHeader = "SCTREG1";

    public string Header { get; set; } = MagicHeader;

    public int Version { get; set; } = 1;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<GameSlot> Slots { get; set; } = [];

    public static string DefaultPath()
    {
        var env = Environment.GetEnvironmentVariable("SCT_REGISTRY");
        if (!string.IsNullOrEmpty(env)) return env;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "SCT", "registry.json");
    }

    public static SctRegistry Load(string? path = null)
    {
        var p = path ?? DefaultPath();
        if (!File.Exists(p)) return new SctRegistry();
        try
        {
            var reg = JsonSerializer.Deserialize<SctRegistry>(File.ReadAllText(p), JsonOpts);
            if (reg is not null && reg.Header == MagicHeader) return reg;
        }
        catch
        {
            // corrupt registry - start clean, scanner rebuilds it
        }
        return new SctRegistry();
    }

    public void Save(string? path = null)
    {
        var p = path ?? DefaultPath();
        var dir = Path.GetDirectoryName(p);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        UpdatedAt = DateTime.UtcNow;
        File.WriteAllText(p, JsonSerializer.Serialize(this, JsonOpts));
    }

    public GameSlot? FindByGame(uint gameAppId) => Slots.FirstOrDefault(s => s.GameAppId == gameAppId);

    public GameSlot? FindByStoredName(string storedName)
        => Slots.FirstOrDefault(s => s.StoredName.Equals(storedName, StringComparison.OrdinalIgnoreCase));

    public void Upsert(GameSlot slot)
    {
        var idx = Slots.FindIndex(s => s.StoredName.Equals(slot.StoredName, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) Slots[idx] = slot; else Slots.Add(slot);
    }

    public void Remove(string storedName)
        => Slots.RemoveAll(s => s.StoredName.Equals(storedName, StringComparison.OrdinalIgnoreCase));

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
}