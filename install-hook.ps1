# install-hook.ps1 - Register ClaudePetNotify.exe as a user-level Claude Code Stop hook.
# - Never overwrites existing hooks: appends only, idempotent.
# - Creates a timestamped backup of settings.json first.
# NOTE: keep this file ASCII-only. Prefer running with pwsh (PowerShell 7).
$ErrorActionPreference = "Stop"

$settingsPath = Join-Path $env:USERPROFILE ".claude\settings.json"
$notifyExe = Join-Path $PSScriptRoot "bin\ClaudePetNotify.exe"
if (-not (Test-Path $notifyExe)) { throw "Run build.ps1 first: $notifyExe not found" }

# Claude Code runs shell-form hook commands via Git Bash / PowerShell; forward slashes work in both.
$hookCommand = ($notifyExe -replace '\\', '/')

if (Test-Path $settingsPath) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $backup = "$settingsPath.backup-claudepet-$stamp"
    Copy-Item $settingsPath $backup
    Write-Host "Backup created: $backup"
    $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
} else {
    $settings = [pscustomobject]@{}
}

if (-not ($settings.PSObject.Properties.Name -contains "hooks")) {
    $settings | Add-Member -MemberType NoteProperty -Name "hooks" -Value ([pscustomobject]@{})
}
if (-not ($settings.hooks.PSObject.Properties.Name -contains "Stop")) {
    $settings.hooks | Add-Member -MemberType NoteProperty -Name "Stop" -Value @()
}

# Idempotency: skip if already registered.
$already = $false
foreach ($group in @($settings.hooks.Stop)) {
    foreach ($h in @($group.hooks)) {
        if ($h.command -like "*ClaudePetNotify*") { $already = $true }
    }
}

if ($already) {
    Write-Host "Already installed. Nothing changed."
} else {
    $entry = [pscustomobject]@{
        hooks = @(
            [pscustomobject]@{
                type    = "command"
                command = $hookCommand
                timeout = 10
                async   = $true
            }
        )
    }
    $settings.hooks.Stop = @($settings.hooks.Stop) + @($entry)

    $json = $settings | ConvertTo-Json -Depth 64
    # Write UTF-8 WITHOUT BOM (a BOM can break JSON parsers).
    [System.IO.File]::WriteAllText($settingsPath, $json, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "Installed Stop hook -> $hookCommand"
}
