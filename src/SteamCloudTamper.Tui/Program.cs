using Spectre.Console;
using SteamCloudTamper.Core;
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

    public static async Task<int> Main()
    {
        AnsiConsole.Write(
            new Panel(new Text("Steam Cloud Tamper - cloud save manager").Centered())
                .Header("[bold aqua]SCT[/]")
                .Border(BoxBorder.Rounded));

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
                    "[cyan]1[/] Buckets        - audit local userdata",
                    "[cyan]2[/] Remote         - list cloud buckets via Steam",
                    "[cyan]3[/] Ferry          - park saves into owned AppID 480 (Spacewar)",
                    "[cyan]4[/] Wipe           - delete/blank bucket files (dry-run by default)",
                    "[cyan]5[/] Guards         - never-touch appid list",
                    "[cyan]6[/] Settings       - dry-run, owned list, steam path",
                    "[cyan]7[/] Quit"));

            switch (choice[0])
            {
                case '1': BucketsScreen(); break;
                case '2': await RemoteScreenAsync(); break;
                case '3': await FerryScreenAsync(); break;
                case '4': await WipeScreenAsync(); break;
                case '5': GuardsScreen(); break;
                case '6': SettingsScreen(); break;
                default: return 0;
            }
        }
    }

    private static string MenuTitle()
        => $"Steam: [cyan]{_steamPath}[/] | accounts: [yellow]{_accounts.Count}[/] | owned: [yellow]{_cfg.GetOwnedSet().Count}[/] | buckets: [yellow]{_buckets.Count}[/] | mode: [{( _cfg.DryRun ? "green" : "red")}]{(_cfg.DryRun ? "dry-run" : "LIVE")}[/]";

    private static void RefreshLocal()
    {
        _accounts = SteamLocator.ListAccounts(_steamPath);
        var audit = new AuditEngine(_cfg);
        _buckets = audit.ListLocal(_steamPath, owned: _cfg.GetOwnedSet());
    }

    private static async Task<SteamSession?> ConnectSessionAsync()
    {
        var session = new SteamSession();
        session.Event += msg => AnsiConsole.MarkupLineInterpolated($"[dim]{Markup.Escape(msg)}[/]");

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
            AnsiConsole.MarkupLine("[red]Steam logon failed[/] - set SCT_USER/SCT_PASS or SCT_AUTH_MODE=qr.");
            return null;
        }
        return session;
    }

    // ---------- Buckets ----------

    private static void BucketsScreen()
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("Local buckets (userdata)")
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
        AnsiConsole.WriteLine("(back to menu: enter)");
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
                    AnsiConsole.MarkupLine($"[dim]quota: {quota.ExistingBytes}/{quota.MaxBytes}b, {quota.ExistingFiles}/{quota.MaxFiles} files[/]");
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
                        .AddColumn("Name").AddColumn("Size").AddColumn("Time");
                    foreach (var f in files)
                        tbl.AddRow(Markup.Escape(f.FileName), HumanSize(f.FileSize), f.Timestamp.ToString());
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
                _cfg.KnownOwnedAppIds =
                    [.. input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(uint.Parse)];
                _cfg.Save(ConfigPath);
                RefreshLocal();
                break;
            }
        }
    }

    private static string HumanSize(long b) =>
        b switch
        {
            >= 1024 * 1024 * 1024 => $"{b / 1024d / 1024d / 1024d:F2} GiB",
            >= 1024 * 1024 => $"{b / 1024d / 1024d:F1} MiB",
            >= 1024 => $"{b / 1024d:F1} KiB",
            _ => $"{b} B",
        };

    private static string ShortSha(string? sha) => sha is { Length: > 8 } ? sha[..8] : (sha ?? "-");
}