# SteamCloudTamper

Steam cloud save auditor and remover - focusing on old cloud saves left behind by SteamTools (rerouted to AppID 760), GreenLuma/SLS-era per-app uploads, and owned-title cloud buckets.

Reasons to exist: Valve added (approx. April 2025) a server-side restriction that **denies uploads to games you do not own** (`EnumerateUserFiles` also returns `AccessDenied` for unowned apps). Saves uploaded before that are still there, and deletability needs to be probed per-account.

## Goal

Find every cloud bucket that references stale usernames/configs, probe what Steam still allows (enumerate / upload / delete), then either delete the bucket or blank its contents so the client stops syncing old data.

## Project layout

- `src/SteamCloudTamper.Core` - models (Era, Bucket, Policy...), VDF parser/writer + `remotecache.vdf` generator, root-path map, Steam install/account discovery, config
- `src/SteamCloudTamper.Core/Pool` - **the parking brain**: barcode trailer lane, curated pool DB, mapping registry (registry.json), smart allocator, tail-scan rebuild
- `src/SteamCloudTamper.Engines` - SteamSession (anonymous / credentials+Guard / QR login), CloudRpcClient (cloud UFS via unified messages), AuditEngine (local+remote merge), WipeEngine, LocalInjectEngine with lock/relocate
- `src/SteamCloudTamper.Cli` - CLI commands
- `src/SteamCloudTamper.Tui` - Spectre.Console interactive manager (buckets/remote/ferry/park/wipe/registry/guards/settings/QR logon) with Unicode/Nerd/ASCII icon sets + live in-terminal QR
- `src/SteamCloudTamper` - **the single binary**: `SteamCloudTamper.exe` - no args + console = TUI, flags = CLI (dispatches into the Cli/Tui projects; nothing deleted)
- `tools/publish.ps1` - builds ONE self-contained single-file exe into `dist/` (gitignored; no .NET runtime needed, double-click = TUI)
- `tools/steamcloudsave` - `SteamCloudSave.dll`: steam_api64 shim + RemoteStorage shadow lane (mount: shim / gbe `load_dlls` / OST `[inject]`)
- `integrations/opensteamtool` - OST toml merge snippet, Lua pool snippet, GUI-satisfaction guide (CloudRedirect)
- `tests/SteamCloudTamper.Core.Tests` - unit tests (barcode, pool, allocator, registry, discovery)

## Commands

```
detect                  locate Steam + accounts + libraries
scan                    audit local userdata buckets (per account/app) + barcode tags
remote-list --app <id>  list files in a cloud bucket
probe <appid...>        check what the backend allows: enumerate / upload / delete
wipe <appid> <file> [--blank] [--force]      delete or blank one cloud file
wipe-all <appid> [--blank] [--force]         wipe an entire bucket
guards add|rm|ls <appid>   maintained never-touch list (steamcloudtamper.json)
inject <uid3> <appid> <file> [remote-name]   local user drop + remotecache.vdf regen
lock/unlock <uid3> <appid>                    read-only file blocks Steam folder re-creation
relocate/unrelocate <uid3> <appid>            junction-isolate bucket into SCT stash

parking brain (anti-ban: private cloud saves of real apps only, never public flooding):
pool list | refresh           curated parking-slot pool (owned games NEVER selected)
                pool discover [--net]         sweep the machine for container AppIDs (PoolDb +
                                              userdata buckets + OST lua hooks + CloudRedirect host
                                              + SLS/Goldberg steam_settings + GreenLuma whitelists),
                                              posture + AutoClouded per container, snapshot saved
                                              to the registry; --net also flags AutoClouded from
                                              cloud_log
                pool probe [--uid <id3>] [--force] [--wait-sec N]
                                  one-private-file writability probe through the RUNNING
                                  Steam client (no logon); verdicts saved to the registry
    park <gameAppId> [--uid <id3>] [--force] [--lane auto|client|rpc|stage] [--bucket <appid>]
         [--spread N] [--copies N] [--stealth] [--wait-sec N]
                                  auto   = Steam up + account active -> client; else rpc
                                  client = stage locally; the signed-in Steam session uploads
                                           (real UFS or CR provider, per app posture)
                                  rpc    = SCT logs on (SCT_USER/SCT_PASS or QR) and uploads
                                           directly - the ONLY real upload for buckets the
                                           client does not AutoCloud (e.g. 480)
                                  stage  = drop files locally only; session syncs on its own
                                  --bucket <appid> pins every file to one explicit slot
    client status | sync <appid> [--down] | tell <command>
                                  client lane: status / force-sync one bucket / raw console
                                  (console input skipped on client builds where it is blocked)
    provider status | init [sync-dir] | ls [--uid <id3>] [--app <appid>]
                                  CloudRedirect folder-provider management (SCT owns the config)
    unpark <storageAppId> <name> [outdir]     download + strip barcode trailer
    rebuild                    tail-scan userdata -> registry.json (fast: 1000 files < 1s)
    barcode <file> | barcode make <payload>   show/render barcode trailers

web lane (needs SCT_COOKIE session cookie):
    web ls | files <appid> | dl <appid> <file> [outfile]

ferry (park saves into owned AppID 480 / Spacewar bucket):
    ferry ls | upload <local-file> [name] | dl <name> [outfile]
```

Single binary: `dist\SteamCloudTamper.exe` (TUI with no args on a console; CLI with flags;
self-contained, no .NET needed - `tools/publish.ps1` builds it).
Run from the repo: `dotnet run --project src/SteamCloudTamper -- <command>`
(net10 SDK at `C:\Users\kaneki\dotnet10`, or set `DOTNET_ROOT` to it for the published exe).

TUI (interactive manager): `dotnet run --project src/SteamCloudTamper.Tui`
- 9 screens: Buckets / Remote / Ferry / **Park smart** / Wipe / **Registry&Pool** / Guards / Settings / **Logon (QR)**
- Registry screen gained "Show discovered containers" (the smart-appid universe)
- QR login renders the challenge as a live in-terminal QR (Unicode half-blocks, ASCII fallback)
- Icons: Unicode by default; `SCT_TUI_ASCII=1` ASCII; `SCT_TUI_NERD=1` Nerd-Font glyphs
- Effects: gradient/glow/sine polish; `SCT_TUI_FLAT=1` disables every effect

## Smart appid containers (discovery)

`pool discover` builds the **container universe** - every AppID SCT may park unowned-game
saves into - and saves it into the registry (`registry.json` -> `Discovered`). Sources:

- PoolDb curated slots (Spacewar 480, Steam Client 7, SteamVR tools, free games, mod hosts)
- real `userdata/<uid>/<appid>` buckets (owned-library games)
- OST Lua `addappid` hooks (`config/lua/*.lua`; e.g. this machine: 113200, 1623730, 3164500,
  3722330, 588650 + bump variants) - these never touch Valve
- CloudRedirect host marker (`opensteamtool.toml [cloud]` + dll) - posture `provider`
- SLS/Goldberg `steam_settings/appid.txt` and GreenLuma `appidwhitelist.txt` (auto-probed,
  skipped when absent)

Each container carries `kind` (owned / free / hidden / modhost / activation), `source`,
`posture` (real / provider / redirected) and - with `--net` - whether the client itself
AutoClouds it (read from `logs/cloud_log.txt`). Posture ranks where uploads land: a
lua-hooked bucket is `redirected` (never Valve) and is disfavored for the RPC lane;
real Valve-touching containers are the ones that matter for durable storage. Owned-game
buckets appear in the universe but parking still never selects them without explicit
user consent. The TUI shows the same screen: Registry -> "Show discovered containers".

## The barcode lane (what makes parking self-describing)

Parked save files carry a trailer (`SCTB1` magic + CRC32 + payload):

```
<original-game-appid>|<steam-userid3>|<DDMMYYYY>      e.g. 588650|1201110076|09082026
```

- The **storage appid is never in the payload** - the bucket you are in IS the storage.
- On a fresh install with no local info, `rebuild` tail-scans every bucket file (reads last
  4KB only) and recreates the mapping in seconds - even for 1000+ files.
- Unparking strips the trailer - the original save comes back byte-identical.
- All lanes (CLI, TUI, SteamCloudSave.dll, OST/gbe register views) read the same
  `%LOCALAPPDATA%\SCT\registry.json`.

## Parking allocator rules (in order)

1. Tier: hidden/dev apps, Valve tools, mod hosts > old free apps; **owned-game buckets are never selected** (Tier 3 hard-excluded).
2. Anti-ban hardening: server-`Denied` slots (from `pool probe`) are skipped; `--spread` fans files across several real-app buckets; `--copies` mirrors them so one purged slot never loses a save; `--stealth` uses hashed names that look native (the barcode trailer still identifies the file).
3. Co-existence wins - a bucket already hosting other parked games is preferred
   (multiple saves per AppID, names carry `<origAppId>_` prefixes).
4. Name collision or quota pressure -> next deterministic candidate.

## Using the running Steam session (no SCT login)

`SteamLocator` detects the signed-in account via `config/loginusers.vdf` (ActiveUser →
AutoLogin → most recent). When Steam is running with that account:

- `park`/`pool probe` default to the **client lane**: files are staged into the slot
  buckets locally, the real Steam client owns the upload, and `CloudLogWatcher` reads
  the verdict from `logs/cloud_log.txt` (`Upload complete, result OK` / `Access Denied`).
  The TUI header shows the live session (e.g. `session: ✔ 1201110076 (Steam running)`).
- **AutoCloud reality check (verified 2026-08-10 on this client)**: Steam only evaluates
  buckets it manages itself - on this machine that is AppID 7 (Steam client config,
  real UFS, synced to ChangeNumber 65), 588650 (now CloudRedirect-local), and the
  installed games. **Spacewar 480 and the cloud test app 113200 are NEVER AutoClouded**,
  so a staged 480 file gets no client tick and the verdict stays "Unknown" - the parked
  copies are verified locally by `rebuild`'s barcode tail-scan, and for a REAL server
  upload of 480/113200 the lane must be `--lane rpc` (SCT logs on as the account).
- **Posture tracking**: every confirmed registry slot records where its upload landed -
  `real` (Valve UFS), `provider` (CloudRedirect folder), `redirected` (OST lua hook) or
  `local` (staged only). `SteamLocator.SyncPosture()` re-derives it live; the TUI
  registry screen shows the column.
- **Steam Console caveat**: `steam://open/console` is blocked on newer client builds
  (verified: no console window even with `-console`; the page does not open). SCT skips
  it and waits on the AutoCloud tick; `client tell` still sends raw commands on builds
  where the console exists. The console-free real-upload path is the RPC lane.

## Steam GUI shows "synced" (OST + CloudRedirect, live-verified 2026-08-10)

For unowned games (Dead Cells 588650 etc.) the Steam client kept re-attempting the
upload and the library showed a cloud sync error. Fix, verified end-to-end:

- **OpenSteamTool built from `main`** (v1.4.8 release predates the `[cloud]` host,
  see `integrations/opensteamtool/OST-HOST-BUILD.md`) + **CloudRedirect v2.6.4**
  (`cloud_redirect.dll`) in `D:\Steam\`, `[cloud] enabled=true` in `opensteamtool.toml`.
- CloudRedirect provider = **local folder** (`%AppData%\CloudRedirect\config.json`
  → `D:\sct_provider`); every `addappid()` game becomes a redirected app whose
  Cloud.* RPCs are answered locally (127.0.0.1 HTTP server).
- Result in `logs/cloud_log.txt` for 588650:
  `HTTP upload ... path /upload/1201110076/588650/user_0.dat - success.` →
  `Upload complete, result OK` (previously `Access Denied` every sync), the client
  accepted the change number in `remotecache.vdf`, and the bytes are stored at
  `D:\sct_provider\1201110076\588650\blobs\`. Steam UI shows cloud synced with the
  files listed; the SCT barcode-tagged parked copies in 480/113200 are untouched.

Auth: anonymous by default (read-limited for unowned buckets; **uploads are denied to
anonymous even for 480** - the RPC lane (`--rpc`, `ferry upload`, `remote-list`) needs a
real session: `SCT_USER`/`SCT_PASS` or `SCT_AUTH_MODE=qr`).

Run: `dotnet run --project src/SteamCloudTamper.Cli -- <command>` (needs .NET 10 SDK).

## Wipe reality check
Valve now denies uploads to unowned games (server-side, approx. April 2025) - enumeration of unowned buckets returns `AccessDenied` even logged in. So:

1. `remote-list --app <id>` - confirm the bucket exists and which files
2. `probe` - what does the backend allow for your account: enumerate / upload / delete?
3. `wipe <id> <file>` - try delete, then blank-overwrite; the result tells you what's possible

For things Valve simply refuses to remove, drop to local isolation (safest: these don't touch servers):

## Local isolation strategies (for locked, unremovable UFS buckets)

### Strategy 1 - CloudRedirect nullify
If CloudRedirect is deployed and keeps pulling an old remote bucket, don't point it at the real `userdata/<uid>/<appid>` - configure a redirect target to an empty private folder (e.g. `%localappdata%\sct\isolated\<app>`), so the game never sees Steam's copy again. (CloudRedirect is out of scope here, this repo does server api + local files.)

### Strategy 2 - lockfile bucket blocker (implemented as `lock`)
Windows won't create a folder where a file with the exact same name exists. `lock <uid3> <appid>`:
1. (with `--force`) backs up and deletes `userdata/<uid>/<appid>`
2. creates a read-only file `userdata/<uid>/<appid>` (content `SCT_LOCKOUT`)
3. Steam then fails cloud sync re-creation silently - no server changes, no data loss beyond the local backup.

`unlock <uid3> <appid>` removes the blocker. Guarded appids are skipped automatically.

### Strategy 4 - junction isolation (implemented as `relocate`)
`relocate <uid3> <appid>`: moves `userdata/<uid>/<appid>` into `%LOCALAPPDATA%\SCT\stash\<appid>` and replaces the folder with a directory junction. Steam reads/writes through the junction into the stash - the old bucket is physically gone from userdata, no admin needed. `unrelocate` re-links to the stash, or `unrelocate --restore <appid>` pulls it back.

### Strategy 5 - hook lane (implemented as `SteamCloudSave.dll` in `tools/steamcloudsave`)
One DLL, three mounting styles (identical engine): renamed to `steam_api64.dll` (shim),
dropped into `<game>\steam_settings\load_dlls\` (gbe_fork auto-load), or loaded via
OpenSteamTool's `[inject]`. Config `steamcloudsave.cfg`:

```
steamPath=D:\Steam
shadowRoot=D:\sct_shadow
app=91330
```

When `app` matches, all ISteamRemoteStorage calls for that game read/write only under
`D:\sct_shadow\<appid>\` (shadow), so the game never touches Steam's buckets or userdata
for it. Every other call forwards to the real steam_api64. The GUI-"satisfaction" path
for unlocked games (client-side cloud icon + sync) is OpenSteamTool's `[cloud]` slot
hosting CloudRedirect over a local provider folder - see `integrations/opensteamtool/`.

### Strategy 3 - "console tricks"
`steam://open/console` commands like `download_depot <AppID>` or any `settingcloudaudit` do NOT exist / do not do what the casual writeups claim (`download_depot` fetches game files, unrelated to cloud saves). Verified on this client (2026-08-10): `steam://open/console` does not even open the console anymore - the only real per-bucket client switches are: Steam settings > Cloud per-app toggles (owned apps), the lockfile above, and CloudRedirect hiding. SCT's `client sync` therefore relies on the AutoCloud tick, and the deterministic real upload is the RPC lane.

## Research notes (why these lanes exist)

- **760 pollution confirmed by multiple RE projects**: SteamTools rewrote cloud requests for non-owned games to AppID 760 (Screenshots) without per-game prefixes - so saves collide across games and get mirrored into each injected app's userdata. STFixer/CloudRedirect and this repo all started from the same mess.
- **Valve patch (Apr 2025)**: cloud UFS for non-owned AppIDs now returns `AccessDenied` on enumerate/upload/delete. Existing tests confirmed: even enumeration is denied.
- **Retail SteamCloudFileManager**: deletes via web/ISteamRemoteStorage are physically rejected server side for special internal appids (e.g. 760/7); they resort to CDP-hijacked read-only web sessions for those. This is why the web lane here is read-only by design.
- **Old "conflict dialog" trick** (zero-out files, delete remotecache.vdf, resume the conflict dialog with "upload nothing"): predates the 2025 patch; only really viable for owned games.
- Active lanes for stuck unowned buckets: web lane (read/backup), ferry park (480 or any hidden slot), barcode park (pool allocator with spread/copies/stealth), client lane (staged via the running Steam session), local lockout/relocate, hook shim, CloudRedirect via OST `[cloud]`, and Steam Support request. The app-emulator (gbe_fork) build ships with a `load_dlls` mount for SteamCloudSave.dll.
- **No public/anonymous flooding, ever**: every probe/park write is a single small private file inside a real app's cloud bucket (Spacewar, SteamVR tools, cloud test app, free-game buckets the account actually owns). The SteamTools-era 760 mass-dump pattern is what got cloud UFS locked down - SCT deliberately does the opposite (many slots, real apps, quiet writes).