using SteamCloudTamper.Core.Pool;

namespace SteamCloudTamper.Core;

public sealed record SlotCheck(
    uint StorageAppId,
    string StoredName,
    int Score,
    bool OccupiedByOtherGames,
    string Reason);

public sealed record ParkingDecision(
    bool Ok,
    uint? StorageAppId,
    string? StoredName,
    string Reason,
    IReadOnlyList<SlotCheck>? Rejected = null)
{
    public static ParkingDecision Fail(string reason, IReadOnlyList<SlotCheck>? rejected = null)
        => new(false, null, null, reason, rejected);
}

public sealed record RemoteBucketSnapshot(uint AppId, List<CloudFileEntry> Files, Quota? Quota);

/// <summary>
/// Smart parking allocator. Rules (in priority order):
///  1. Tier: hidden/dev apps &gt; old free apps; owned games are HARD-excluded (Tier 3 never picked).
///  2. Co-existence: a bucket already hosting other parked games is preferred - multiple
///     saves may share one AppID.
///  3. Name collision / quota pressure -> next candidate, deterministic by score then appid.
/// </summary>
public sealed class ParkingEngine
{
    private readonly HashSet<uint> _owned;

    private readonly IReadOnlyList<GameSlot> _registry;

    /// <summary><paramref name="remoteProbe"/> returns null when the lane is offline (offline guess mode).</summary>
    public ParkingEngine(
        HashSet<uint> owned,
        IReadOnlyList<GameSlot> registry,
        Func<uint, Task<RemoteBucketSnapshot?>>? remoteProbe = null)
    {
        _owned = owned;
        _registry = registry;
        _remoteProbe = remoteProbe;
    }

    private readonly Func<uint, Task<RemoteBucketSnapshot?>>? _remoteProbe;

    public ParkingDecision Pick(uint gameAppId, string originalFileName, long sizeBytes)
    {
        if (_owned.Contains(gameAppId))
            throw new InvalidOperationException($"appid {gameAppId} is in the owned list - parking an owned game is refused (use Ferry for backups).");

        var candidates = PoolDb.Usable()
            .Select(p => (App: p, Score: PoolScoring.TierScore(p.Tier) + PoolScoring.AgeScore(p.ReleaseYear)))
            .ToList();
        // registry: a game already parked -> reuse the same slot if it still allows co-existence
        var alreadyParked = _registry.FirstOrDefault(s => s.GameAppId == gameAppId);
        if (alreadyParked is not null)
        {
            var stale = PoolDb.Find(alreadyParked.StorageAppId);
            if (stale is { IsUsable: true })
            {
                return new ParkingDecision(true, alreadyParked.StorageAppId, alreadyParked.StoredName,
                    $"already parked: {alreadyParked.StorageAppId}/{alreadyParked.StoredName}");
            }
        }

        var rejected = new List<SlotCheck>();
        foreach (var (app, baseScore) in candidates.OrderByDescending(c => c.Score).ThenBy(c => c.App.AppId))
        {
            var storedName = Ferry.ParkName(gameAppId, originalFileName);
            var occupiedByOther = _registry.Any(s => s.StorageAppId == app.AppId && s.GameAppId != gameAppId);
            var coexist = PoolScoring.CoexistScore(
                _registry.Count(s => s.StorageAppId == app.AppId && s.GameAppId != gameAppId),
                _registry.Count(s => s.StorageAppId == app.AppId));

            // collision against the registry
            var regCollision = _registry.Any(s =>
                s.StorageAppId == app.AppId &&
                s.StoredName.Equals(storedName, StringComparison.OrdinalIgnoreCase));

            var remote = _remoteProbe is not null ? _remoteProbe(app.AppId).GetAwaiter().GetResult() : null;
            var remoteCollision = remote?.Files.Any(f =>
                f.FileName.Equals(storedName, StringComparison.OrdinalIgnoreCase)) ?? false;

            if (regCollision || remoteCollision)
            {
                rejected.Add(new SlotCheck(app.AppId, storedName, baseScore, occupiedByOther,
                    "name collision in bucket; next candidate"));
                continue;
            }

            if (remote?.Quota is { } q && q.ExistingBytes + (ulong)sizeBytes > q.MaxBytes)
            {
                rejected.Add(new SlotCheck(app.AppId, storedName, baseScore, occupiedByOther,
                    $"quota {q.ExistingBytes}+{sizeBytes} over {q.MaxBytes}; next candidate"));
                continue;
            }

            var reason = occupiedByOther
                ? $"co-existence: bucket {app.AppId} already hosts other parked games"
                : $"free slot at {app.AppId} ({app.Name})";
            return new ParkingDecision(true, app.AppId, storedName,
                $"{reason} (score {baseScore + coexist})", rejected);
        }

        return ParkingDecision.Fail("pool exhausted - no usable parking slot (owned games are never used)", rejected);
    }
}