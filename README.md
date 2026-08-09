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
- `tools/SteamCloudTamper.ApiProbe` - reflection dump of SteamKit2 API surface (dev aid)
- `tools/steamcloudsave` - `SteamCloudSave.dll`: steam_api64 shim + RemoteStorage shadow lane (mount: shim / gbe `load_dlls` / OST `[inject]`)
- `integrations/opensteamtool` - OST toml merge snippet, Lua pool snippet, GUI-satisfaction guide (CloudRedirect)
- `tests/SteamCloudTamper.Core.Tests` - unit tests (barcode, pool, allocator, registry)

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

parking brain:
    pool list | refresh        curated parking-slot pool (owned games NEVER selected)
    park <uid3> <gameAppId> [--force] [--offline]
                               barcode-park local bucket into best hidden slot
    unpark <storageAppId> <name> [outdir]     download + strip barcode trailer
    rebuild                    tail-scan userdata -> registry.json (fast: 1000 files < 1s)
    barcode <file> | barcode make <payload>   show/render barcode trailers

web lane (needs SCT_COOKIE session cookie):
    web ls | files <appid> | dl <appid> <file> [outfile]

ferry (park saves into owned AppID 480 / Spacewar bucket):
    ferry ls | upload <local-file> [name] | dl <name> [outfile]
```

TUI (interactive manager): `dotnet run --project src/SteamCloudTamper.Tui`
- 9 screens: Buckets / Remote / Ferry / **Park smart** / Wipe / **Registry&Pool** / Guards / Settings / **Logon (QR)**
- QR login renders the challenge as a live in-terminal QR (Unicode half-blocks, ASCII fallback)
- Icons: Unicode by default; `SCT_TUI_ASCII=1` ASCII; `SCT_TUI_NERD=1` Nerd-Font glyphs

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

1. Tier: hidden/dev apps > old free apps; **owned-game buckets are never selected**.
2. Co-existence wins - a bucket already hosting other parked games is preferred
   (multiple saves per AppID, names carry `<origAppId>_` prefixes).
3. Name collision or quota pressure -> next deterministic candidate.

Auth: anonymous by default (read-limited for unowned buckets; **uploads are denied to
anonymous even for 480** - a `park`/`ferry upload` needs a real session:
`SCT_USER`/`SCT_PASS` or `SCT_AUTH_MODE=qr`).

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
`steam://open/console` commands like `download_depot <AppID>` or any `settingcloudaudit` do NOT exist / do not do what the casual writeups claim (`download_depot` fetches game files, unrelated to cloud saves). The only real per-bucket client switches are: Steam settings > Cloud per-app toggles (owned apps), the lockfile above, and CloudRedirect hiding.

## Research notes (why these lanes exist)

- **760 pollution confirmed by multiple RE projects**: SteamTools rewrote cloud requests for non-owned games to AppID 760 (Screenshots) without per-game prefixes - so saves collide across games and get mirrored into each injected app's userdata. STFixer/CloudRedirect and this repo all started from the same mess.
- **Valve patch (Apr 2025)**: cloud UFS for non-owned AppIDs now returns `AccessDenied` on enumerate/upload/delete. Existing tests confirmed: even enumeration is denied.
- **Retail SteamCloudFileManager**: deletes via web/ISteamRemoteStorage are physically rejected server side for special internal appids (e.g. 760/7); they resort to CDP-hijacked read-only web sessions for those. This is why the web lane here is read-only by design.
- **Old "conflict dialog" trick** (zero-out files, delete remotecache.vdf, resume the conflict dialog with "upload nothing"): predates the 2025 patch; only really viable for owned games.
- Active lanes for stuck unowned buckets: web lane (read/backup), ferry park (480 or any hidden slot), barcode park (pool allocator), local lockout/relocate, hook shim, CloudRedirect via OST `[cloud]`, and Steam Support request. The app-emulator (gbe_fork) build ships with a `load_dlls` mount for SteamCloudSave.dll.