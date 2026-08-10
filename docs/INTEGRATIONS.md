# Integration matrix — how SteamCloudSave/SCT plugs into the ecosystem

| Tool | Lane | Status |
|---|---|---|
| **SCT CLI/TUI** | native | full (pool, park, ferry, barcode, registry, wipe, web) |
| **SCT client lane** | running Steam session (no login): stage locally + `CloudLogWatcher` verdict; AutoCloud tick only (Steam Console blocked on newer builds); non-AutoCloud buckets (480/113200) need `--lane rpc` for real uploads | full |
| **SCT posture tracker** | per-slot `real` / `provider` / `redirected` / `local` in registry.json, derived live by `SteamLocator.SyncPosture()` (OST lua hook, CR toml/dll, else Valve) | full |
| **gbe_fork** | `steam_settings\load_dlls\SteamCloudSave.dll` (auto `LoadLibraryW`) | shipped (tools/steamcloudsave) |
| **OpenSteamTool** | `[cloud]` +CloudRedirect host, `[inject]` for SteamCloudSave.dll, Lua pool snippet — **GUI-synced lane, live-verified** (build from `main`, see OST-HOST-BUILD.md) | shipped (integrations/opensteamtool) |
| **CloudRedirect** | provider engine: local-folder provider (`%AppData%\CloudRedirect\config.json` → `D:\sct_provider`) answers Cloud.* RPCs locally (127.0.0.1), so the Steam GUI shows "synced" for every `addappid()` game — no UFS uploads ever | shipped (v2.6.4) |
| **SteamTools / SLS / GreenLuma** | same family: integrate the parking brain or ship SteamCloudSave.dll | code to copy (Core/Pool) |
| **online-fix.me** | their cracked steam_api64.dll is a steamclient-side shim; SteamCloudSave.dll can be load-listed or its exports copied | doc only |
| **Millennium (SteamClientHomebrew)** | plugins are **JS/CSS for the Steam CEF UI**, not native DLLs — SCT integrates at the data layer (registry.json read by a tiny plugin) or via the `SteamCloudSave.dll` lanes; there is no native plugin slot | doc only |
| **SteamRE / SteamDatabase** | knowledge base feeding pool curation + docs | doc only |
| **DepotDownloader (anonymous mods)** | used by `pool refresh` to verify entitlement-agnostic download works for candidate slots | doc only |

## Anti-flooding rule (all lanes)

SCT never mass-uploads, never uses public/anonymous dumps (the SteamTools-760 pattern
is what got cloud UFS locked down). Every write is ONE small private file in a REAL
app's cloud bucket — chosen by `PoolDb` (Spacewar, SteamVR tools, cloud test app,
free-game buckets, mod hosts), verified per-account by `pool probe` (client-lane
verdict from `cloud_log.txt`), and spread/mirrored via `--spread`/`--copies` so no
single slot ever carries a detectable pattern.

## The shared truth
All lanes read/write the SAME files:
- `%LOCALAPPDATA%\SCT\registry.json` — game -> storage slot map (read anytime, `sct rebuild`)
- barcode trailers in parked saves (`SCTB1` + `gameAppId|uid3|DDMMYYYY` + CRC32) —
  any lane can re-derive the map from the data itself
- `steamcloudsave.cfg` — per-DLL config (steamPath / shadowRoot / appid)

## Naming contract (all lanes, identical casing)
- DLL: `SteamCloudSave.dll`, public API `SteamCloudSave_Init/Shutdown/State/App/ShadowRoot`
- Registry file: `registry.json` (magic `SCTREG1`)
- Trailer magic: `SCTB1`, payload `<gameAppId>|<uid3>|<DDMMYYYY>`
- C# code: PascalCase public API, `SteamCloudSave` name in every project file name