# Build sct_hook.dll (Steam RemoteStorage redirect shim) with GCC/MinGW-w64.
# Requires: gcc on PATH, or set GCC.

$ErrorActionPreference = "Stop"
$exe = $env:GCC
if (-not $exe) { $exe = "gcc" }

$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
& $exe -shared -O2 -o "$dir\sct_hook.dll" "$dir\sct_hook.c"
if ($LASTEXITCODE -ne 0) { throw "build failed" }
Write-Host "built $dir\sct_hook.dll"