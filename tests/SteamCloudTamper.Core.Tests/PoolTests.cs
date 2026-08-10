using SteamCloudTamper.Core;
using SteamCloudTamper.Core.Pool;
using SteamCloudTamper.Core.Steam;

namespace SteamCloudTamper.Core.Tests;

public class BarcodeTests
{
    [Fact]
    public void TrailerRoundTripPreservesPayload()
    {
        var trailer = Barcode.PackTrailer("588650|1201110076|09082026");
        Assert.True(Barcode.TryDecode(trailer, out var payload));
        Assert.Equal("588650|1201110076|09082026", payload);

        var (game, uid, date) = Barcode.Parse(payload);
        Assert.Equal(588650u, game);
        Assert.Equal("1201110076", uid);
        Assert.Equal(new DateOnly(2026, 8, 9), date);
    }

    [Fact]
    public void TailDecodeFindsTrailerInWindow()
    {
        var data = new byte[8192];
        new Random(42).NextBytes(data);
        var trailer = Barcode.PackTrailer("480|123456789|01012026");
        var full = data.Concat(trailer).ToArray();

        var tail = full.AsSpan(full.Length - Barcode.TailWindowBytes);
        Assert.True(Barcode.TryDecodeTail(tail, out var payload, out var len));
        Assert.Equal("480|123456789|01012026", payload);
        Assert.Equal(trailer.Length, len);
    }

    [Fact]
    public void StripTrailerRestoresOriginalBytes()
    {
        var original = new byte[] { 1, 2, 3, 4, 5, 6, 7 };
        var trailer = Barcode.PackTrailer("588650|1201110076|09082026");
        var tagged = original.Concat(trailer).ToArray();

        var start = Math.Max(0, tagged.Length - Barcode.TailWindowBytes);
        var tail = tagged.AsSpan(start);
        Assert.True(Barcode.TryDecodeTail(tail, out _, out var len));
        var restored = Barcode.StripTrailer(tagged, len);
        Assert.Equal(original, restored);
    }

    [Fact]
    public void CrcDetectsCorruption()
    {
        var trailer = Barcode.PackTrailer("588650|1201110076|09082026");
        trailer[^1] ^= 0xFF; // flip a crc byte
        Assert.False(Barcode.TryDecode(trailer, out _));
    }

    [Fact]
    public void RenderBarcodeProducesFixedHeight()
    {
        var lines = Barcode.RenderBarcode("588650|1201110076|09082026", height: 21);
        Assert.Equal(21, lines.Count);
        Assert.All(lines, l => Assert.Contains(l, c => c == '█' || c == ' '));
    }
}

public class PoolDbTests
{
    [Fact]
    public void BlockedAppsAreNeverUsable()
    {
        Assert.False(PoolDb.Find(760)!.IsUsable); // SteamTools flood site stays dead
        Assert.True(PoolDb.Find(7)!.IsUsable);    // Steam client bucket re-candidate (native syncs observed)
        Assert.True(PoolDb.Find(480)!.IsUsable);
    }

    [Fact]
    public void OwnedReservedTierIsBelowCutoff()
    {
        Assert.Empty(PoolDb.Usable().Where(p => p.Tier == SlotTier.OwnedReserved));
    }
}

public class ParkingEngineTests
{
    [Fact]
    public void PicksHiddenSlotWhenAvailable()
    {
        var engine = new ParkingEngine([], []);
        var d = engine.Pick(91330, "save.sav", 1024);
        Assert.True(d.Ok);
        Assert.NotNull(d.StorageAppId);
        var picked = PoolDb.Find(d.StorageAppId.Value);
        Assert.NotNull(picked);
        Assert.Equal(SlotTier.HiddenDev, picked.Tier); // highest tier wins by design
        Assert.False(picked.IsBlocked);
        Assert.Equal("91330_save.sav", d.StoredName);
    }

    [Fact]
    public void NeverPicksOwnedGameSlot()
    {
        var engine = new ParkingEngine([480], []);
        var d = engine.Pick(91330, "save.sav", 1024);
        Assert.True(d.Ok);
        Assert.NotEqual(480u, d.StorageAppId);
    }

    [Fact]
    public void ReusesExistingParkingSlotForSameGame()
    {
        var slots = new List<GameSlot>
        {
            GameSlot.New(91330, 480, "91330_save.sav", "save.sav", 10, "91330|1|01012026"),
        };
        var engine = new ParkingEngine([], slots);
        var d = engine.Pick(91330, "save.sav", 1024);
        Assert.True(d.Ok);
        Assert.Equal(480u, d.StorageAppId);
        Assert.Contains("already parked", d.Reason);
    }

    [Fact]
    public void CoexistenceShiftsToOtherSlotOnNameCollision()
    {
        var slots = new List<GameSlot>
        {
            GameSlot.New(91330, 480, "91330_save.sav", "save.sav", 10, "91330|1|01012026"),
            GameSlot.New(4200, 480, "4200_other.sav", "other.sav", 10, "4200|1|01012026"),
        };
        var engine = new ParkingEngine([], slots);
        var d = engine.Pick(91330, "other.sav", 1024); // same stored name as 480's other game? no - 91330_other.sav
        Assert.True(d.Ok);
        Assert.Equal(480u, d.StorageAppId);
    }

    [Fact]
    public void PlanSpreadsFilesAcrossSlots()
    {
        var engine = new ParkingEngine([], []);
        var plan = engine.Plan(91330,
            [new ParkFile("a.sav", 100), new ParkFile("b.sav", 100), new ParkFile("c.sav", 100)],
            spread: 3);
        Assert.All(plan, d => Assert.True(d.Ok));
        var slots = plan.Select(d => d.StorageAppId).Distinct().ToList();
        Assert.Equal(3, slots.Count); // top-3 usable: 480, 113200, 250820
        Assert.Equal("91330_a.sav", plan[0].StoredName);
    }

    [Fact]
    public void PlanCopiesMirrorToDistinctNames()
    {
        var engine = new ParkingEngine([], []);
        var plan = engine.Plan(91330, [new ParkFile("save.sav", 100)], copies: 2);
        Assert.Equal(2, plan.Count);
        Assert.All(plan, d => Assert.True(d.Ok));
        Assert.NotEqual(plan[0].StoredName, plan[1].StoredName);
        Assert.Contains("c1", plan[0].StoredName!);
        Assert.Contains("c2", plan[1].StoredName!);
    }

    [Fact]
    public void PlanStealthHashesStoredNames()
    {
        var engine = new ParkingEngine([], []);
        var plan = engine.Plan(91330, [new ParkFile("save.sav", 100)], stealth: true);
        Assert.True(plan[0].Ok);
        Assert.StartsWith("k000164c2", plan[0].StoredName!); // k{91330:x8}
        Assert.EndsWith(".sav", plan[0].StoredName!);
        Assert.DoesNotContain("91330_save", plan[0].StoredName);
    }

    [Fact]
    public void ServerDeniedSlotIsExcludedFromPlanning()
    {
        var engine = new ParkingEngine([], [], poolProbes: new Dictionary<uint, string> { [480] = "Denied" });
        var d = engine.Pick(91330, "save.sav", 1024);
        Assert.True(d.Ok);
        Assert.NotEqual(480u, d.StorageAppId);
        Assert.Equal(7u, d.StorageAppId); // next best usable slot (Steam Client bucket re-candidate)
    }

    [Fact]
    public void ForceBucketPinsEveryFileToOneSlot()
    {
        var engine = new ParkingEngine([], []);
        var plan = engine.Plan(91330,
            [new ParkFile("a.sav", 100), new ParkFile("b.sav", 100)],
            spread: 3, forceBucket: 250820);
        Assert.All(plan, d => Assert.True(d.Ok));
        Assert.All(plan, d => Assert.Equal(250820u, d.StorageAppId));
    }

    [Fact]
    public void ForceBucketRefusedWhenBlockedInPool()
    {
        var engine = new ParkingEngine([], []);
        var plan = engine.Plan(91330, [new ParkFile("a.sav", 100)], forceBucket: 760);
        Assert.All(plan, d => Assert.False(d.Ok));
        Assert.Contains("blocked", plan[0].Reason);
    }

    [Fact]
    public void ForceBucketRefusedWhenServerDenied()
    {
        var engine = new ParkingEngine([], [], poolProbes: new Dictionary<uint, string> { [480] = "Denied" });
        var plan = engine.Plan(91330, [new ParkFile("a.sav", 100)], forceBucket: 480);
        Assert.All(plan, d => Assert.False(d.Ok));
        Assert.Contains("Denied", plan[0].Reason);
    }

    [Fact]
    public void ForceBucketRefusedWhenNotInPool()
    {
        var engine = new ParkingEngine([], []);
        var plan = engine.Plan(91330, [new ParkFile("a.sav", 100)], forceBucket: 91330);
        Assert.All(plan, d => Assert.False(d.Ok));
    }

    [Fact]
    public void PostureSurvivesRegistryRoundtrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sct_reg_{Guid.NewGuid():N}.json");
        try
        {
            var reg = new SctRegistry();
            reg.Upsert(GameSlot.New(588650, 480, "588650_user_0.dat", "user_0.dat", 10, "588650|1|01012026")
                .WithPosture("real"));
            reg.Save(path);
            var loaded = SctRegistry.Load(path);
            Assert.Equal("real", loaded.Slots[0].Posture);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class SteamLocatorTests
{
    [Fact]
    public void IsOstRedirectedDetectsLuaAddappid()
    {
        var steam = Path.Combine(Path.GetTempPath(), $"sct_steam_{Guid.NewGuid():N}");
        var luaDir = Path.Combine(steam, "config", "lua");
        Directory.CreateDirectory(luaDir);
        File.WriteAllText(Path.Combine(luaDir, "588650.lua"),
            "local a = addappid(480)\nlocal b = addappid(113200)\n");
        try
        {
            Assert.True(SteamLocator.IsOstRedirected(steam, 480));
            Assert.True(SteamLocator.IsOstRedirected(steam, 113200));
            Assert.False(SteamLocator.IsOstRedirected(steam, 7));
        }
        finally
        {
            Directory.Delete(steam, recursive: true);
        }
    }

    [Fact]
    public void SyncPostureIsRealWithoutOst()
    {
        var steam = Path.Combine(Path.GetTempPath(), $"sct_steam_{Guid.NewGuid():N}");
        Directory.CreateDirectory(steam);
        try
        {
            Assert.Equal("real", SteamLocator.SyncPosture(steam, 588650));
        }
        finally
        {
            Directory.Delete(steam, recursive: true);
        }
    }

    [Fact]
    public void IsCloudRedirectLoadedNeedsToml()
    {
        var steam = Path.Combine(Path.GetTempPath(), $"sct_steam_{Guid.NewGuid():N}");
        Directory.CreateDirectory(steam);
        try
        {
            Assert.False(SteamLocator.IsCloudRedirectLoaded(steam));
            File.WriteAllText(Path.Combine(steam, "opensteamtool.toml"), "[cloud]\nenabled = true\nlibrary = \"cloud_redirect.dll\"\n");
            Assert.False(SteamLocator.IsCloudRedirectLoaded(steam)); // dll missing in root
            File.WriteAllText(Path.Combine(steam, "cloud_redirect.dll"), "");
            Assert.True(SteamLocator.IsCloudRedirectLoaded(steam));
        }
        finally
        {
            Directory.Delete(steam, recursive: true);
        }
    }
}

public class RegistryTests
{
    [Fact]
    public void LoadSaveRoundtrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sct_reg_{Guid.NewGuid():N}.json");
        try
        {
            var reg = new SctRegistry();
            reg.Upsert(GameSlot.New(588650, 480, "588650_user_0.dat", "user_0.dat", 12345, "588650|1|09082026"));
            reg.Save(path);

            var loaded = SctRegistry.Load(path);
            Assert.Equal(SctRegistry.MagicHeader, loaded.Header);
            Assert.Single(loaded.Slots);
            Assert.Equal(588650u, loaded.Slots[0].GameAppId);
        }
        finally
        {
            File.Delete(path);
        }
    }
}