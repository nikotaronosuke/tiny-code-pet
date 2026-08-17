# uninstall-hook.ps1 - Remove all ClaudePetNotify hooks from user-level Claude Code settings.
# Only removes hook entries whose command contains "ClaudePetNotify"; everything else is untouched.
# Creates a timestamped backup of settings.json first.
# NOTE: keep this file ASCII-only. Prefer running with pwsh (PowerShell 7).
$ErrorActionPreference = "Stop"

$settingsPath = Join-Path $env:USERPROFILE ".claude\settings.json"
if (-not (Test-Path $settingsPath)) { Write-Host "No settings.json; nothing to do."; exit 0 }

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backup = "$settingsPath.backup-claudepet-$stamp"
Copy-Item $settingsPath $backup
Write-Host "Backup created: $backup"

$settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
if (-not ($settings.PSObject.Properties.Name -contains "hooks")) {
    Write-Host "No hooks registered; nothing to do."
    exit 0
}

$removed = 0
$eventNames = @($settings.hooks.PSObject.Properties.Name)
foreach ($ev in $eventNames) {
    $newGroups = @()
    foreach ($group in @($settings.hooks.$ev)) {
        $kept = @()
        foreach ($h in @($group.hooks)) {
            if ($h.command -like "*ClaudePetNotify*") { $removed++ } else { $kept += $h }
        }
        if ($kept.Count -gt 0) {
            $group.hooks = $kept
            $newGroups += $group
        }
    }
    if ($newGroups.Count -gt 0) {
        $settings.hooks.$ev = $newGroups
    } else {
        $settings.hooks.PSObject.Properties.Remove($ev)
    }
}

if ($removed -eq 0) {
    Write-Host "ClaudePetNotify hooks not found; nothing changed."
    exit 0
}

if ($settings.hooks.PSObject.Properties.Name.Count -eq 0) {
    $settings.PSObject.Properties.Remove("hooks")
}

$json = $settings | ConvertTo-Json -Depth 64
[System.IO.File]::WriteAllText($settingsPath, $json, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Removed $removed ClaudePetNotify hook(s)."
