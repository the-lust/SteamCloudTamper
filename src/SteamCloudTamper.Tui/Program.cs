using Spectre.Console;
using SteamCloudTamper.Core;
using SteamCloudTamper.Core.Pool;
using SteamCloudTamper.Core.Steam;
using SteamCloudTamper.Engines;

namespace SteamCloudTamper.Tui;

public static class Program
{
    private const string ConfigPath = "steamcloudtamper.json";

    private static AppConfig _cfg = null!;
    private static string _steamPath = "";
    private static List<SteamAccount> _accounts = [];
    private static List<Bucket> _buckets = [];
    private static SctRegistry _registry = null!;

    public static async Task<int> Main()
    {
        var brand = Branding.RenderRawBrand();
        if (brand.Length > 0)
        {
            foreach (var line in brand.Split('\n').Take(8))
                Console.WriteLine(line.TrimEnd());
        }
        else
        {
            AnsiConsole.MarkupLine("[bold aqua]STEAM CLOUD SAVER[/] - park, tag, ferry, survive.");
        }
        Console.WriteLine();

        _cfg = AppConfig.Load(ConfigPath);
        _steamPath = _cfg.SteamPathOverride ?? SteamLocator.DetectInstallPath() ?? "";
        if (string.IsNullOrEmpty(_steamPath))
        {
            _steamPath = AnsiConsole.Ask<string>("Steam not detected. Enter the Steam install folder:");
            _cfg.SteamPathOverride = _steamPath;
            _cfg.Save(ConfigPath);
        }

        RefreshLocal();

        while (true)
        {
            var choice = AnsiConsole.Prompt(new SelectionPrompt<string>()
                .Title(MenuTitle())
                .AddChoices(
                    $"{Ui.Icon("folder")} [cyan]1[/] Buckets       - audit local userdata",
                    $"{Ui.Icon("cloud")} [cyan]2[/] Remote        - list cloud buckets via Steam",
                    $"{Ui.Icon("ferry")} [cyan]3[/] Ferry         - park saves into owned AppID 480 (Spacewar)",
                    $"{Ui.Icon("park")} [cyan]4[/] Park smart    - barcode park local buckets (never owned games)",
                    $"{Ui.Icon("wipe")} [cyan]5[/] Wipe          - delete/blank bucket files (dry-run by default)",
                    $"{Ui.Icon("registry")} [cyan]6[/] Registry     - slot map + parking pool",
                    $"{Ui.Icon("shield")} [cyan]7[/] Guards        - never-touch appid list",
                    $"{Ui.Icon("gear")} [cyan]8[/] Settings      - dry-run, owned list, steam path",
                    $"{Ui.Icon("qr")} [cyan]9[/] Logon         - QR / credentials session (opens the real doors)",
                    $"{Ui.Icon("x")} [red]0[/] Quit"));

            switch (choice[0])
            {
                case '1': BucketsScreen(); break;
                case '2': await RemoteScreenAsync(); break;
                case '3': await FerryScreenAsync(); break;
                case '4': await ParkScreenAsync(); break;
                case '5': await WipeScreenAsync(); break;
                case '6': await RegistryScreenAsync(); break;
                case '7': GuardsScreen(); break;
                case '8': SettingsScreen(); break;
                case '9': await LogonScreenAsync(); break;
                default: return 0;
            }
            Console.WriteLine();
        }
    }

    private static string MenuTitle()
    {
        var slots = _registry.Slots.Count;
        return $"SCT | steam: [cyan]{_steamPath}[/] | accts: [yellow]{_accounts.Count}[/] | owned: [yellow]{_cfg.GetOwnedSet().Count}[/] | buckets: [yellow]{_buckets.Count}[/] | slots: [aqua]{slots}[/] | [{( _cfg.DryRun ? "green" : "red")}]{(_cfg.DryRun ? "dry-run" : "LIVE")}[/]";
    }

    private static void RefreshLocal()
    {
        _accounts = SteamLocator.ListAccounts(_steamPath);
        var audit = new AuditEngine(_cfg);
        _buckets = audit.ListLocal(_steamPath, owned: _cfg.GetOwnedSet());
        _registry = SctRegistry.Load();
    }

    private static async Task<SteamSession?> ConnectSessionAsync(bool quiet = false)
    {
        var session = new SteamSession();
        session.Event += msg =>
        {
            if (!quiet) AnsiConsole.MarkupLineInterpolated($"[dim]{Markup.Escape(msg)}[/]");
        };

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
            AnsiConsole.MarkupLine("[red]Steam logon failed[/] - try the Logon screen or set SCT_USER/SCT_PASS / SCT_AUTH_MODE=qr.");
            return null;
        }
        return session;
    }

    // ---------- Buckets ----------

    private static void BucketsScreen()
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title($"{Ui.Icon("folder")} Local buckets (userdata)")
            .AddColumn(new TableColumn("App").Centered())
            .AddColumn(new TableColumn("Era"))
            .AddColumn(new TableColumn("Files").Centered())
            .AddColumn(new TableColumn("Size").Centered())
            .AddColumn(new TableColumn("Note").LeftAligned());

        foreach (var b in _buckets.OrderBy(b => b.AppId))
        {
            table.AddRow(
                b.AppId.ToString(),
                EraStyle(b.Era),
                b.Files.Count.ToString(),
                HumanSize(b.TotalBytes),
                Markup.Escape(b.Note));
        }
        AnsiConsole.Write(table);
        Footer("enter/0 to go back");
    }

    private static string EraStyle(Era e) => e switch
    {
        Era.SteamTools760 => "[red3]ST-760[/]",
        Era.GreenLumaRealAppId => "[orange1]GL-real[/]",
        Era.Owned => "[green]owned[/]",
        Era.Emulated => "[purple]emu[/]",
        Era.Client => "[grey]client[/]",
        _ => "[grey]unk[/]",
    };

    // ---------- Remote ----------

    private static async Task RemoteScreenAsync()
    {
        await using var session = await ConnectSessionAsync();
        if (session is null) return;
        var rpc = new CloudRpcClient(session);

        while (true)
        {
            var appId = AnsiConsole.Ask<uint>("Appid to list (0 = back):");
            if (appId == 0) break;

            try
            {
                var files = await AnsiConsole.Status()
                    .StartAsync($"Listing {appId}...", _ => rpc.EnumerateAsync(appId));
                var table = new Table().Border(TableBorder.Rounded).Title($"Remote {appId}")
                    .AddColumn("File").AddColumn("Size").AddColumn("Time").AddColumn("SHA");
                foreach (var f in files)
                    table.AddRow(Markup.Escape(f.FileName), HumanSize(f.FileSize), f.Timestamp.ToString(), ShortSha(f.FileSha));
                AnsiConsole.Write(table);

                try
                {
                    var quota = await rpc.QuotaAsync(appId);
                    AnsiConsole.MarkupLine($"[dim]quota: {HumanSize((long)quota.ExistingBytes)}/{HumanSize((long)quota.MaxBytes)}, {quota.ExistingFiles}/{quota.MaxFiles} files[/]");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[dim]quota: {Markup.Escape(ex.Message)}[/]");
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            }
        }
    }

    // ---------- Ferry ----------

    private static async Task FerryScreenAsync()
    {
        await using var session = await ConnectSessionAsync();
        if (session is null) return;
        var rpc = new CloudRpcClient(session);

        while (true)
        {
            var choice = AnsiConsole.Prompt(new SelectionPrompt<string>()
                .Title("Ferry (AppID 480 / Spacewar)")
                .AddChoices("List parked", "Upload local file", "Download parked", "Back"));
            switch (choice)
            {
                case "List parked":
                {
                    var files = await AnsiConsole.Status().StartAsync("Fetching...", _ => rpc.EnumerateAsync(Ferry.SpacewarApp));
                    var tbl = new Table().Border(TableBorder.Rounded).Title("Parked in 480")
                        .AddColumn("Name").AddColumn("Size").AddColumn("Time").AddColumn("Origin");
                    foreach (var f in files)
                    {
                        var (src, orig) = Ferry.UnparkName(f.FileName);
                        var origin = src != 0 && orig != f.FileName ? $"from {src} ({orig})" : "-";
                        tbl.AddRow(Markup.Escape(f.FileName), HumanSize(f.FileSize), f.Timestamp.ToString(), Markup.Escape(origin));
                    }
                    AnsiConsole.Write(tbl);
                    break;
                }
                case "Upload local file":
                {
                    var src = AnsiConsole.Ask<string>("local file path:");
                    if (!File.Exists(src)) { AnsiConsole.MarkupLine("[red]missing[/]"); break; }
                    var data = await File.ReadAllBytesAsync(src);
                    var name = AnsiConsole.Ask("name in bucket:", Ferry.ParkName(0, Path.GetFileName(src)));
                    var res = await AnsiConsole.Status().StartAsync("Uploading...", _ => rpc.UploadAsync(Ferry.SpacewarApp, name, data));
                    AnsiConsole.MarkupLine(res == SteamKit2.EResult.OK ? $"[green]uploaded {name} ({data.Length}b)[/]" : $"[red]{res}[/]");
                    break;
                }
                case "Download parked":
                {
                    var name = AnsiConsole.Ask<string>("parked name:");
                    var data = await AnsiConsole.Status().StartAsync("Downloading...", _ => rpc.DownloadAsync(Ferry.SpacewarApp, name));
                    if (data is null) { AnsiConsole.MarkupLine("[red]failed[/]"); break; }
                    var outFile = AnsiConsole.Ask("out file:", name.Replace('/', '_'));
                    await File.WriteAllBytesAsync(outFile, data);
                    AnsiConsole.MarkupLine($"[green]saved {data.Length}b -> {outFile}[/]");
                    break;
                }
                default: return;
            }
        }
    }

    // ---------- Park (smart, barcode) ----------

    private static async Task ParkScreenAsync()
    {
        var candidates = _buckets
            .Where(b => b.Files.Count > 0)
            .OrderBy(b => b.AppId)
            .ToList();
        if (candidates.Count == 0) { AnsiConsole.MarkupLine("[dim]no local buckets to park[/]"); return; }

        var labels = candidates.Select(b => $"{b.AppId}  ({b.Files.Count} files, {HumanSize(b.TotalBytes)})").ToList();
        var pick = new SelectionPrompt<string>()
            .Title($"{Ui.Icon("park")} Pick a bucket to park (upload + barcode-tag)")
            .AddChoices(labels);
        pick.AddChoice("Back");
        var choice = AnsiConsole.Prompt(pick);
        if (choice == "Back") return;

        var bucket = candidates[labels.IndexOf(choice)];
        await ParkBucketAsync(bucket.AppId);
    }

    private static async Task ParkBucketAsync(uint gameAppId)
    {
        var files = _buckets.Where(b => b.AppId == gameAppId).SelectMany(b => b.Files).ToList();
        if (files.Count == 0) { AnsiConsole.MarkupLine("[dim]nothing to park[/]"); return; }

        await using var session = await ConnectSessionAsync();
        if (session is null) return;
        var rpc = new CloudRpcClient(session);

        var engine = new ParkingEngine(_cfg.GetOwnedSet(), _registry.Slots,
            appId => Task.FromResult<RemoteBucketSnapshot?>(PoolRemoteSnapshotAsync(rpc, appId).GetAwaiter().GetResult()));

        AnsiConsole.MarkupLine($"[dim]{gameAppId}: running the allocator... owned-set: {_cfg.GetOwnedSet().Count}[/]");
        var today = DateOnly.FromDateTime(DateTime.Now);
        var plans = new List<(Bucket FileOrigin, CloudFileEntry F, ParkingDecision D, string Stored, byte[] Tagged)>();

        foreach (var f in files)
        {
            var filePath = FindBucketFile(gameAppId, f.FileName);
            var size = f.FileSize;
            var decision = engine.Pick(gameAppId, f.FileName, size);
            if (!decision.Ok) { AnsiConsole.MarkupLine($"[yellow]  {f.FileName}: refused - {decision.Reason}[/]"); continue; }

            var stored = decision.StoredName!;
            if (filePath is { } path && File.Exists(path))
            {
                var original = await File.ReadAllBytesAsync(path);
                var trailer = Barcode.PackTrailer(gameAppId.ToString(), session.SteamId != null ? Acct3(session).ToString() : "?", today);
                plans.Add((new Bucket(0, Era.Unknown, "", [f]), f, decision, stored, original.Concat(trailer).ToArray()));
            }
            else
            {
                plans.Add((new Bucket(0, Era.Unknown, "", [f]), f, decision, stored, null!));
            }
        }

        if (plans.Count == 0) { AnsiConsole.MarkupLine("[yellow]nothing parkable[/]"); return; }

        foreach (var p in plans)
        {
            AnsiConsole.MarkupLine($"  [cyan]{p.F.FileName}[/] ({p.F.FileSize}b) -> [aqua]{p.Stored}[/] @ [bold]{p.D.StorageAppId}[/]");
            AnsiConsole.MarkupLine($"    [dim]{p.D.Reason}[/]");
        }

        if (_cfg.DryRun)
        {
            AnsiConsole.MarkupLine("[yellow]DRY-RUN[/] - enable LIVE mode in Settings to upload");
            return;
        }
        if (!AnsiConsole.Confirm("Park now?")) return;

        var ok = 0;
        foreach (var p in plans)
        {
            if (p.Tagged.Length == 0) { AnsiConsole.MarkupLine($"  [red]{p.F.FileName}: no local copy on disk[/]"); continue; }
            var res = await AnsiConsole.Status().StartAsync($"Parking {p.Stored}...", _ => rpc.UploadAsync(p.D.StorageAppId!.Value, p.Stored, p.Tagged));
            if (res == SteamKit2.EResult.OK)
            {
                var payload = $"{gameAppId}{Barcode.Sep}{Acct3(session)}{Barcode.Sep}{today:ddMMyyyy}";
                _registry.Upsert(GameSlot.New(gameAppId, p.D.StorageAppId!.Value, p.Stored, p.F.FileName, p.Tagged.Length, payload));
                ok++;
                AnsiConsole.MarkupLine($"  [green]{Ui.Icon("check")} {p.Stored} @ {p.D.StorageAppId} ({res})[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"  [red]{p.Stored}: {res}[/]");
            }
        }
        _registry.Save();
        AnsiConsole.MarkupLine($"{ok}/{plans.Count} parked; registry saved");
    }

    private static uint Acct3(SteamSession s) => (uint)(s.SteamId?.AccountID ?? 0);

    private static string? FindBucketFile(uint appId, string fileName)
    {
        var userdata = Path.Combine(_steamPath, "userdata");
        if (!Directory.Exists(userdata)) return null;
        foreach (var account in Directory.EnumerateDirectories(userdata))
        {
            var path = Path.Combine(account, appId.ToString(), fileName);
            if (File.Exists(path)) return path;
        }
        return null;
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
        catch { return null; }
    }

    // ---------- Registry & pool ----------

    private static async Task RegistryScreenAsync()
    {
        while (true)
        {
            var sub = AnsiConsole.Prompt(new SelectionPrompt<string>()
                .Title($"{Ui.Icon("registry")} Registry & Parking Pool")
                .AddChoices("Show registry slots", "Rebuild registry (scan barcodes)", "Show pool", "Refresh pool metadata", "Back"));

            switch (sub)
            {
                case "Show registry slots":
                {
                    if (_registry.Slots.Count == 0) { AnsiConsole.MarkupLine("[dim]empty registry - run a rebuild after parking[/]"); break; }
                    var tbl = new Table().Border(TableBorder.Rounded).Title("registry.json")
                        .AddColumn("Game").AddColumn("Storage").AddColumn("Stored name").AddColumn("Original").AddColumn("Size").AddColumn("Status");
                    foreach (var s in _registry.Slots.OrderBy(s => s.StorageAppId).ThenBy(s => s.StoredName))
                        tbl.AddRow(s.GameAppId.ToString(), s.StorageAppId.ToString(), Markup.Escape(s.StoredName), Markup.Escape(s.OriginalName), HumanSize(s.Size), s.Status);
                    AnsiConsole.Write(tbl);
                    AnsiConsole.MarkupLine($"[dim]{SctRegistry.DefaultPath()}[/]");
                    break;
                }
                case "Rebuild registry (scan barcodes)":
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    _registry = PoolScanner.RebuildRegistry(_steamPath);
                    sw.Stop();
                    _registry.Save();
                    AnsiConsole.MarkupLine($"[green]{_registry.Slots.Count} slot(s) rebuilt in {sw.ElapsedMilliseconds}ms[/] - auto flow: the barcode tail-scan found them");
                    break;
                }
                case "Show pool":
                {
                    var tbl = new Table().Border(TableBorder.Rounded).Title("Parking pool (owned games NEVER selected)")
                        .AddColumn("Tier").AddColumn("App").AddColumn("Name").AddColumn("Free").AddColumn("Year").AddColumn("State").AddColumn("Note");
                    foreach (var p in PoolDb.DefaultPool.OrderBy(p => p.Tier).ThenBy(p => p.AppId))
                        tbl.AddRow(p.Tier.ToString(), p.AppId.ToString(), Markup.Escape(p.Name), p.IsFree ? "yes" : "no", p.ReleaseYear.ToString(), p.State.ToString(), Markup.Escape(p.Note));
                    AnsiConsole.Write(tbl);
                    break;
                }
                case "Refresh pool metadata":
                {
                    await AnsiConsole.Status().StartAsync("Querying store API (anonymous, read-only)...", async _ =>
                    {
                        await StoreApi.RefreshPoolAsync(PoolDb.DefaultPool.Select(p => p.AppId));
                    });
                    AnsiConsole.MarkupLine("[green]pool metadata refreshed[/]");
                    break;
                }
                default: return;
            }
        }
    }

    // ---------- Wipe ----------

    private static async Task WipeScreenAsync()
    {
        var targets = _buckets
            .SelectMany(b => b.Files.Select(f => (Bucket: b, f)))
            .ToList();
        if (targets.Count == 0) { AnsiConsole.MarkupLine("[dim]no local buckets[/]"); return; }

        var labels = targets.Select(t => $"{t.Bucket.AppId}/{t.f.FileName} ({HumanSize(t.f.FileSize)})").ToList();
        var pick = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("Choose file to wipe").AddChoices(labels));
        var chosen = targets[labels.IndexOf(pick)];

        await using var session = await ConnectSessionAsync();
        if (session is null) return;
        var rpc = new CloudRpcClient(session);
        var engine = new WipeEngine(_cfg);
        engine.Log += m => AnsiConsole.MarkupLine($"[dim]{Markup.Escape(m)}[/]");

        var blank = AnsiConsole.Confirm("Blank instead of delete?");
        AnsiConsole.MarkupLine(_cfg.DryRun ? "[yellow]DRY-RUN mode[/] - Settings to go live" : "[green]LIVE mode[/]");
        if (AnsiConsole.Confirm("Run?"))
        {
            var outcome = await engine.WipeAsync(rpc, chosen.Bucket.AppId, chosen.f.FileName, blank);
            AnsiConsole.MarkupLine(outcome.Success
                ? $"[green]OK[/] {outcome.Result}"
                : $"[red]FAIL[/] {outcome.Result}");
        }
    }

    // ---------- Guards ----------

    private static void GuardsScreen()
    {
        var sub = AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title("Guards")
            .AddChoices("Show", "Add appid", "Remove appid", "Back"));
        switch (sub)
        {
            case "Show":
                foreach (var g in _cfg.GuardedAppIds.OrderBy(x => x)) AnsiConsole.WriteLine(g.ToString());
                break;
            case "Add appid":
                _cfg.GuardedAppIds.Add(AnsiConsole.Ask<uint>("appid:"));
                _cfg.Save(ConfigPath);
                break;
            case "Remove appid":
            {
                var id = AnsiConsole.Ask<uint>("appid:");
                _cfg.GuardedAppIds.Remove(id);
                _cfg.Save(ConfigPath);
                break;
            }
        }
    }

    // ---------- Logon (QR / credentials) ----------

    private static async Task LogonScreenAsync()
    {
        var mode = AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title($"{Ui.Icon("qr")} Steam session logon")
            .AddChoices("QR code (Steam mobile app)", "Credentials (SCT_USER / SCT_PASS)", "Anonymous (read-limited)", "Back"));

        var session = new SteamSession();
        switch (mode)
        {
            case "QR code (Steam mobile app)":
            {
                var urlTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                session.ChallengeUrlChanged += u => urlTcs.TrySetResult(u);
                session.Event += msg => AnsiConsole.MarkupLineInterpolated($"[dim]{Markup.Escape(msg)}[/]");

                var connectTask = Task.Run(() => session.ConnectAsync(AuthMode.Qr));
                string url;
                try { url = await urlTcs.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
                catch (TimeoutException)
                {
                    AnsiConsole.MarkupLine("[red]no QR challenge received[/]");
                    await session.DisposeAsync();
                    return;
                }

                AnsiConsole.MarkupLine("[bold aqua]Scan this QR with the Steam mobile app:[/]");
                foreach (var line in QrRenderer.Render(url))
                    Console.WriteLine(line);
                AnsiConsole.MarkupLine($"[dim]or open: {Markup.Escape(url)}[/]");

                var ok = await AnsiConsole.Status()
                    .StartAsync("Waiting for the scan...", _ => connectTask);
                if (!ok)
                {
                    AnsiConsole.MarkupLine("[red]QR logon failed[/]");
                    await session.DisposeAsync();
                    return;
                }
                AnsiConsole.MarkupLine($"[green]{Ui.Icon("check")} logged on as {session.SteamId?.ConvertToUInt64()}[/]");
                break;
            }
            case "Credentials (SCT_USER / SCT_PASS)":
            {
                var user = AnsiConsole.Ask("username:", Environment.GetEnvironmentVariable("SCT_USER") ?? "");
                var pass = AnsiConsole.Prompt(new TextPrompt<string>("password:").Secret());
                session.Event += msg => AnsiConsole.MarkupLineInterpolated($"[dim]{Markup.Escape(msg)}[/]");
                var ok = await AnsiConsole.Status().StartAsync("Authenticating...", _ => session.ConnectAsync(AuthMode.Credentials, user, pass));
                if (!ok) { AnsiConsole.MarkupLine("[red]logon failed[/]"); await session.DisposeAsync(); return; }
                AnsiConsole.MarkupLine($"[green]{Ui.Icon("check")} logged on[/]");
                break;
            }
            case "Anonymous (read-limited)":
            {
                session.Event += msg => AnsiConsole.MarkupLineInterpolated($"[dim]{Markup.Escape(msg)}[/]");
                var ok = await AnsiConsole.Status().StartAsync("Connecting...", _ => session.ConnectAsync(AuthMode.Anonymous));
                if (!ok) { AnsiConsole.MarkupLine("[red]logon failed[/]"); await session.DisposeAsync(); return; }
                AnsiConsole.MarkupLine($"[green]{Ui.Icon("check")} anonymous session[/]");
                break;
            }
            default: return;
        }

        await session.DisposeAsync();
        AnsiConsole.MarkupLine("[dim]session discarded (session cookie remains useful via SCT_COOKIE for the web lane)[/]");
    }

    // ---------- Settings ----------

    private static void SettingsScreen()
    {
        var sub = AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title("Settings")
            .AddChoices("Toggle dry-run/live", "Steam path", "Owned appids", "Back"));
        switch (sub)
        {
            case "Toggle dry-run/live":
                _cfg.DryRun = !_cfg.DryRun;
                _cfg.Save(ConfigPath);
                AnsiConsole.MarkupLine($"dry-run: {( _cfg.DryRun ? "[green]on[/]" : "[red]off[/]")}");
                break;
            case "Steam path":
            {
                var p = AnsiConsole.Ask("path:", _cfg.SteamPathOverride ?? _steamPath);
                _cfg.SteamPathOverride = p;
                _cfg.Save(ConfigPath);
                RefreshLocal();
                break;
            }
            case "Owned appids":
            {
                var joined = string.Join(", ", _cfg.KnownOwnedAppIds.OrderBy(x => x));
                var input = AnsiConsole.Ask("comma-separated appids:", joined);
                try
                {
                    _cfg.KnownOwnedAppIds =
                        [.. input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(uint.Parse)];
                }
                catch { AnsiConsole.MarkupLine("[red]unparseable - unchanged[/]"); break; }
                _cfg.Save(ConfigPath);
                RefreshLocal();
                break;
            }
        }
    }

    // ---------- helpers ----------

    private static void Footer(string hint)
    {
        AnsiConsole.Write(new Rule($" [dim]{Markup.Escape(hint)}[/]").RuleStyle("grey"));
    }

    private static string HumanSize(long b) =>
        b switch
        {
            >= 1024L * 1024 * 1024 => $"{b / 1024d / 1024d / 1024d:F2} GiB",
            >= 1024L * 1024 => $"{b / 1024d / 1024d:F1} MiB",
            >= 1024 => $"{b / 1024d:F1} KiB",
            _ => $"{b} B",
        };

    private static string ShortSha(string? sha) => sha is { Length: > 8 } ? sha[..8] : (sha ?? "-");
}

/// <summary>Terminal glyphs: Unicode by default, ASCII with SCT_TUI_ASCII=1, Nerd-Font only when the font really has the glyphs.</summary>
public static class Ui
{
    private static readonly bool Ascii = Environment.GetEnvironmentVariable("SCT_TUI_ASCII") == "1";
    private static readonly bool Nerd = !Ascii && Environment.GetEnvironmentVariable("SCT_TUI_NERD") == "1";

    /// <summary>Returns the best glyph (Nerd Font glyph, else plain Unicode, else ASCII).</summary>
    public static string Icon(string name)
    {
        if (Nerd) return NerdGlyph(name);
        if (Ascii) return AsciiGlyph(name);
        return UniGlyph(name);
    }

    public static string Style(string body) => body; // future theming hook

    private static string NerdGlyph(string n) => n switch
    {
        // Font Awesome 5 codepoints (stable in Nerd Fonts)
        "folder" => "\uF07B",   // folder
        "cloud" => "\uF0C2",    // cloud
        "ferry" => "\uF21A",    // ship
        "park" => "\uF1F9",     // parking
        "wipe" => "\uF1F8",     // trash
        "registry" => "\uF03A", // list
        "shield" => "\uF3ED",   // shield-halved
        "gear" => "\uF013",     // gear
        "qr" => "\uF029",       // qrcode
        "check" => "\uF058",    // circle-check
        "x" => "\uF00D",        // xmark
        _ => "\uF0C9",          // bars
    };

    private static string UniGlyph(string n) => n switch
    {
        "folder" => "▣",
        "cloud" => "☁",
        "ferry" => "⛴",
        "park" => "🅿",
        "wipe" => "✂",
        "registry" => "▤",
        "shield" => "◆",
        "gear" => "⚙",
        "qr" => "▦",
        "check" => "✔",
        "x" => "✘",
        _ => "•",
    };

    private static string AsciiGlyph(string n) => n switch
    {
        "folder" => "[D]",
        "cloud" => "~C",
        "ferry" => "~F",
        "park" => "[P]",
        "wipe" => "[X]",
        "registry" => "[R]",
        "shield" => "[L]",
        "gear" => "[S]",
        "qr" => "[Q]",
        "check" => "OK",
        "x" => "[!]",
        _ => "[*]",
    };
}

/// <summary>Renders a QR challenge into terminal lines: Unicode half-block mode, ASCII ## fallback.</summary>
public static class QrRenderer
{
    public static List<string> Render(string text, bool ascii = false)
    {
        using var gen = new QRCoder.QRCodeGenerator();
        var data = gen.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
        var rows = data.ModuleMatrix; // List<BitArray>
        var size = rows.Count;
        var quiet = 2;
        var lines = new List<string>();

        bool Module(int x, int y) => x >= 0 && y >= 0 && x < size && y < size && rows[y][x];

        if (ascii)
        {
            for (var y = 0; y < size + quiet * 2; y++)
            {
                var sb = new System.Text.StringBuilder();
                for (var x = 0; x < size + quiet * 2; x++)
                    sb.Append(Module(x - quiet, y - quiet) ? "██" : "  ");
                lines.Add(sb.ToString());
            }
            return lines;
        }

        // Unicode half-blocks: two module rows per line
        for (var y = -quiet; y < size + quiet; y += 2)
        {
            var sb = new System.Text.StringBuilder();
            for (var x = -quiet; x < size + quiet; x++)
            {
                var top = Module(x, y);
                var bottom = Module(x, y + 1);
                sb.Append((top, bottom) switch
                {
                    (true, true) => "▀",
                    (true, false) => "▀",
                    (false, true) => "▄",
                    _ => " ",
                });
            }
            lines.Add(sb.ToString());
        }
        return lines;
    }
}