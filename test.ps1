# No installs, live hooks, production process or account settings are touched.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'bin\ninja-tests'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /codepage:65001 /warnaserror+ /optimize+ /target:exe /main:NinjaTests "/out:$out\NinjaTests.exe" /r:System.Drawing.dll /r:System.Web.Extensions.dll "/resource:$root\assets\ninja\ninja.png,ninja.png" "/resource:$root\assets\ninja\ninja.json,ninja.json" "$root\src\Pet.cs" "$root\src\SpriteAnimator.cs" "$root\src\SubagentRoster.cs" "$root\src\AgentIdentity.cs" "$root\tests\NinjaTests.cs"
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed' }
& "$out\NinjaTests.exe" $out
if ($LASTEXITCODE -ne 0) { throw 'Ninja tests failed' }
