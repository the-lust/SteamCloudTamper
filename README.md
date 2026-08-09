# SteamCloudTamper

Steam cloud save auditor and remover - focusing on old cloud saves left behind by SteamTools (rerouted to AppID 760), GreenLuma/SLS-era per-app uploads, and owned-title cloud buckets.

Reasons to exist: Valve added (approx. April 2025) a server-side restriction that **denies uploads to games you do not own** (`EnumerateUserFiles` also returns `AccessDenied` for unowned apps). Saves uploaded before that are still there, and deletability needs to be probed per-account.

## Goal

Find every cloud bucket that references stale usernames/configs, probe what Steam still allows (enumerate / upload / delete), then either delete the bucket or blank its contents so the client stops syncing old data.

## Project layout (M1 - SteamKit2 CLI)

- `src/SteamCloudTamper.Core` - models (Era, Bucket, Policy...), VDF parser/writer + `remotecache.vdf` generator, root-path map, Steam install/account discovery, config
- `src/SteamCloudTamper.Engines` - SteamSession (anonymous or SCT_USER/SCT_PASS), CloudRpcClient (cloud RPC via unified messages), AuditEngine (local+remote merge), WipeEngine, LocalInjectEngine
- `src/SteamCloudTamper.Cli` - CLI commands
- `tools/SteamCloudTamper.ApiProbe` - reflection dump of SteamKit2 API surface (development aid)
- `tests/SteamCloudTamper.Core.Tests` - unit tests

## Commands

```
detect                  locate Steam + accounts + libraries
scan                    audit local userdata buckets (per account/app)
remote-list --app <id>  list files in a cloud bucket
probe <appid...>        check what the backend allows: enumerate / upload / delete
wipe <appid> <file> [--blank] [--force]      delete or blank one cloud file
wipe-all <appid> [--blank] [--force]         wipe an entire bucket
guards add|rm|ls <appid>   maintained never-touch list (steamcloudtamper.json)
inject <uid3> <appid> <file> [remote-name]   local user drop + remotecache.vdf regen
lock/unlock <uid3> <appid>  isolate a bucket locally (see Strategy 2 below)
```

Auth: anonymous by default; set `SCT_USER` / `SCT_PASS` for account-scoped operations (probe/wipe of unowned buckets requires this because anonymous enumeration is denied).

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

### Strategy 3 - "console tricks"
`steam://open/console` commands like `download_depot <AppID>` or any `settingcloudaudit` do NOT exist / do not do what the casual writeups claim (`download_depot` fetches game files, unrelated to cloud saves). The only real per-bucket client switches are: Steam settings > Cloud per-app toggles (owned apps), the lockfile above, and CloudRedirect hiding.