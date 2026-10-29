# Wireify lesson gate: stamp the session start. A fresh session gets a fresh
# mutation record, so the Stop-time check below only ever sees this session.
# Before that, print the warmup Wireify wrote at Connect (the previous
# session's facts); SessionStart output reaches the session as context.
$ErrorActionPreference = "SilentlyContinue"
$dir = Split-Path -Parent $PSScriptRoot
$warmup = Join-Path $dir "warmup.txt"
if (Test-Path $warmup) { Get-Content -Raw $warmup | Write-Output }
New-Item -ItemType File -Force -Path (Join-Path $dir "session-start-marker") | Out-Null
Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $dir "mutations.log")
Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $dir "review-done")
exit 0
