# OST host bundle — reproducible deployment set

Exact binaries deployed to `D:\Steam\` for the GUI-synced lane
(verified 2026-08-10, Steam client build 1785799196):

| File | Origin | SHA256 |
|---|---|---|
| `dwmapi.dll` | OpenSteamTool main (built 2026-08-10, Release) | `DE6C58D6DD385E9936DA7592BFC2682A5C465AF3DADB500BEA0EAA76249A46FA` |
| `OpenSteamTool.dll` | OpenSteamTool main (built 2026-08-10, Release) | `3196AC19A39EC7F1C3C4CC9C902DBC9626F8B584C5AFA8A38E6B90F194EB135E` |
| `xinput1_4.dll` | OpenSteamTool main (built 2026-08-10, Release) | `5A1CB30837D303582072866D8B5181E2F22ECDBA537F80BEC4D6554F411D8812` |
| `cloud_redirect.dll` | CloudRedirect v2.6.4 release asset | `A481B006E0C7763F4B2E69322B06C92B242AF59EB3E62189515231BAA9EA96B2` |

Deploy: copy all four into the Steam root (`D:\Steam\`) with Steam stopped.
Companion configs: `D:\Steam\opensteamtool.toml` (see
`integrations/opensteamtool/opensteamtool.toml.sct`) and
`%AppData%\CloudRedirect\config.json` (see
`integrations/opensteamtool/cloudredirect.config.example.json`).

Why build OST from source instead of the release zip: the `[cloud]` host only
exists on `main` (PR #138, 2026-06-25; v1.4.8 release is 2026-06-13).
See `integrations/opensteamtool/OST-HOST-BUILD.md`.
