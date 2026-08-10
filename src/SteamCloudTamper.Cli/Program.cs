using SteamCloudTamper.Core;
using SteamCloudTamper.Core.Pool;
using SteamCloudTamper.Core.Steam;
using SteamCloudTamper.Engines;

namespace SteamCloudTamper.Cli;

public static class Program
{
    private const string ConfigPath = "steamcloudtamper.json";

    public static async Task<int> Main(string[] args)
    {
        AnsiTerminal.Enable();

        Branding.PrintToConsole();

        var config = AppConfig.Load(ConfigPath);
        var cmd = args.Length == 0 ? "help" : args[0].ToLowerInvariant();

        try
        {
            return cmd switch
            {
                "detect" => Detect(config),
                "scan" => Scan(config),
                "remote-list" => await RemoteList(args, config),
                "probe" => await Probe(args, config),
                "wipe" => await Wipe(args, config),
                "wipe-all" => await WipeAll(args, config),
                "guards" => Guards(args, config),
                "inject" => InjectLocal(args, config),
                "lock" => LockBucket(args, config),
                "relocate" => RelocateBucket(args, config),
                "web" => await WebCloud(args, config),
                "ferry" => await FerryCmd(args, config),
                "pool" => await PoolCmd(args, config),
                "park" => await ParkCmd(args, config),
                "unpark" => await UnparkCmd(args, config),
                "rebuild" => RebuildCmd(args, config),
                "barcode" => BarcodeCmd(args, config),
                _ => Help()
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex.Message}");
            return 1;
        }
    }

    private static string ResolveSteam(AppConfig config)
        => config.SteamPathOverride ?? SteamLocator.DetectInstallPath()
           ?? throw new InvalidOperationException("Steam install not found. Configure SteamPathOverride or pass --steam.");

    private static int Detect(AppConfig config)
    {
        var path = SteamLocator.DetectInstallPath();
        Console.WriteLine($"Steam path: {path ?? "(not found)"}");
        Console.WriteLine($"Override  : {config.SteamPathOverride ?? "(none)"}");
        if (path is null) return 1;

        Console.WriteLine($"Libraries : {string.Join("; ", SteamLocator.ListLibraries(path))}");
        foreach (var a in SteamLocator.ListAccounts(path))
            Console.WriteLine($"Account   : {a.AccountId} ({a.DisplayName ?? "?"})");
        return 0;
    }

    private static int Scan(AppConfig config)
    {
        var steam = ResolveSteam(config);
        var audit = new AuditEngine(config);
        var buckets = audit.ListLocal(steam, owned: config.GetOwnedSet());

        Console.WriteLine($"{buckets.Count} local bucket(s).");
        foreach (var b in buckets)
        {
            Console.WriteLine($"  [{b.AppId}] {b.Era,-20} {b.Files.Count} files, {b.TotalBytes}b  {b.Note}");
            foreach (var f in b.Files)
            {
                var tag = "";
                var filePath = Path.Combine(steam, "userdata", "?", b.AppId.ToString());
                // best-effort tag check against every account dir
                tag = QuickTag(steam, b.AppId, f.FileName);
                Console.WriteLine($"      {f.FileName} ({f.FileSize}b){tag}");
            }
        }

        return 0;
    }

    private static string QuickTag(string steam, uint appId, string fileName)
    {
        var userdata = Path.Combine(steam, "userdata");
        if (!Directory.Exists(userdata)) return "";
        foreach (var account in Directory.EnumerateDirectories(userdata))
        {
            var path = Path.Combine(account, appId.ToString(), fileName);
            if (!File.Exists(path)) continue;
            var info = new FileInfo(path);
            if (info.Length < Barcode.TailWindowBytes) continue;
            var tail = ReadTail(path, Math.Min(info.Length, Barcode.TailWindowBytes));
            if (Barcode.TryDecodeTail(tail, out var payload, out _))
                return $"  tag={payload}";
        }
        return "";
    }

    private static async Task<int> RemoteList(string[] args, AppConfig config)
    {
        var appFilter = Arg(args, "--app");
        if (appFilter is null)
        {
            Console.WriteLine("usage: remote-list --app <appid>");
            return 1;
        }

        var appId = uint.Parse(appFilter);
        await using var s = await ConnectSessionAsync();
        var rpc = new CloudRpcClient(s);

        try
        {
            var files = await rpc.EnumerateAsync(appId);
            Console.WriteLine($"{appId}: {files.Count} file(s):");
            foreach (var f in files)
            {
                var sha = f.FileSha is { Length: > 0 } fsha ? fsha.Length > 16 ? fsha[..16] + "..." : fsha : "-";
                Console.WriteLine($"  {f.FileName}  {f.FileSize}b  ts={f.Timestamp}  sha={sha}");
            }

            return 0;
        }
        catch (CloudRpcException ex)
        {
            Console.Error.WriteLine($"FAILED: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> Probe(string[] args, AppConfig config)
    {
        var appIds = args.Skip(1).Where(a => uint.TryParse(a, out _)).Select(uint.Parse).ToArray();
        if (appIds.Length == 0)
        {
            Console.WriteLine("usage: probe <appid...>");
            return 1;
        }

        await using var session = await ConnectSessionAsync();
        var rpc = new CloudRpcClient(session);
        foreach (var appId in appIds)
        {
            var v = await rpc.ProbeAsync(appId);
            Console.WriteLine($"{appId}: enumerate={v.Enumerate,-12} upload={v.Upload,-12} delete={v.Delete,-12} | {v.Detail}");
        }

        return 0;
    }

    private static async Task<int> Wipe(string[] args, AppConfig config)
    {
        if (args.Length < 3 || !uint.TryParse(args[1], out var appId))
        {
            Console.WriteLine("usage: wipe <appid> <filename> [--blank] [--force]");
            return 1;
        }

        var file = args[2];
        var blank = Has(args, "--blank");
        var dryRun = !Has(args, "--force") && config.DryRun;
        if (dryRun) Console.WriteLine("DRY-RUN (--force to execute)");
        config.DryRun = dryRun;

        await using var session = await ConnectSessionAsync();
        var rpc = new CloudRpcClient(session);
        var engine = new WipeEngine(config);
        var outcome = await engine.WipeAsync(rpc, appId, file, blank);

        Console.WriteLine($"{(outcome.Success ? "OK  " : "FAIL")} [{outcome.Action}] {outcome.AppId}/{outcome.FileName}: {outcome.Result}");
        return outcome.Success ? 0 : 1;
    }

    private static async Task<int> WipeAll(string[] args, AppConfig config)
    {
        if (args.Length < 2 || !uint.TryParse(args[1], out var appId))
        {
            Console.WriteLine("usage: wipe-all <appid> [--blank] [--force]");
            return 1;
        }

        var blank = Has(args, "--blank");
        var dryRun = !Has(args, "--force") && config.DryRun;
        if (dryRun) Console.WriteLine("DRY-RUN (--force to execute)");
        config.DryRun = dryRun;

await using var session = await ConnectSessionAsync();
        var rpc = new CloudRpcClient(session);
        var files = await rpc.EnumerateAsync(appId);
        if (files.Count == 0)
        {
            Console.WriteLine("no files in bucket");
            return 0;
        }

        Console.WriteLine($"{files.Count} files in bucket {appId}");
        var engine = new WipeEngine(config);
        var ok = 0;
        foreach (var f in files)
        {
            var o = await engine.WipeAsync(rpc, appId, f.FileName, blank);
            if (o.Success) ok++;
            Console.WriteLine($"  {(o.Success ? "OK " : "ERR")} {f.FileName}");
        }

        Console.WriteLine($"{ok}/{files.Count} succeeded");
        return ok == files.Count ? 0 : 1;
    }

    private static int Guards(string[] args, AppConfig config)
    {
        var sub = args.Length > 1 ? args[1].ToLowerInvariant() : "ls";
        switch (sub)
        {
            case "add" when args.Length > 2 && uint.TryParse(args[2], out var id):
                config.GuardedAppIds.Add(id);
                config.Save(ConfigPath);
                Console.WriteLine($"guarded {id}");
                break;
            case "rm" when args.Length > 2 && uint.TryParse(args[2], out var id):
                config.GuardedAppIds.Remove(id);
                config.Save(ConfigPath);
                Console.WriteLine($"unguarded {id}");
                break;
            case "ls":
                foreach (var g in config.GuardedAppIds.OrderBy(x => x)) Console.WriteLine(g);
                break;
            default:
                Console.WriteLine("usage: guards add|rm|ls <appid>");
                return 1;
        }

        return 0;
    }

    private static int InjectLocal(string[] args, AppConfig config)
    {
        if (args.Length < 4 || !uint.TryParse(args[1], out var uid) || !uint.TryParse(args[2], out var appId))
        {
            Console.WriteLine("usage: inject <uid3> <appid> <file> [remote-name]");
            return 1;
        }

        var src = args[3];
        var name = args.Length > 4 ? args[4] : null;
        var appDir = Path.Combine(ResolveSteam(config), "userdata", uid.ToString(), appId.ToString());

        var engine = new LocalInjectEngine();
        var dest = engine.InjectFile(appDir, src, name);
        Console.WriteLine($"wrote {dest}");
        return 0;
    }

    private static int LockBucket(string[] args, AppConfig config)
    {
        if (args.Length < 4 || !uint.TryParse(args[2], out var uid))
        {
            Console.WriteLine("usage: lock <uid3> <appid>   | unlock <uid3> <appid>");
            return 1;
        }

        var unlock = args[1] == "unlock";
        var appId = uint.Parse(args[2]);
        if (config.GuardedAppIds.Contains(appId))
        {
            Console.WriteLine($"appid {appId} is guarded, skipping");
            return 1;
        }

        var steam = ResolveSteam(config);
        var userDir = Path.Combine(steam, "userdata", uid.ToString());
        var engine = new LocalInjectEngine();
        engine.Log += Console.WriteLine;

        if (!unlock)
        {
            var dryRun = !Has(args, "--force") && config.DryRun;
            if (dryRun)
            {
                Console.WriteLine($"DRY-RUN: would lock appid {appId} in {userDir}");
                return 0;
            }
            engine.InstallLock(userDir, appId);
        }
        else
        {
            engine.RemoveLock(userDir, appId);
        }

        return 0;
    }

    private static async Task<int> WebCloud(string[] args, AppConfig config)
    {
        var cookie = config.CookieFile is not null && File.Exists(config.CookieFile)
            ? File.ReadAllText(config.CookieFile).Trim()
            : Environment.GetEnvironmentVariable("SCT_COOKIE");

        if (string.IsNullOrEmpty(cookie))
        {
            Console.WriteLine("Set SCT_COOKIE or config.CookieFile (a steam session cookie) for web lane.");
            return 1;
        }

        var web = new SteamWebClient(cookie);
        var cmd = args.Length > 1 ? args[1].ToLowerInvariant() : "ls";

        switch (cmd)
        {
            case "ls":
            {
                var apps = await web.ListAppsAsync();
                Console.WriteLine($"{apps.Count} app(s) visible on remote storage:");
                foreach (var a in apps.OrderBy(a => a.AppId))
                    Console.WriteLine($"  [{a.AppId}] {a.Name}  ({a.FileCount} files, {a.TotalBytes}b)");
                return 0;
            }
            case "files":
            {
                if (args.Length < 3 || !uint.TryParse(args[2], out var appId))
                {
                    Console.WriteLine("usage: web files <appid>");
                    return 1;
                }
                var files = await web.ListFilesAsync(appId);
                Console.WriteLine($"{appId}: {files.Count} file(s):");
                foreach (var f in files)
                    Console.WriteLine($"  {f.FileName}  {f.Size}b  {f.Detail ?? ""}");
                return 0;
            }
            case "dl":
            {
                if (args.Length < 4 || !uint.TryParse(args[2], out var appId))
                {
                    Console.WriteLine("usage: web dl <appid> <name> [outfile]");
                    return 1;
                }
                var outFile = args.Length > 4 ? args[4] : $"{appId}_{args[3]}";
                var bytes = await web.DownloadAsync(appId, args[3]);
                if (bytes is null)
                {
                    Console.WriteLine("download failed (no data)");
                    return 1;
                }
                File.WriteAllBytes(outFile, bytes);
                Console.WriteLine($"saved {bytes.Length}b -> {outFile}");
                return 0;
            }
            default:
                Console.WriteLine("usage: web ls | files <appid> | dl <appid> <file> [outfile]");
                return 1;
        }
    }

    private static async Task<int> FerryCmd(string[] args, AppConfig config)
    {
        var sub = args.Length > 1 ? args[1].ToLowerInvariant() : "ls";
        if (sub is not ("ls" or "upload" or "dl"))
        {
            Console.WriteLine("usage: ferry ls | upload <local-file> [name] | dl <name> [outfile]");
            return 1;
        }

        await using var session = await ConnectSessionAsync();
        var rpc = new CloudRpcClient(session);

        switch (sub)
        {
            case "ls":
            {
                var files = await rpc.EnumerateAsync(Ferry.SpacewarApp);
                Console.WriteLine($"AppID 480 (Spacewar) bucket - {files.Count} file(s):");
                foreach (var f in files)
                {
                    var (src, orig) = Ferry.UnparkName(f.FileName);
                    var origin = src != 0 && orig != f.FileName ? $" (from AppID {src}: {orig})" : "";
                    Console.WriteLine($"  {f.FileName}  {f.FileSize}b  ts={f.Timestamp}{origin}");
                }
                return 0;
            }
            case "upload":
            {
                if (args.Length < 3)
                {
                    Console.WriteLine("usage: ferry upload <local-file> [name]");
                    return 1;
                }
                var src = args[2];
                var data = File.ReadAllBytes(src);
                var name = args.Length > 3 ? args[3] : Ferry.ParkName(0, Path.GetFileName(src).Replace("_", "-"));
                var result = await rpc.UploadAsync(Ferry.SpacewarApp, name, data);
                Console.WriteLine($"upload('{name}') -> {result} ({data.Length}b)");
                return result == SteamKit2.EResult.OK ? 0 : 1;
            }
            default:
            {
                if (args.Length < 3)
                {
                    Console.WriteLine("usage: ferry dl <name> [outfile]");
                    return 1;
                }
                var name = args[2];
                var data = await rpc.DownloadAsync(Ferry.SpacewarApp, name);
                if (data is null)
                {
                    Console.WriteLine("download failed");
                    return 1;
                }
                var outFile = args.Length > 3 ? args[3] : name.Replace('/', '_');
                File.WriteAllBytes(outFile, data);
                Console.WriteLine($"saved {data.Length}b -> {outFile}");
                return 0;
            }
        }
    }

    private static int RelocateBucket(string[] args, AppConfig config)
    {
        if (args.Length < 4 || !uint.TryParse(args[2], out var uid) || !uint.TryParse(args[3], out var appId))
        {
            Console.WriteLine("usage: relocate <uid3> <appid>   | unrelocate <uid3> <appid>");
            return 1;
        }

        var undo = args[1] == "unrelocate";
        if (!undo && config.GuardedAppIds.Contains(appId))
        {
            Console.WriteLine($"appid {appId} is guarded, skipping");
            return 1;
        }

        var steam = ResolveSteam(config);
        var userDir = Path.Combine(steam, "userdata", uid.ToString());
        var engine = new LocalInjectEngine();
        engine.Log += Console.WriteLine;

        if (!undo)
        {
            var dryRun = !Has(args, "--force") && config.DryRun;
            if (dryRun)
            {
                Console.WriteLine($"DRY-RUN: would relocate appid {appId} from {userDir}");
                return 0;
            }
            engine.Relocate(userDir, appId);
        }
        else
        {
            engine.Unrelocate(userDir, appId);
        }

        return 0;
    }

    private static async Task<SteamSession> ConnectSessionAsync()
    {
        var session = new SteamSession();
        session.Event += Console.WriteLine;

        var mode = (Environment.GetEnvironmentVariable("SCT_AUTH_MODE") ?? "").ToLowerInvariant();
        var user = Environment.GetEnvironmentVariable("SCT_USER");
        var pass = Environment.GetEnvironmentVariable("SCT_PASS");

        var ok = mode switch
        {
            "qr" => await session.ConnectAsync(AuthMode.Qr),
            _ when user is { Length: > 0 } && pass is { Length: > 0 }
                => await session.ConnectAsync(AuthMode.Credentials, user, pass),
            _ => await session.ConnectAsync(AuthMode.Anonymous),
        };

        if (!ok)
        {
            await session.DisposeAsync();
            throw new InvalidOperationException("could not log on to Steam (set SCT_USER/SCT_PASS or SCT_AUTH_MODE=qr)");
        }

        return session;
    }

    private static async Task<int> PoolCmd(string[] args, AppConfig config)
    {
        var sub = args.Length > 1 ? args[1].ToLowerInvariant() : "list";
        switch (sub)
        {
            case "list":
            {
                Console.WriteLine("Parking pool (owned-game buckets are NEVER selected):");
                var reg = SctRegistry.Load();
                foreach (var p in PoolDb.DefaultPool
                             .OrderBy(p => p.Tier).ThenBy(p => p.AppId))
                {
                    var state = p.State.ToString().ToLowerInvariant();
                    var probe = reg.PoolProbes.TryGetValue(p.AppId, out var ps) ? $"[probe:{ps}]" : "";
                    Console.WriteLine($"  [{p.Tier}] {p.AppId,-8} {p.Name,-32} ({(p.IsFree ? "free" : "PAID")}, {p.ReleaseYear}) {state,-18} {probe} {p.Category} - {p.Note}");
                }
                return 0;
            }
            case "refresh":
            {
                Console.WriteLine($"Refreshing pool metadata from the store API ({PoolDb.DefaultPool.Count} apps)...");
                var progress = new Progress<(uint, AppDetails)>(
                    r => Console.WriteLine($"  {r.Item1}: {r.Item2.Name} (free={r.Item2.IsFree}, {r.Item2.ReleaseDate})"));
                var results = await StoreApi.RefreshPoolAsync(
                    PoolDb.DefaultPool.Select(p => p.AppId), progress);
                var ok = results.Count(r => r.Details.Found);
                Console.WriteLine($"{ok}/{results.Count} resolved.");
                return 0;
            }
            case "probe":
            {
                return await PoolProbeCmd(args, config);
            }
            default:
                Console.WriteLine("usage: pool list | refresh | probe [--uid <id3>] [--wait-sec N]");
                return 1;
        }
    }

    /// <summary>
    /// Private writability probe: drops ONE tiny barcode-tagged file into each candidate
    /// slot bucket, lets the RUNNING Steam client sync it (no SCT logon), reads the verdict
    /// from cloud_log.txt, then cleans the probe back out. No anonymous flooding - one
    /// quiet file per real app at a time.
    /// </summary>
    private static async Task<int> PoolProbeCmd(string[] args, AppConfig config)
    {
        var steam = ResolveSteam(config);
        var uid = ResolveUid(args, config, steam);
        if (uid == 0)
        {
            Console.WriteLine("no account - pass --uid <id3> or run with Steam logged in");
            return 1;
        }
        if (!SteamLocator.IsRunning())
        {
            Console.WriteLine("Steam is not running - the client must be up to sync the probe and log the verdict.");
            return 1;
        }

        var waitSec = Arg(args, "--wait-sec") is { } ws && int.TryParse(ws, out var w) ? w : 20;
        var dryRun = !Has(args, "--force") && config.DryRun;
        Console.WriteLine($"probe account {uid}: {PoolDb.Usable().Count()} candidate slot(s), wait {waitSec}s each{ (dryRun ? " (DRY-RUN)" : "")}");

        var registry = SctRegistry.Load();
        var engine = new LocalInjectEngine();
        engine.Log += Console.WriteLine;
        var random = Random.Shared;
        var total = 0;

        foreach (var slot in PoolDb.Usable().OrderBy(p => p.Tier).ThenBy(p => p.AppId))
        {
            var userAppDir = Path.Combine(steam, "userdata", uid.ToString(), slot.AppId.ToString());
            var probePath = Path.Combine(userAppDir, "remote", ProbeFileName);

            if (dryRun)
            {
                Console.WriteLine($"  [dry] would probe {slot.AppId} ({slot.Name})");
                continue;
            }

            if (!File.Exists(probePath))
            {
                var payload = $"{slot.AppId}{Barcode.Sep}probe{Barcode.Sep}{DateTime.Now:ddMMyyyy}";
                var probeBytes = new byte[64];
                random.NextBytes(probeBytes);
                var tagged = probeBytes.Concat(Barcode.PackTrailer(payload)).ToArray();
                Directory.CreateDirectory(Path.Combine(userAppDir, "remote"));
                File.WriteAllBytes(probePath, tagged);
            }
            engine.RegenerateVdf(userAppDir);

            var verdict = await new CloudLogWatcher(steam, slot.AppId)
                .WaitForVerdictAsync(TimeSpan.FromSeconds(waitSec));
            total++;

            switch (verdict.Verdict)
            {
                case CloudVerdict.Success:
                    registry.PoolProbes[slot.AppId] = "VerifiedWritable";
                    Console.WriteLine($"  {slot.AppId} ({slot.Name}): {CloudVerdict.Success} - slot proven, probe removed");
                    File.Delete(probePath);
                    engine.RegenerateVdf(userAppDir);
                    break;
                case CloudVerdict.Denied:
                    registry.PoolProbes[slot.AppId] = "Denied";
                    Console.WriteLine($"  {slot.AppId} ({slot.Name}): {CloudVerdict.Denied} - account is not entitled; slot excluded");
                    File.Delete(probePath);
                    engine.RegenerateVdf(userAppDir);
                    break;
                default:
                    Console.WriteLine($"  {slot.AppId} ({slot.Name}): {verdict.Verdict} - no verdict yet (client quiet / not syncing this app)");
                    break;
            }
            registry.Save();
        }

        Console.WriteLine($"probe done: {total} slot(s) checked; verdicts saved to registry (see object)");
        return 0;
    }

    private const string ProbeFileName = "sctprobe.bin";

    private static async Task<int> ParkCmd(string[] args, AppConfig config)
    {
        // shapes: park <uid3> <gameAppId> [flags]   |   park <gameAppId> [--uid <id3>] [flags]
        var a1 = args.Length > 1 && uint.TryParse(args[1], out var x1) ? x1 : 0u;
        var a2 = args.Length > 2 && uint.TryParse(args[2], out var x2) ? x2 : 0u;

        uint uid, gameAppId;
        if (args.Length >= 3 && a1 != 0 && a2 != 0) { uid = a1; gameAppId = a2; }
        else if (args.Length >= 2 && a1 != 0)
        {
            gameAppId = a1;
            var steam0 = ResolveSteam(config);
            uid = ResolveUid(args, config, steam0);
            if (uid == 0)
            {
                Console.WriteLine("no account to park for - pass --uid <id3> or run with Steam logged in");
                return 1;
            }
        }
        else
        {
            Console.WriteLine("usage: park <gameAppId> [--uid <id3>] [--force] [--offline] [--client|--rpc] [--spread N] [--copies N] [--stealth] [--wait-sec N]");
            return 1;
        }

        var offline = Has(args, "--offline");
        var stealth = Has(args, "--stealth");
        var spread = Arg(args, "--spread") is { } sp && int.TryParse(sp, out var s) ? Math.Max(1, s) : 1;
        var copies = Arg(args, "--copies") is { } cp && int.TryParse(cp, out var c) ? Math.Max(1, c) : 1;
        var dry = !Has(args, "--force") && config.DryRun;
        var steam = ResolveSteam(config);

        var bucketDir = Path.Combine(steam, "userdata", uid.ToString(), gameAppId.ToString());
        if (!Directory.Exists(bucketDir))
        {
            Console.WriteLine($"no local bucket {bucketDir}");
            return 1;
        }

        var registry = SctRegistry.Load();
        var files = Directory.EnumerateFiles(bucketDir, "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).Equals("remotecache.vdf", StringComparison.OrdinalIgnoreCase))
            .Select(f => new FileInfo(f))
            .ToList();
        if (files.Count == 0)
        {
            Console.WriteLine("bucket is empty (nothing to park)");
            return 0;
        }

        var engine = new ParkingEngine(config.GetOwnedSet(), registry.Slots, poolProbes: registry.PoolProbes);
        Console.WriteLine($"{gameAppId}: {files.Count} file(s) to park | spread={spread} copies={copies} stealth={stealth} | owned-set {config.GetOwnedSet().Count} | uid {uid}");

        // lane choice: --rpc forces the logon-based upload lane; otherwise, if Steam is
        // running with this account signed in, use the CLIENT lane = stage locally, let the
        // running session sync, read the verdict. No SCT login at all.
        var clientLane = Has(args, "--client")
            || (!Has(args, "--rpc") && SteamLocator.IsRunning() && SteamLocator.GetActiveAccount(steam)?.AccountId == uid);
        if (clientLane && !SteamLocator.IsRunning())
        {
            Console.WriteLine("--client requested but Steam is not running");
            return 1;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var decisions = engine.Plan(gameAppId, files.Select(f => new ParkFile(f.Name, f.Length)).ToList(),
            stealth, spread, copies);

        var plans = new List<(string FileName, ParkingDecision D, byte[] Tagged)>();
        var di = 0;
        for (var i = 0; i < files.Count; i++)
        {
            for (var ci = 0; ci < copies; ci++)
            {
                var d = decisions[di++];
                if (!d.Ok)
                {
                    Console.WriteLine($"  {files[i].Name}: PARK REFUSED - {d.Reason}");
                    continue;
                }
                var tail = ReadTail(files[i].FullName, Math.Min(files[i].Length, Barcode.TailWindowBytes));
                if (Barcode.TryDecodeTail(tail, out var payload, out _))
                {
                    var (game, _, _) = Barcode.Parse(payload);
                    if (game == gameAppId && !stealth)
                    {
                        Console.WriteLine($"  skip {files[i].Name}: already tagged (barcode present)");
                        continue;
                    }
                }
                var originalBytes = File.ReadAllBytes(files[i].FullName);
                var trailer = Barcode.PackTrailer(gameAppId.ToString(), uid.ToString(), today);
                plans.Add((files[i].Name, d, originalBytes.Concat(trailer).ToArray()));
            }
        }

        if (plans.Count == 0)
        {
            Console.WriteLine("nothing to park (all files already tagged or refused)");
            return 0;
        }

        foreach (var p in plans)
            Console.WriteLine($"  {p.FileName} -> {p.D.StoredName} @ {p.D.StorageAppId}  [{p.D.Reason}]");

        if (dry)
        {
            Console.WriteLine("dry-run complete (use --force to execute) - registry untouched");
            return 0;
        }

        if (clientLane)
        {
            return await ClientLaneParkAsync(steam, uid, registry, engine, plans, today,
                Arg(args, "--wait-sec") is { } ws && int.TryParse(ws, out var w) ? w : 25);
        }

        // ---- RPC lane: logon-based upload (QR / credentials / anonymous) ----
        await using var session = offline ? null : await ConnectSessionAsync();
        if (session is null && !offline) return 1;
        var rpc = session is null ? null : new CloudRpcClient(session);
        var okCount = 0;
        foreach (var p in plans)
        {
            var res = await rpc!.UploadAsync(p.D.StorageAppId!.Value, p.D.StoredName!, p.Tagged);
            if (res == SteamKit2.EResult.OK)
            {
                var payloadBarcode = $"{gameAppId}{Barcode.Sep}{uid}{Barcode.Sep}{today:ddMMyyyy}";
                registry.Upsert(GameSlot.New(gameAppId, p.D.StorageAppId!.Value, p.D.StoredName!, p.FileName, p.Tagged.Length, payloadBarcode));
                okCount++;
            }
            Console.WriteLine($"  {p.FileName}: {res} -> {p.D.StoredName} @ {p.D.StorageAppId}");
        }
        registry.Save();
        Console.WriteLine($"{okCount}/{plans.Count} parked; registry updated");
        return 0;
    }

    /// <summary>
    /// CLIENT lane: drop tagged files into the slot buckets locally, let the already
    /// signed-in Steam client synchronize them, read the verdict from cloud_log.txt.
    /// Denied slots are excluded from the pool immediately; proven slots get rewritten
    /// as VerifiedWritable. No SCT credentials are ever needed.
    /// </summary>
    private static async Task<int> ClientLaneParkAsync(string steam, uint uid, SctRegistry registry,
        ParkingEngine engine, List<(string FileName, ParkingDecision D, byte[] Tagged)> plans,
        DateOnly today, int waitSec)
    {
        var injector = new LocalInjectEngine();
        var bySlot = plans.GroupBy(p => p.D.StorageAppId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());
        var userDataRoot = Path.Combine(steam, "userdata", uid.ToString());
        var error = 0;

        foreach (var (slot, slotPlans) in bySlot)
        {
            var slotDir = Path.Combine(userDataRoot, slot.ToString(), "remote");
            Directory.CreateDirectory(slotDir);
            foreach (var p in slotPlans)
                await File.WriteAllBytesAsync(Path.Combine(slotDir, p.D.StoredName!), p.Tagged);
            injector.RegenerateVdf(Path.Combine(userDataRoot, slot.ToString()));

            Console.WriteLine($"  [{slot}] staged {slotPlans.Count} file(s); waiting up to {waitSec}s for the client sync...");
            var verdict = await new CloudLogWatcher(steam, slot).WaitForVerdictAsync(TimeSpan.FromSeconds(waitSec));
            Console.WriteLine($"  [{slot}] {verdict.Verdict}" + (verdict.MatchLine is null ? "" : $"  <{verdict.MatchLine.Trim()}>"));

            switch (verdict.Verdict)
            {
                case CloudVerdict.Success:
                    registry.PoolProbes[slot] = "VerifiedWritable";
                    foreach (var p in slotPlans)
                    {
                        var game = ReadGameFromTrailer(p.Tagged);
                        if (game is null) continue;
                        var payload = $"{game}{Barcode.Sep}{uid}{Barcode.Sep}{today:ddMMyyyy}";
                        registry.Upsert(GameSlot.New(game.Value, slot, p.D.StoredName!, p.FileName, p.Tagged.Length, payload));
                    }
                    break;
                case CloudVerdict.Denied:
                    registry.PoolProbes[slot] = "Denied";
                    foreach (var p in slotPlans)
                    {
                        var staged = Path.Combine(slotDir, p.D.StoredName!);
                        if (File.Exists(staged)) File.Delete(staged);
                    }
                    injector.RegenerateVdf(Path.Combine(userDataRoot, slot.ToString()));
                    Console.WriteLine($"  [{slot}] slot excluded (account not entitled for this app); staged files removed");
                    error++;
                    break;
                default:
                    Console.WriteLine($"  [{slot}] no verdict yet - files stay staged locally; the client syncs on its next cloud tick (re-check with 'pool probe' later)");
                    break;
            }

            registry.Save();
        }

        Console.WriteLine(error == 0
            ? "client-lane park complete - verify with 'scan' after the client syncs"
            : $"{error} slot(s) denied by the server - excluded from the pool");
        return error == 0 ? 0 : 1;
    }

    private static uint? ReadGameFromTrailer(byte[] tagged)
    {
        var start = Math.Max(0, tagged.Length - Barcode.TailWindowBytes);
        if (!Barcode.TryDecodeTail(tagged.AsSpan(start), out var payload, out _)) return null;
        var (game, _, _) = Barcode.Parse(payload);
        return game;
    }

    private static async Task<RemoteBucketSnapshot?> PoolRemoteSnapshotAsync(CloudRpcClient rpc, uint appId)
    {
        try
        {
            var files = await rpc.EnumerateAsync(appId);
            Quota? quota = null;
            try { quota = await rpc.QuotaAsync(appId); } catch { }
            return new RemoteBucketSnapshot(appId, files, quota);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<int> UnparkCmd(string[] args, AppConfig config)
    {
        if (args.Length < 3 || !uint.TryParse(args[1], out var storageAppId))
        {
            Console.WriteLine("usage: unpark <storageAppId> <remoteName> [outdir]");
            return 1;
        }

        var name = args[2];
        await using var session = await ConnectSessionAsync();
        var rpc = new CloudRpcClient(session);

        var tagged = await rpc.DownloadAsync(storageAppId, name);
        if (tagged is null)
        {
            Console.WriteLine("download failed");
            return 1;
        }
        if (!Barcode.TryDecodeTail(ReadTailBytes(tagged), out var payload, out var trailerLen))
        {
            Console.WriteLine($"no barcode trailer in {name} - saving as-is");
            trailerLen = 0;
        }
        else
        {
            var (game, _, _) = Barcode.Parse(payload);
            Console.WriteLine($"barcode: {payload} (game {game})");
        }

        var clean = trailerLen > 0 ? Barcode.StripTrailer(tagged, trailerLen) : tagged;
        var outDir = args.Length > 3 ? args[3] : Path.Combine("unparked", storageAppId.ToString());
        Directory.CreateDirectory(outDir);

        // registry knows the true original name even for stealth/hashed stored names
        var registry = SctRegistry.Load();
        var slot = registry.FindByStoredName(storageAppId, name);
        var (src, orig) = Ferry.UnparkName(name);
        var outFile = Path.Combine(outDir, src == 0 ? name : orig);
        if (slot is { OriginalName.Length: > 0 } && Path.GetFileName(outFile) != slot.OriginalName)
            outFile = Path.Combine(outDir, slot.OriginalName);
        await File.WriteAllBytesAsync(outFile, clean);
        Console.WriteLine($"{clean.Length}b -> {outFile}");

        registry.Remove(storageAppId, name);
        registry.Save();
        return 0;
    }

    private static int RebuildCmd(string[] args, AppConfig config)
    {
        var steam = ResolveSteam(config);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var registry = PoolScanner.RebuildRegistry(steam);
        sw.Stop();
        registry.Save();
        Console.WriteLine($"rebuild: {registry.Slots.Count} slot(s) in {sw.ElapsedMilliseconds}ms (registry: {SctRegistry.DefaultPath()})");
        foreach (var s in registry.Slots.OrderBy(s => s.StorageAppId).ThenBy(s => s.StoredName))
            Console.WriteLine($"  [{s.StorageAppId}] {s.StoredName}  game={s.GameAppId}  {s.BarcodePayload}");
        return 0;
    }

    private static int BarcodeCmd(string[] args, AppConfig config)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: barcode <file> | barcode make <payload>");
            return 1;
        }

        if (args[1] == "make")
        {
            var makePayload = args.Length > 2 ? string.Join(" ", args.Skip(2)) : "588650|1201110076|09082026";
            Console.WriteLine($"payload: {makePayload}");
            foreach (var line in Barcode.RenderBarcode(makePayload))
                Console.WriteLine(line);
            return 0;
        }

        var path = args[1];
        if (!File.Exists(path))
        {
            Console.WriteLine($"no such file: {path}");
            return 1;
        }
        var info = new FileInfo(path);
        var tail = ReadTail(path, Math.Min(info.Length, Barcode.TailWindowBytes));
        if (!Barcode.TryDecodeTail(tail, out var payload, out var trailerLen))
        {
            Console.WriteLine($"{path}: no SCT barcode trailer");
            return 1;
        }
        var (game, uid, date) = Barcode.Parse(payload);
        Console.WriteLine($"{path} ({info.Length}b):");
        Console.WriteLine($"  trailer    : {trailerLen}b at end (magic {Barcode.Magic})");
        Console.WriteLine($"  payload    : {payload}");
        Console.WriteLine($"  game appid : {game}");
        Console.WriteLine($"  user id3   : {uid}");
        Console.WriteLine($"  tagged on  : {date:dd/MM/yyyy}");
        Console.WriteLine("  visual:");
        foreach (var line in Barcode.RenderBarcode(payload))
            Console.WriteLine("    " + line);
        return 0;
    }

    private static byte[] ReadTailBytes(byte[] data)
        => data.Length > Barcode.TailWindowBytes ? data[^Barcode.TailWindowBytes..] : data;

    private static byte[] ReadTail(string path, long count)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        fs.Seek(-count, SeekOrigin.End);
        var buf = new byte[count];
        fs.ReadExactly(buf);
        return buf;
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static bool Has(string[] args, string name) => args.Contains(name);

    private static uint ResolveUid(string[] args, AppConfig config, string steam)
    {
        if (Arg(args, "--uid") is { } u && uint.TryParse(u, out var id)) return id;
        return SteamLocator.GetActiveAccount(steam)?.AccountId ?? 0;
    }

    private static int Help()
    {
        Console.WriteLine("""
            SteamCloudTamper - Steam cloud save manager (official + SteamTools/GreenLuma/SLS-era buckets)

            detect                    locate Steam + accounts + libraries
            scan                      audit local userdata buckets (per account/app)
            remote-list --app <id>    list files in a cloud bucket
            probe <appid...>          check what Valve allows: enumerate / upload / delete
            wipe <appid> <file> [--blank] [--force]      delete or blank one cloud file
            wipe-all <appid> [--blank] [--force]          wipe entire bucket
            guards add|rm|ls <appid>   maintain never-touch list (persisted)
            inject <uid3> <appid> <file> [remote-name]    userdata drop + remotecache.vdf regen
            lock/unlock <uid3> <appid>   isolate a bucket locally: read-only file blocks Steam re-creation
            relocate/unrelocate <uid3> <appid>  junction-isolate bucket into %LOCALAPPDATA%\SCT\stash

            web lane (needs SCT_COOKIE):  web ls | files <appid> | dl <appid> <file>
            ferry (park saves into owned AppID 480 bucket):
                ferry ls | upload <local-file> [name] | dl <name> [outfile]

            parking brain (anti-ban: private cloud saves of real apps only, never public flooding):
                pool list | refresh | probe [--uid <id3>] [--force] [--wait-sec N]
                                      probe = one-private-file writability check via the RUNNING
                                      Steam client (no logon); verdicts saved to the registry
                park <gameAppId> [--uid <id3>] [--force] [--client|--rpc] [--spread N] [--copies N] [--stealth] [--wait-sec N]
                                      default lane = client: stage locally, the signed-in Steam
                                      client syncs, verdict read from cloud_log.txt (no SCT login)
                unpark <storageAppId> <name> [outdir]   download + strip barcode
                rebuild                    tail-scan userdata -> registry.json
                barcode <file> | barcode make <payload>   show/render barcode trailers
            registry: {SctRegistry.DefaultPath()}

            auth: anonymous by default; env SCT_USER/SCT_PASS or SCT_AUTH_MODE=qr for account ops.
                  IMPORTANT: the client lane needs no auth - it rides the running Steam session.
            """);
        return 0;
    }
}