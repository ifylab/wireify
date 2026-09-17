# Wireify lesson gate: record that a structural mutation ran this session.
# Content is a debugging aid; the Stop-time check only needs existence + mtime.
$ErrorActionPreference = "SilentlyContinue"
$null = [Console]::In.ReadToEnd()
$dir = Split-Path -Parent $PSScriptRoot
Add-Content -Path (Join-Path $dir "mutations.log") -Value ([DateTime]::UtcNow.ToString("o"))
exit 0
