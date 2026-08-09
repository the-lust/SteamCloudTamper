# OpenSteamTool + SteamCloudSave — making the Steam GUI "satisfied"

OpenSteamTool already has the two slots SCT needs (check opensteamtool.example.toml on GitHub):

1. `[cloud]` — loads `cloud_redirect.dll` inside Steam and registers **every addappid() game**
   as a redirected app. The Steam client then believes unlocked titles have a working cloud:
   the GUI shows the cloud icon, sync events are driven by CloudRedirect, and all data lives
   in CloudRedirect's provider (Google Drive / OneDrive / **local folder**).
   This is the only realistic way to make the client GUI happy for unowned titles —
   the UFS server denies uploads at the account level, no local spoof can change that.

2. `[inject]` — `library_x64` / `library_x86` loaded into the target game process:
   the mounting point for `SteamCloudSave.dll`.

## Two-step setup (copy config files, never edit Steam's own config)

### Step 1 — SteamCloudSave.dll as the game-process shadow (optional, per game)
```
[inject]
enabled = true
library_x64 = "D:\tools\SteamCloudSave\x64\SteamCloudSave.dll"
library_x86 = "D:\tools\SteamCloudSave\x86\SteamCloudSave.dll"
```
The DLL reads `steamcloudsave.cfg` beside itself (steamPath / shadowRoot / appid).

### Step 2 — GUI cloud for unlocked games via CloudRedirect + SCT local provider
1. Install CloudRedirect, point its provider at a **local folder** (e.g. `D:\sct_provider`).
2. Add `[cloud]` to `opensteamtool.toml` (see `opensteamtool.toml.sct` in this folder).
3. Let SCT manage that provider folder as a parking zone:
   - `sct ferry dl <name> <out>` / `sct ferry upload <file>` move saves between
     the 480 bucket and the provider folder.
   - `sct rebuild` keeps `registry.json` in sync with any barcode-tagged parked files,
     wherever they live (480 remote, provider folder, userdata).

SCT works with CloudRedirect rather than reimplementing its steamclient patches:
both were built for the same war, different flanks.

## What SCT does NOT do
- It never edits `opensteamtool.toml` or Steam config on disk by itself
  (this repo generates config files you place yourself).
- It cannot make the server accept uploads for unowned appids. That is a Valve-side
  entitlement check (patched ~April 2025); only owned buckets or the 480/any free-game
  buckets accept writes.