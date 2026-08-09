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
                foreach (var p in PoolDb.DefaultPool
                             .OrderBy(p => p.Tier).ThenBy(p => p.AppId))
                {
                    var state = p.State.ToString().ToLowerInvariant();
                    Console.WriteLine($"  [{p.Tier}] {p.AppId,-8} {p.Name,-32} {(p.IsFree ? "free" : "PAID")} {p.ReleaseYear} {state,-18} {p.Note}");
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
            default:
                Console.WriteLine("usage: pool list | refresh");
                return 1;
        }
    }

    private static async Task<int> ParkCmd(string[] args, AppConfig config)
    {
        if (args.Length < 3 || !uint.TryParse(args[1], out var uid) || !uint.TryParse(args[2], out var gameAppId))
        {
            Console.WriteLine("usage: park <uid3> <gameAppId> [--force] [--offline]   (parks all local bucket files with barcode trailers)");
            return 1;
        }

        var offline = Has(args, "--offline");
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

        await using var session = offline ? null : await ConnectSessionAsync();
        var rpc = session is null ? null : new CloudRpcClient(session);

        var engine = new ParkingEngine(config.GetOwnedSet(), registry.Slots,
            rpc is null
                ? null
                : appId => Task.FromResult<RemoteBucketSnapshot?>(PoolRemoteSnapshotAsync(rpc, appId).GetAwaiter().GetResult()));

        Console.WriteLine($"{gameAppId}: {files.Count} file(s) to park, owned-set size {config.GetOwnedSet().Count}");
        var dry = !Has(args, "--force") && config.DryRun;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var okCount = 0;

        foreach (var f in files)
        {
            var tail = ReadTail(f.FullName, Math.Min(f.Length, Barcode.TailWindowBytes));
            if (Barcode.TryDecodeTail(tail, out var payload, out _))
            {
                var (game, _, _) = Barcode.Parse(payload);
                if (game == gameAppId)
                {
                    Console.WriteLine($"  skip {f.Name}: already tagged (barcode present)");
                    continue;
                }
            }

            var decision = engine.Pick(gameAppId, f.Name, f.Length);
            if (!decision.Ok)
            {
                Console.WriteLine($"  {f.Name}: PARK REFUSED - {decision.Reason}");
                continue;
            }

            var originalBytes = File.ReadAllBytes(f.FullName);
            var trailer = Barcode.PackTrailer(gameAppId.ToString(), uid.ToString(), today);
            var tagged = originalBytes.Concat(trailer).ToArray();
            var storedName = decision.StoredName!;

            if (dry)
            {
                Console.WriteLine($"  [dry] would park {f.Name} ({f.Length}b) -> {storedName} @ {decision.StorageAppId}");
                continue;
            }

            if (rpc is null)
            {
                Console.WriteLine("  offline mode: no session - nothing uploaded (use --offline only for planning)");
                continue;
            }

            var res = await rpc.UploadAsync(decision.StorageAppId!.Value, storedName, tagged);
            if (res == SteamKit2.EResult.OK)
            {
                var barcodePayload = $"{gameAppId}{Barcode.Sep}{uid}{Barcode.Sep}{today:ddMMyyyy}";
                registry.Upsert(GameSlot.New(gameAppId, decision.StorageAppId.Value, storedName, f.Name, tagged.Length, barcodePayload));
                okCount++;
            }
            Console.WriteLine($"  {f.Name}: {res} -> {storedName} @ {decision.StorageAppId}");
        }

        if (!dry) registry.Save();
        Console.WriteLine(dry
            ? "dry-run complete (use --force to execute the uploads)"
            : $"{okCount}/{files.Count} parked; registry updated");
        return 0;
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
        var (src, orig) = Ferry.UnparkName(name);
        var outFile = Path.Combine(outDir, src == 0 ? name : orig);
        await File.WriteAllBytesAsync(outFile, clean);
        Console.WriteLine($"{clean.Length}b -> {outFile}");

        var registry = SctRegistry.Load();
        registry.Remove(name);
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

            parking brain:
                pool list | refresh        curated parking-slot pool (never owned games)
                park <uid3> <gameAppId>    park local bucket -> best slot (barcode trailer)
                unpark <storageAppId> <name> [outdir]   download + strip barcode
                rebuild                    tail-scan userdata -> registry.json
                barcode <file> | barcode make <payload>   show/render barcode trailers
            registry: {SctRegistry.DefaultPath()}

            auth: anonymous by default; env SCT_USER/SCT_PASS or SCT_AUTH_MODE=qr for account ops
            """);
        return 0;
    }
}