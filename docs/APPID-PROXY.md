# AppID proxy for unowned cloud (Ace SLS's approach, 2026-08-12)

Source: `cloud proxy.7z` -> `0004-feat-cloud-Add-appId-proxy-for-unowned-games.patch`
(a 4/4 patch by Ace SLS, acesls@protonmail.com). We read the whole thing so you
dont have to. Credit where its due: this is a clean idea and it works around the
same april 2025 valve wall we do - just from inside the client instead of outside.

## what it does

an in-process hook on `CClientUnifiedServiceTransport::sendAndRecvMsg` rewrites
every `Cloud.*` request for an UNOWNED appid into a configured **proxy appid**
(the one you own) and namespaces every filename with `sls-<appid>/`:

- `pool discover`-equivalent: `CloudProxies:` map in the yaml. key `0` = default
  proxy for ANY unowned app. per-appid entries override.
- `shouldIntercept()`: skip subscribed apps; skip appid 7 (isSubscribed lies
  about it).
- intercepted services: BeginAppUploadBatch, ClientBeginFileUpload,
  ClientCommitFileUpload, ClientConflictResolution, ClientDeleteFile,
  ClientFileDownload, ClientGetAppQuotaUsage, CompleteAppUploadBatchBlocking,
  GetAppFileChangelist, GetSingleFileInfo, ResumeAppSession,
  SignalAppLaunchIntent, SuspendAppSession.
- uploads/deletes/downloads get `sls-<appid>/` glued onto the file name, so saves
  from different games never collide inside the one bucket.
- the changelist response is FILTERED: files whose path prefix isnt
  `sls-<appid>/` are dropped (DeleteSubrange), prefix indices cleaned up
  (a zero-length path prefix segfaults the client, they noted), prefixes
  stripped, and the change numbers are hammered to 0 both ways.
- why it works: the SERVER never sees the unowned appid. it sees the proxy appid
  you own. no AccessDenied, no entitlement check. the changelist claim checks
  out - "Stellar Blade (unowned) returned a valid changelist" is consistent
  with a rewrite, not a bypass.

## the cloud-cleaner

`tools/cloud-cleaner/main.cpp` - Steamworks SDK tool:
`SteamAppId=<proxy>`, `SteamAPI_Init`, `GetISteamRemoteStorage`, then deletes
every file with the `sls-<appid>/` prefix between Begin/EndFileWriteBatch.
notable bits:

- doesnt need to know which files are "saves" - the prefix does that.
- GetFileCount can come back 0 right after init (steam still downloading the
  filelist), it retries up to 10x with 1s sleeps.
- deletes happen WHILE iterating forward with an `i--` retry hack - and yes,
  that IS the bug the author mentioned ("iterate the files backwards fyi").
  iterating from files-1 down to 0 fixes it, which is exactly what the changelist
  filter in the patch itself does. consistency, folks.
- file size limit note: 100MB per file, 100GB per game (Sandbox quota caps).

## what it means for SCT

- **validation**: someone else proved the "keep unowned saves inside a bucket you
  own" concept end-to-end, INCLUDING the client being able to download them back
  (the changelist filter presumes downloads will happen - so the client both
  uploads AND downloads proxied saves). our barcode lane does the same job with
  zero hooks - but our 480 files never get a real changelist because the client
  never AutoClouds 480. if we ever want GUI-visible synced saves for unowned
  games via a PROXY instead of CR redirect, this is the mechanism to copy.
- **the changelist filter lesson**: if you park multiple games in one bucket and
  the client is actually managing it, you HAVE to filter foreign paths out of
  changelist responses or the client goes downloading other games' saves. our
  strategy avoids this (client ignores 480, we reconstruct maps via barcodes),
  but it would bite us in any "make the client really manage the bucket" lane.
- **no changes to SCT code needed from this**: the mechanism lives inside the
  steamclient process (OST/CR-family host hook). our rpc lane talks UFS directly
  via SteamKit with real appids - which is exactly why the hook is needed there
  and not here. if we ever want it, its "code to copy" for the integrations
  folder, not the core repo.
- **backwards-iteration check on our own loops**: WipeEngine, PoolScanner and
  the rebuilds iterate by NAME and delete by name - no index invalidation bugs
  lurking. the only index loop we have is in the discovery changelist-style
  filter, which is already backwards. good.
- appid 7 quirk worth remembering: `isSubscribed(7)` is untrustworthy server-side
  - skip it in any proxy logic too.

link to the original author's repo when they share it; this note is research only.