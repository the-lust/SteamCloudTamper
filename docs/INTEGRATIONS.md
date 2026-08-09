# Integration matrix — how SteamCloudSave/SCT plugs into the ecosystem

| Tool | Lane | Status |
|---|---|---|
| **SCT CLI/TUI** | native | full (pool, park, ferry, barcode, registry, wipe, web) |
| **gbe_fork** | `steam_settings\load_dlls\SteamCloudSave.dll` (auto `LoadLibraryW`) | shipped (tools/steamcloudsave) |
| **OpenSteamTool** | `[cloud]` +CloudRedirect host, `[inject]` for SteamCloudSave.dll, Lua pool snippet | shipped (integrations/opensteamtool) |
| **SteamTools / SLS / GreenLuma** | same family: integrate the parking brain or ship SteamCloudSave.dll | code to copy (Core/Pool) |
| **online-fix.me** | their cracked steam_api64.dll is a steamclient-side shim; SteamCloudSave.dll can be load-listed or its exports copied | doc only |
| **SteamRE / SteamDatabase** | knowledge base feeding pool curation + docs | doc only |
| **DepotDownloader (anonymous mods)** | used by `pool refresh` to verify entitlement-agnostic download works for candidate slots | doc only |

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