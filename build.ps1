# build.ps1 - Build with csc.exe bundled in .NET Framework 4.8 (no extra install needed).
# NOTE: keep this file ASCII-only (Windows PowerShell 5.1 misreads BOM-less UTF-8).
param([string]$OutputDirectory = "bin")
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc" }

New-Item -ItemType Directory -Force -Path "$root\bin" | Out-Null

$output = Join-Path $root $OutputDirectory
New-Item -ItemType Directory -Force -Path $output | Out-Null

$common = @("/nologo", "/codepage:65001", "/optimize+", "/warn:4", "/target:winexe")

& $csc @common "/out:$output\ClaudePet.exe" "/r:System.Drawing.dll" "/r:System.Web.Extensions.dll" "/resource:$root\assets\ninja\ninja.png,ninja.png" "/resource:$root\assets\ninja\ninja.json,ninja.json" "$root\src\Pet.cs" "$root\src\SpriteAnimator.cs" "$root\src\SubagentRoster.cs"
if ($LASTEXITCODE -ne 0) { throw "build failed: ClaudePet.exe" }

& $csc @common "/out:$output\ClaudePetNotify.exe" "$root\src\Notify.cs" "$root\src\AgentIdentity.cs"
if ($LASTEXITCODE -ne 0) { throw "build failed: ClaudePetNotify.exe" }

& $csc @common "/out:$output\CodexPetNotify.exe" "$root\src\CodexNotify.cs" "$root\src\AgentIdentity.cs"
if ($LASTEXITCODE -ne 0) { throw "build failed: CodexPetNotify.exe" }

Write-Host "OK ($OutputDirectory): ClaudePet.exe, ClaudePetNotify.exe, CodexPetNotify.exe"
