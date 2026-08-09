# SteamCloudSave.dll - one DLL, three mounting styles

Same engine everywhere: **shadow the game's RemoteStorage into a local folder** so it never
touches Steam's cloud, or **passthrough** to the real steam_api64.dll.

| Mounting style | How | Result |
|---|---|---|
| SHIM | rename `SteamCloudSave.dll` -> `steam_api64.dll` in the game folder (back up the real one first) | classic shadow redirect for a single game |
| gbe_fork | copy `SteamCloudSave.dll` into `<game>/steam_settings/load_dlls/` | gbe_fork auto-loads it via `LoadLibraryW`; the emulator keeps running the game, the shim stays dormant unless a shadowRoot is configured |
| OpenSteamTool | `[inject]` in `opensteamtool.toml` (`library_x64` / `library_x86`) targets the game process | same engine, mounted by OST |

Config: `steamcloudsave.cfg` (or env `SCT_SCS_CONFIG`) - see `steamcloudsave.cfg.EXAMPLE`.
Without `shadowRoot` the DLL is a passive passthrough (safe to pre-install).

Exports for other tools:
`SteamCloudSave_Init(configPath)`, `SteamCloudSave_Shutdown()`, `SteamCloudSave_State()`,
`SteamCloudSave_App()`, `SteamCloudSave_ShadowRoot()`.

Build: `.\build.ps1` (MinGW gcc; set `$env:GCC` if not on PATH).