using SteamCloudTamper.Core;
using SteamCloudTamper.Core.Steam;
using SteamCloudTamper.Engines;

namespace SteamCloudTamper.Cli;

public static class Program
{
    private const string ConfigPath = "steamcloudtamper.json";

    public static async Task<int> Main(string[] args)
    {
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
                Console.WriteLine($"      {f.FileName} ({f.FileSize}b)");
        }

        return 0;
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

    private static async Task<SteamSession> ConnectSessionAsync()
    {
        var session = new SteamSession();
        session.Event += Console.WriteLine;

        var user = Environment.GetEnvironmentVariable("SCT_USER");
        var pass = Environment.GetEnvironmentVariable("SCT_PASS");
        var ok = user is { Length: > 0 } && pass is { Length: > 0 }
            ? await session.ConnectAsync(AuthMode.Credentials, user, pass)
            : await session.ConnectAsync(AuthMode.Anonymous);

        if (!ok)
        {
            await session.DisposeAsync();
            throw new InvalidOperationException("could not log on to Steam (anonymous or SCT_USER/SCT_PASS logon failed)");
        }

        return session;
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

            auth: anonymous by default; env SCT_USER/SCT_PASS for account ops
            """);
        return 0;
    }
}