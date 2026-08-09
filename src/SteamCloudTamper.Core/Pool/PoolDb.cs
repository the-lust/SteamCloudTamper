namespace SteamCloudTamper.Core.Pool;

public enum SlotTier
{
    /// <summary>Hidden / dev / test apps - every account is entitled, lowest visibility.</summary>
    HiddenDev = 1,
    /// <summary>Old, unpopular, free apps - still entitled, more visible than Tier 1.</summary>
    OldFree = 2,
    /// <summary>Would be owned-game buckets - NEVER selected unless explicitly whitelisted by the user.</summary>
    OwnedReserved = 3,
}

public enum SlotState
{
    Candidate,
    Blocked,
    VerifiedWritable,
}

public sealed record ParkingApp(
    uint AppId,
    string Name,
    int ReleaseYear,
    bool IsFree,
    SlotTier Tier,
    SlotState State,
    string Note)
{
    public bool IsBlocked => State == SlotState.Blocked;

    public bool IsUsable => State is SlotState.Candidate or SlotState.VerifiedWritable;
}

/// <summary>
/// Curated parking pool: the only places SCT will store "foreign" save copies.
/// Every candidate must be an AppID the account is entitled to regardless of library
/// (hidden/dev apps, free games). 7/760 are enforced-blocked by Valve's patch (Apr 2025).
/// CloudEnabled is not published via the store API - candidates get verified
/// by live probe (upload+delete OK) before the registry marks them VerifiedWritable.
/// </summary>
public static class PoolDb
{
    public static readonly IReadOnlyList<ParkingApp> DefaultPool = Build();

    private static List<ParkingApp> Build() =>
    [
        // ---- Tier 1: hidden / dev / test apps --------------------------------
        new(480, "Spacewar", 2013, true, SlotTier.HiddenDev, SlotState.VerifiedWritable,
            "Steam's hidden test game. Every account is entitled; bucket verified writable by SCT."),
        new(113200, "Cloud test app", 2011, true, SlotTier.HiddenDev, SlotState.Candidate,
            "Steam cloud test app (empty bucket observed on real installs). Probe before use."),
        new(250820, "SteamVR", 2016, true, SlotTier.HiddenDev, SlotState.Candidate,
            "Hidden app; VR settings sync via cloud. Probe before use."),
        new(413080, "SteamVR Home", 2016, true, SlotTier.HiddenDev, SlotState.Candidate,
            "Hidden app; probe before use."),
        new(7, "Steam Client", 2003, true, SlotTier.HiddenDev, SlotState.Blocked,
            "INTERNAL - server refuses all UFS operations since 2025 patch."),
        new(760, "Screenshots", 2008, true, SlotTier.HiddenDev, SlotState.Blocked,
            "INTERNAL - the SteamTools 760 dump site. Server refuses UFS ops."),

        // ---- Tier 2: old, free, widely-entitled games -------------------------
        new(230410, "Warframe", 2013, true, SlotTier.OldFree, SlotState.Candidate,
            "F2P, cloud-synced. Probe before use."),
        new(359550, "Rainbow Six Siege", 2015, true, SlotTier.OldFree, SlotState.Candidate,
            "F2P since 2025?; probe before use."),
        new(570, "Dota 2", 2013, true, SlotTier.OldFree, SlotState.Candidate,
            "F2P, cloud-synced; high visibility, crowded bucket."),
        new(440, "Team Fortress 2", 2007, true, SlotTier.OldFree, SlotState.Candidate,
            "F2P, cloud-synced; ancient, crowded bucket."),
        new(730, "Counter-Strike 2", 2023, true, SlotTier.OldFree, SlotState.Candidate,
            "F2P successor of CS:GO (2012, old codebase)."),
        new(322330, "Don't Starve Together", 2016, false, SlotTier.OwnedReserved, SlotState.Blocked,
            "PAID - may only be used if genuinely owned; excluded by default."),
    ];

    public static IEnumerable<ParkingApp> Usable(SlotTier maxTier = SlotTier.OldFree)
        => DefaultPool.Where(p => p.IsUsable && p.Tier <= maxTier);

    public static ParkingApp? Find(uint appId) => DefaultPool.FirstOrDefault(p => p.AppId == appId);
}

public static class PoolScoring
{
    /// <summary>Tier base score; higher = preferred.</summary>
    public static int TierScore(SlotTier tier) => tier switch
    {
        SlotTier.HiddenDev => 100,
        SlotTier.OldFree => 60,
        SlotTier.OwnedReserved => 0,
        _ => 10,
    };

    /// <summary>Older release year wins (old saves are more "dead" = safer co-tenants).</summary>
    public static int AgeScore(int releaseYear) => Math.Clamp(2026 - releaseYear, 0, 50);

    /// <summary>Co-existence bonus: bucket already hosting other parked games = proven slot.</summary>
    public static int CoexistScore(int distinctHostedGames, int fileCount) =>
        distinctHostedGames > 0 ? 20 + Math.Min(fileCount, 25) : 0;
}