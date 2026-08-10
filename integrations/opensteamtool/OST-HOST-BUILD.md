# Building OpenSteamTool from source (why, and how)

## Why

The `[cloud]` section of `opensteamtool.toml` (CloudRedirectHost) was added in
PR #138, merged 2026-06-25. The latest tagged release **v1.4.8 (2026-06-13)
predates it** — a release build contains no cloud-redirect code at all.
Until upstream ships v1.4.9+, build `main`.

## How (verified on this machine)

Requirements: git, CMake 3.20+, Visual Studio 2022 Build Tools with the
C++ workload (`cl.exe` — `winget install Microsoft.VisualStudio.2022.BuildTools
--override "--add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"`).

```bat
git clone https://github.com/OpenSteam001/OpenSteamTool
cd OpenSteamTool
"C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\Tools\VsDevCmd.bat" -arch=amd64
set CONFIGS=Release
build.bat
```

Dependencies are CMake FetchContent (cached under `<repo>\.deps`); no vcpkg.

Outputs: `build\Release\{dwmapi.dll, xinput1_4.dll, OpenSteamTool.dll}` — copy to
`D:\Steam\` with Steam stopped.

## Smoke test after swapping in the built DLL

`D:\Steam\cloud_redirect.log` appears once Steam performs a Cloud sync for any
redirected app (log lines like `[VtHook] INTERCEPT Cloud.CompleteAppUploadBatchBlocking#1
app=588650`). If the file never appears, the DLL lacks the CloudRedirectHost
(you got a release build) or `[cloud] enabled` isn't being parsed.
