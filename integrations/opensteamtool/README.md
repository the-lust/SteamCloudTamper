# OpenSteamTool + CloudRedirect — making the Steam GUI show "synced"

Live-verified 2026-08-10 on Steam client build 1785799196:
Steam's own `cloud_log.txt` for the previously-denied app now ends with
`Upload complete, result OK` (was `Upload Access Denied` every sync since
Valve's Apr-2025 entitlement patch), the client accepted the new change
number in `remotecache.vdf`, and the bytes live in a local folder provider.

Two lanes, two binaries:

1. `[cloud]` — OST hosts `cloud_redirect.dll` (v2.6.4) inside Steam. Every
   `addappid()` game (Dead Cells 588650, cloud-test 1132xx, 1623730, ...)
   becomes a **redirected app**: Cloud.* RPCs are answered locally, so the
   Steam GUI shows the cloud icon / properties → "cloud synced", and no UFS
   request ever leaves the machine.
2. `[inject]` — optional game-process mounting point for `SteamCloudSave.dll`
   (off by default: that DLL is a steam_api64-replacement shim for
   depot-level integration, not a blind process injection).

## Install (what is deployed on this machine right now)

```
D:\Steam\dwmapi.dll           OpenSteamTool build (Release, from source)
D:\Steam\xinput1_4.dll        OpenSteamTool build (Release, from source)
D:\Steam\OpenSteamTool.dll    OpenSteamTool build (Release, from source)
D:\Steam\cloud_redirect.dll   CloudRedirect v2.6.4 (github.com/Selectively11/CloudRedirect)
D:\Steam\opensteamtool.toml   [cloud] enabled, library = "cloud_redirect.dll"
%AppData%\CloudRedirect\config.json
D:\sct_provider               folder provider root (CR metadata + content blobs)
```

### 1. Build OpenSteamTool from source — the release lags the [cloud] feature

The latest tagged release (v1.4.8, 2026-06-13) predates PR #138 "Added
CloudRedirect support" (merged 2026-06-25). You MUST build `main`:

```bat
git clone https://github.com/OpenSteam001/OpenSteamTool
cd OpenSteamTool
:: in a VS2022 x64 dev prompt (VsDevCmd.bat -arch=amd64)
set CONFIGS=Release
build.bat
:: copy build\Release\{dwmapi.dll, xinput1_4.dll, OpenSteamTool.dll} to D:\Steam\
```

Verify the built DLL contains the host (it exports nothing new; instead
watch for `CloudRedirectHost` activity): with Steam running, `D:\Steam\cloud_redirect.log`
must appear after a Cloud sync attempt.

### 2. CloudRedirect provider config — local folder, no token

`%AppData%\CloudRedirect\config.json` (the DLL reads this path itself,
see `src/common/cli.cpp` `GetConfigDir()`):

```json
{
  "provider": "folder",
  "sync_path": "D:\\sct_provider"
}
```

Provider layout (verified): `{sync_path}/{accountId}/{appId}/blobs/{filename}`
(content-addressed blobs) plus `cn.cloudredirect`, `file_tokens.cloudredirect`,
`state.cloudredirect` metadata. Folder provider needs no OAuth; `local` is
local-only, `folder` is what SCT uses.

### 3. OST config

`D:\Steam\opensteamtool.toml` (hot-reloaded):

```toml
[cloud]
enabled = true
library = "cloud_redirect.dll"

[inject]
enabled = false
# library_x64 = "D:\\steam-cloud-mod\\tools\\steamcloudsave\\SteamCloudSave.dll"
# library_x86 = "D:\\steam-cloud-mod\\tools\\steamcloudsave\\SteamCloudSave.dll"
```

### 4. Restart Steam, verify

Steam's log `D:\Steam\logs\cloud_log.txt` for the redirected app changes from

```
[AppID 588650] Upload Access Denied for file user_0.dat
[AppID 588650] Upload complete, result Access Denied
```

to

```
[AppID 588650] HTTP upload for file 'user_0.dat' (offset=0, length=72016) to (127.0.0.1 ... path /upload/1201110076/588650/user_0.dat - success.
[AppID 588650] Upload OK for file user_0.dat
[AppID 588650] Upload complete, result OK
```

The `127.0.0.1:<port>` target is CloudRedirect's local HTTP server; the
client's `remotecache.vdf` then records the accepted change number, and the
GUI shows the app as cloud-synced (properties → "View files" lists the
uploaded files).

## SCT + the provider folder

- SCT's parked saves stay where they were parked (barcode-tagged in the
  480/113200 buckets); the provider folder is Steam's *view* of the cloud
  for redirected apps. `sct rebuild` / `sct scan` cover the provider root as
  well, so any barcode-tagged file there is also tracked.
- `sct ferry` moves saves between the 480 bucket and any folder, including
  the provider.

## What SCT does NOT do

- It never edits `opensteamtool.toml` or Steam config by itself (this repo
  generates config files you place yourself).
- It cannot make Valve's server accept uploads for unowned appids; the
  local-provider redirect is the GUI-satisfied path, the parked buckets are
  the durable archive path.
