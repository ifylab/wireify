# Wireify lesson gate: stamp the session start. A fresh session gets a fresh
# mutation record, so the Stop-time check below only ever sees this session.
$ErrorActionPreference = "SilentlyContinue"
$dir = Split-Path -Parent $PSScriptRoot
New-Item -ItemType File -Force -Path (Join-Path $dir "session-start-marker") | Out-Null
Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $dir "mutations.log")
Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $dir "review-done")
exit 0
