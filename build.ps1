# build.ps1 - Build with csc.exe bundled in .NET Framework 4.8 (no extra install needed).
# NOTE: keep this file ASCII-only (Windows PowerShell 5.1 misreads BOM-less UTF-8).
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc" }

New-Item -ItemType Directory -Force -Path "$root\bin" | Out-Null

$common = @("/nologo", "/codepage:65001", "/optimize+", "/warn:4", "/target:winexe")

& $csc @common "/out:$root\bin\ClaudePet.exe" "/r:System.Drawing.dll" "$root\src\Pet.cs"
if ($LASTEXITCODE -ne 0) { throw "build failed: ClaudePet.exe" }

& $csc @common "/out:$root\bin\ClaudePetNotify.exe" "$root\src\Notify.cs"
if ($LASTEXITCODE -ne 0) { throw "build failed: ClaudePetNotify.exe" }

Write-Host "OK: bin\ClaudePet.exe, bin\ClaudePetNotify.exe"
