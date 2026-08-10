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

public sealed record ParkFile(string Name, long Size);

/// <summary>
/// Smart parking allocator. Rules (in priority order):
///  1. Tier: hidden/dev/tool apps &gt; old free apps; owned games are HARD-excluded (Tier 3 never picked).
///  2. Anti-ban hardening: slots with a server "Denied" verdict (private pool probe) are skipped;
///     names can be hashed (--stealth); files can spread across N slots (--spread) and
///     mirror to extra slots (--copies) so no single bucket pattern is detectable.
///  3. Co-existence: a bucket already hosting other parked games is preferred.
///  4. Name collision / quota pressure -&gt; next candidate, deterministic by score then appid.
/// </summary>
public sealed class ParkingEngine
{
    private readonly HashSet<uint> _owned;

    private readonly IReadOnlyList<GameSlot> _registry;

    private readonly IReadOnlyDictionary<uint, string> _poolProbes;

    private readonly Func<uint, Task<RemoteBucketSnapshot?>>? _remoteProbe;

    /// <summary><paramref name="remoteProbe"/> returns null when the lane is offline (offline guess mode).</summary>
    public ParkingEngine(
        HashSet<uint> owned,
        IReadOnlyList<GameSlot> registry,
        Func<uint, Task<RemoteBucketSnapshot?>>? remoteProbe = null,
        IReadOnlyDictionary<uint, string>? poolProbes = null)
    {
        _owned = owned;
        _registry = registry;
        _remoteProbe = remoteProbe;
        _poolProbes = poolProbes ?? new Dictionary<uint, string>();
    }

    public ParkingDecision Pick(uint gameAppId, string originalFileName, long sizeBytes)
        => Plan(gameAppId, [new ParkFile(originalFileName, sizeBytes)])[0];

    /// <summary>Plans parking for a whole bucket. Returns one decision per file (copies expands it).</summary>
    public List<ParkingDecision> Plan(
        uint gameAppId,
        IReadOnlyList<ParkFile> files,
        bool stealth = false,
        int spread = 1,
        int copies = 1)
    {
        if (_owned.Contains(gameAppId))
            throw new InvalidOperationException($"appid {gameAppId} is in the owned list - parking an owned game is refused (use Ferry for backups).");

        if (files.Count == 0) return [];
        spread = Math.Max(1, spread);
        copies = Math.Max(1, copies);

        var candidates = PoolDb.Usable()
            .Where(p => !IsServerDenied(p.AppId))
            .Select(p => (App: p, Score: PoolScoring.TierScore(p.Tier) + PoolScoring.AgeScore(p.ReleaseYear)))
            .OrderByDescending(c => c.Score).ThenBy(c => c.App.AppId)
            .ToList();

        var head = Math.Min(spread, candidates.Count);
        if (head == 0)
            return files.Select(f => (ParkingDecision)ParkingDecision.Fail(
                "pool exhausted - no usable parking slot (owned games are never used)")).ToList();

        // already-parked game -> keep its proven slot; never reallocate what works
        var parkedSlot = !stealth ? _registry.FirstOrDefault(s => s.GameAppId == gameAppId) : null;
        if (parkedSlot is not null && PoolDb.Find(parkedSlot.StorageAppId) is { IsUsable: true })
        {
            return files.SelectMany(f => Enumerable.Range(0, copies).Select(copy =>
                (ParkingDecision)new ParkingDecision(true, parkedSlot.StorageAppId,
                    BuildStoredName(gameAppId, f.Name, copy, copies, stealth),
                    $"already parked: {parkedSlot.StorageAppId}/{parkedSlot.StoredName} (updated in place)"))).ToList();
        }

        var decisions = new List<ParkingDecision>();
        for (var i = 0; i < files.Count; i++)
        {
            for (var copy = 0; copy < copies; copy++)
            {
                var slot = candidates[(i + copy) % head];
                decisions.Add(ChooseSlot(gameAppId, files[i], slot, stealth, copy, copies));
            }
        }
        return decisions;
    }

    private bool IsServerDenied(uint appId)
    {
        if (!_poolProbes.TryGetValue(appId, out var state)) return false;
        return state.Equals("Denied", StringComparison.OrdinalIgnoreCase)
            || state.Equals("Blocked", StringComparison.OrdinalIgnoreCase);
    }

    private ParkingDecision ChooseSlot(uint gameAppId, ParkFile file,
        (ParkingApp App, int Score) slot, bool stealth, int copy, int copies)
    {
        var (app, baseScore) = slot;
        if (!app.IsUsable)
            return ParkingDecision.Fail($"{app.AppId}: slot not usable (state {app.State})");

        var storedName = BuildStoredName(gameAppId, file.Name, copy, copies, stealth);

        // registry: uploading the same stored name again = in-place update, never a second copy
        if (!stealth && copy == 0)
        {
            var existing = _registry.FirstOrDefault(s =>
                s.GameAppId == gameAppId &&
                s.StorageAppId == app.AppId &&
                s.StoredName.Equals(storedName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return new ParkingDecision(true, app.AppId, storedName,
                    $"already parked: {app.AppId}/{storedName} (updated in place)");
            }
        }

        var occupiedByOther = _registry.Any(s => s.StorageAppId == app.AppId && s.GameAppId != gameAppId);
        var coexist = PoolScoring.CoexistScore(
            _registry.Count(s => s.StorageAppId == app.AppId && s.GameAppId != gameAppId),
            _registry.Count(s => s.StorageAppId == app.AppId));

        var regCollision = _registry.Any(s =>
            s.StorageAppId == app.AppId &&
            s.StoredName.Equals(storedName, StringComparison.OrdinalIgnoreCase));

        var remote = _remoteProbe is not null ? _remoteProbe(app.AppId).GetAwaiter().GetResult() : null;
        var remoteCollision = remote?.Files.Any(f =>
            f.FileName.Equals(storedName, StringComparison.OrdinalIgnoreCase)) ?? false;

        if (regCollision || remoteCollision)
            return ParkingDecision.Fail($"{app.AppId}: '{storedName}' name collision in bucket (check registry or remote first)");

        if (remote?.Quota is { } q && q.ExistingBytes + (ulong)file.Size > q.MaxBytes)
            return ParkingDecision.Fail($"{app.AppId}: quota {q.ExistingBytes}+{file.Size} over {q.MaxBytes}");

        var reason = copy > 0 && copies > 1
            ? $"mirror copy {copy + 1}/{copies} in {app.AppId} ({app.Name})"
            : occupiedByOther
                ? $"co-existence: bucket {app.AppId} already hosts other parked games"
                : $"free slot at {app.AppId} ({app.Name})";
        return new ParkingDecision(true, app.AppId, storedName,
            $"{reason} (score {baseScore + coexist})");
    }

    /// <summary>Stealth names look like native slot data (k{appid:x}{counter}{ext}); the barcode trailer still identifies the file.</summary>
    public static string BuildStoredName(uint gameAppId, string originalName, int copy, int copies, bool stealth = false)
    {
        var ext = Path.GetExtension(originalName);
        var stem = stealth
            ? $"k{gameAppId:x8}{copy:x2}"
            : Ferry.ParkName(gameAppId, Path.GetFileNameWithoutExtension(originalName));
        var suffix = copies > 1 ? $"c{copy + 1}" : "";
        return $"{stem}{suffix}{ext}";
    }
}