# install-codex-hook.ps1 - Register CodexPetNotify.exe hooks in Codex hooks.json.
# Events: UserPromptSubmit, PostToolUse(.*), PermissionRequest(.*), Stop, SessionEnd,
#         SubagentStart, SubagentStop.  (PreToolUse is NOT needed in production.)
# - Never overwrites existing hooks: appends only, idempotent per event.
# - Creates a timestamped backup of hooks.json first.
# - Never touches config.toml (model / sandbox / trust / notify are left alone).
# - Use -DryRun to preview every change without writing anything.
# NOTE: keep this file ASCII-only. Works on Windows PowerShell 5.1 and pwsh 7.
[CmdletBinding()]
param(
    [switch]$DryRun,
    [string]$CodexHome,
    [string]$ProjectPath   # optional: install project-level <ProjectPath>\.codex\hooks.json
)
$ErrorActionPreference = "Stop"

$notifyExe = Join-Path $PSScriptRoot "bin\CodexPetNotify.exe"
if (-not (Test-Path $notifyExe)) { throw "Run build.ps1 first: $notifyExe not found" }
$notifyExe = (Resolve-Path $notifyExe).Path

if ($ProjectPath) {
    $targetDir = Join-Path (Resolve-Path $ProjectPath).Path ".codex"
    $scope = "project"
} else {
    if (-not $CodexHome) {
        $CodexHome = $env:CODEX_HOME
        if (-not $CodexHome) { $CodexHome = Join-Path $env:USERPROFILE ".codex" }
    }
    $targetDir = $CodexHome
    $scope = "user"
}
$hooksPath = Join-Path $targetDir "hooks.json"
$configPath = Join-Path $targetDir "config.toml"

Write-Host "Scope      : $scope"
Write-Host "hooks.json : $hooksPath"
Write-Host "helper     : $notifyExe"
if ($DryRun) { Write-Host "MODE       : DRY RUN (nothing will be written)" }

# Codex accepts both; commandWindows is used on Windows.
$cmdPosix = ($notifyExe -replace '\\', '/')
$cmdWin = $notifyExe

# event name -> @{ matcher = <regex or $null>; async = $bool }
# async is a performance optimization only: correctness must not depend on it.
#   UserPromptSubmit / PermissionRequest / Stop / Subagent*: ordering matters -> sync
#   PostToolUse: high frequency -> async
#   SessionEnd: Codex runs SessionEnd synchronously anyway
$events = [ordered]@{
    "UserPromptSubmit"  = @{ matcher = $null; async = $false }
    "PostToolUse"       = @{ matcher = ".*";  async = $true  }
    "PermissionRequest" = @{ matcher = ".*";  async = $false }
    "Stop"              = @{ matcher = $null; async = $false }
    "SessionEnd"        = @{ matcher = $null; async = $false }
    "SubagentStart"     = @{ matcher = $null; async = $false }
    "SubagentStop"      = @{ matcher = $null; async = $false }
}

function New-HookEntry([object]$matcher, [bool]$async) {
    $inner = [pscustomobject]@{
        type           = "command"
        command        = $cmdPosix
        commandWindows = $cmdWin
        async          = $async
        timeout        = 5
    }
    if ($null -ne $matcher) {
        return [pscustomobject]@{ matcher = $matcher; hooks = @($inner) }
    }
    return [pscustomobject]@{ hooks = @($inner) }
}

if (Test-Path $hooksPath) {
    $raw = Get-Content $hooksPath -Raw
    try { $doc = $raw | ConvertFrom-Json } catch { throw "Existing hooks.json is not valid JSON: $hooksPath" }
    if ($null -eq $doc) { $doc = [pscustomobject]@{} }
} else {
    $doc = [pscustomobject]@{}
}

if (-not ($doc.PSObject.Properties.Name -contains "hooks")) {
    $doc | Add-Member -MemberType NoteProperty -Name "hooks" -Value ([pscustomobject]@{})
}

$changed = $false
foreach ($ev in $events.Keys) {
    $spec = $events[$ev]
    if (-not ($doc.hooks.PSObject.Properties.Name -contains $ev)) {
        $doc.hooks | Add-Member -MemberType NoteProperty -Name $ev -Value @()
    }
    $already = $false
    foreach ($group in @($doc.hooks.$ev)) {
        foreach ($h in @($group.hooks)) {
            if (("$($h.command)" -like "*CodexPetNotify*") -or ("$($h.commandWindows)" -like "*CodexPetNotify*")) {
                $already = $true
            }
        }
    }
    if ($already) {
        Write-Host "$ev : already installed, skipped"
    } else {
        $doc.hooks.$ev = @($doc.hooks.$ev) + @((New-HookEntry $spec.matcher $spec.async))
        $mode = if ($spec.async) { "async" } else { "sync" }
        Write-Host "$ev : will install ($mode)"
        $changed = $true
    }
}

$json = $doc | ConvertTo-Json -Depth 64

if (-not $changed) {
    Write-Host "Nothing to change."
} elseif ($DryRun) {
    Write-Host "---- hooks.json that WOULD be written ----"
    Write-Host $json
    Write-Host "---- (dry run: not written) ----"
} else {
    if (-not (Test-Path $targetDir)) { New-Item -ItemType Directory -Force -Path $targetDir | Out-Null }
    if (Test-Path $hooksPath) {
        $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $backup = "$hooksPath.backup-codexpet-$stamp"
        Copy-Item $hooksPath $backup
        Write-Host "Backup created: $backup"
    }
    # UTF-8 without BOM (a BOM can break JSON parsers).
    [System.IO.File]::WriteAllText($hooksPath, $json, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "Hooks written: $hooksPath"
}

# --- validation / manual steps -------------------------------------------
# config.toml is never modified by this script.
$featureOk = $false
if (Test-Path $configPath) {
    $cfg = Get-Content $configPath -Raw
    if ($cfg -match '(?m)^\s*\[features\]') {
        $tail = $cfg.Substring($cfg.IndexOf("[features]"))
        $nextSection = [regex]::Match($tail.Substring(1), '(?m)^\s*\[')
        if ($nextSection.Success) { $tail = $tail.Substring(0, $nextSection.Index + 1) }
        if ($tail -match '(?m)^\s*hooks\s*=\s*true\s*$') { $featureOk = $true }
    }
}
Write-Host ""
if ($featureOk) {
    Write-Host "OK: [features] hooks = true is present in $configPath"
} else {
    Write-Host "ACTION REQUIRED: Codex hooks are not enabled yet."
    Write-Host "  Add these two lines to $configPath (this script never edits it):"
    Write-Host "    [features]"
    Write-Host "    hooks = true"
}
Write-Host "NOTE: Codex asks you to review and trust hooks.json on the next start."
Write-Host "      Approve it there. Do NOT use --dangerously-bypass-hook-trust."
Write-Host "      Hooks take effect in new Codex sessions."

# Self-check: prove the helper normalizes a sample payload without sending anything.
# The helper is a GUI-subsystem exe, so stdio must be redirected via Start-Process.
$sample = '{"hook_event_name":"UserPromptSubmit","session_id":"selfcheck","turn_id":"t0","cwd":"C:/dev/claude-desktop-pet"}'
$inFile = [System.IO.Path]::GetTempFileName()
$outFile = [System.IO.Path]::GetTempFileName()
try {
    [System.IO.File]::WriteAllText($inFile, $sample, (New-Object System.Text.UTF8Encoding($false)))
    Start-Process -FilePath $notifyExe -ArgumentList "--dry-run" -RedirectStandardInput $inFile `
        -RedirectStandardOutput $outFile -NoNewWindow -Wait
    Write-Host ("helper self-check: " + ((Get-Content $outFile -Raw).Trim()))
} finally {
    Remove-Item $inFile, $outFile -Force -ErrorAction SilentlyContinue
}
