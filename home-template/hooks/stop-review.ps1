# Wireify lesson gate: block the session end ONCE when components were built or
# rewired but MEMORY.md gained no lesson. Every check is a local file test;
# fail-open on any missing state.
$ErrorActionPreference = "SilentlyContinue"
$payload = [Console]::In.ReadToEnd()
if ($payload -match '"stop_hook_active"\s*:\s*true') { exit 0 }
$dir = Split-Path -Parent $PSScriptRoot
$homeDir = Split-Path -Parent $dir
$marker = Join-Path $dir "session-start-marker"
if (-not (Test-Path $marker)) { exit 0 }
$log = Join-Path $dir "mutations.log"
if (-not (Test-Path $log)) { exit 0 }
if ((Get-Item $log).Length -eq 0) { exit 0 }
$markerTime = (Get-Item $marker).LastWriteTimeUtc
if ((Get-Item $log).LastWriteTimeUtc -le $markerTime) { exit 0 }
$done = Join-Path $dir "review-done"
if ((Test-Path $done) -and ((Get-Item $done).LastWriteTimeUtc -gt $markerTime)) { exit 0 }
$mem = Join-Path $homeDir "MEMORY.md"
if ((Test-Path $mem) -and ((Get-Item $mem).LastWriteTimeUtc -gt $markerTime)) { exit 0 }
New-Item -ItemType File -Force -Path $done | Out-Null
$today = Get-Date -Format yyyy-MM-dd
[Console]::Error.WriteLine("Wireify: components were built or rewired this session, but MEMORY.md has no new lesson. If a non-obvious fix, gotcha, or user correction emerged, append it now as '### $today [W<n>] <title>' with Symptom / Cause / Fix / Applies-when (rules at the top of MEMORY.md). If nothing durable emerged, say so and finish.")
exit 2
