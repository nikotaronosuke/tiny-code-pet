# install-hook.ps1 - Register ClaudePetNotify.exe hooks in user-level Claude Code settings.
# Events: Stop, UserPromptSubmit, Notification(permission_prompt), PostToolUse(*), SessionEnd.
# - Never overwrites existing hooks: appends only, idempotent per event.
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

function New-HookEntry([string]$cmd, [object]$matcher) {
    $inner = [pscustomobject]@{
        type    = "command"
        command = $cmd
        timeout = 10
        async   = $true
    }
    if ($null -ne $matcher) {
        return [pscustomobject]@{ matcher = $matcher; hooks = @($inner) }
    }
    return [pscustomobject]@{ hooks = @($inner) }
}

# event name -> matcher ($null = no matcher field)
$events = [ordered]@{
    "Stop"             = $null
    "UserPromptSubmit" = $null
    "Notification"     = "permission_prompt"
    "PostToolUse"      = "*"
    "SessionEnd"       = $null
    "TaskCreated"      = $null
    "TaskCompleted"    = $null
}

$changed = $false
foreach ($ev in $events.Keys) {
    if (-not ($settings.hooks.PSObject.Properties.Name -contains $ev)) {
        $settings.hooks | Add-Member -MemberType NoteProperty -Name $ev -Value @()
    }
    $already = $false
    foreach ($group in @($settings.hooks.$ev)) {
        foreach ($h in @($group.hooks)) {
            if ($h.command -like "*ClaudePetNotify*") { $already = $true }
        }
    }
    if ($already) {
        Write-Host "$ev : already installed, skipped"
    } else {
        $settings.hooks.$ev = @($settings.hooks.$ev) + @((New-HookEntry $hookCommand $events[$ev]))
        Write-Host "$ev : installed"
        $changed = $true
    }
}

if ($changed) {
    $json = $settings | ConvertTo-Json -Depth 64
    # Write UTF-8 WITHOUT BOM (a BOM can break JSON parsers).
    [System.IO.File]::WriteAllText($settingsPath, $json, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "Settings updated: $settingsPath"
} else {
    Write-Host "Nothing changed."
}
