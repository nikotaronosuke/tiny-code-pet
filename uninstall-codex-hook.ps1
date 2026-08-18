# uninstall-codex-hook.ps1 - Remove CodexPetNotify hooks from Codex hooks.json.
# Only removes hook entries whose command contains "CodexPetNotify"; everything else
# (including other people's hooks) is left untouched. config.toml is never modified.
# Creates a timestamped backup first. Use -DryRun to preview.
# NOTE: keep this file ASCII-only. Works on Windows PowerShell 5.1 and pwsh 7.
[CmdletBinding()]
param(
    [switch]$DryRun,
    [string]$CodexHome,
    [string]$ProjectPath
)
$ErrorActionPreference = "Stop"

if ($ProjectPath) {
    $targetDir = Join-Path (Resolve-Path $ProjectPath).Path ".codex"
} else {
    if (-not $CodexHome) {
        $CodexHome = $env:CODEX_HOME
        if (-not $CodexHome) { $CodexHome = Join-Path $env:USERPROFILE ".codex" }
    }
    $targetDir = $CodexHome
}
$hooksPath = Join-Path $targetDir "hooks.json"

Write-Host "hooks.json : $hooksPath"
if ($DryRun) { Write-Host "MODE       : DRY RUN (nothing will be written)" }

if (-not (Test-Path $hooksPath)) { Write-Host "No hooks.json; nothing to do."; exit 0 }

$doc = Get-Content $hooksPath -Raw | ConvertFrom-Json
if ($null -eq $doc -or -not ($doc.PSObject.Properties.Name -contains "hooks")) {
    Write-Host "No hooks registered; nothing to do."
    exit 0
}

function Is-CodexPetHook($h) {
    return (("$($h.command)" -like "*CodexPetNotify*") -or ("$($h.commandWindows)" -like "*CodexPetNotify*"))
}

$removed = 0
# NOTE: on an empty PSCustomObject, .Properties.Name yields a single $null,
# so enumerate the property objects instead of the Name collection.
$eventNames = @($doc.hooks.PSObject.Properties | ForEach-Object { $_.Name })
foreach ($ev in $eventNames) {
    $newGroups = @()
    foreach ($group in @($doc.hooks.$ev)) {
        $kept = @()
        foreach ($h in @($group.hooks)) {
            if (Is-CodexPetHook $h) { $removed++ } else { $kept += $h }
        }
        if ($kept.Count -gt 0) {
            $group.hooks = $kept
            $newGroups += $group
        }
    }
    if ($newGroups.Count -gt 0) {
        $doc.hooks.$ev = $newGroups
    } else {
        $doc.hooks.PSObject.Properties.Remove($ev)
    }
}

if ($removed -eq 0) {
    Write-Host "CodexPetNotify hooks not found; nothing changed."
    exit 0
}

$otherTopLevel = @($doc.PSObject.Properties | ForEach-Object { $_.Name } | Where-Object { $_ -ne "hooks" })
$hooksEmpty = (@($doc.hooks.PSObject.Properties).Count -eq 0)
$deleteFile = ($hooksEmpty -and $otherTopLevel.Count -eq 0)

if ($DryRun) {
    Write-Host "Would remove $removed CodexPetNotify hook(s)."
    if ($deleteFile) { Write-Host "Would delete $hooksPath (no other hooks left)." }
    else { Write-Host "---- hooks.json that WOULD be written ----"; Write-Host ($doc | ConvertTo-Json -Depth 64) }
    exit 0
}

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backup = "$hooksPath.backup-codexpet-$stamp"
Copy-Item $hooksPath $backup
Write-Host "Backup created: $backup"

if ($deleteFile) {
    Remove-Item $hooksPath -Force
    Write-Host "Removed $removed CodexPetNotify hook(s); deleted empty $hooksPath."
} else {
    $json = $doc | ConvertTo-Json -Depth 64
    [System.IO.File]::WriteAllText($hooksPath, $json, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "Removed $removed CodexPetNotify hook(s)."
}
