#Requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskCompiler = "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$taskMi = "$env:SystemRoot\Microsoft.NET\assembly\GAC_MSIL\Microsoft.Management.Infrastructure\v4.0_1.0.0.0__31bf3856ad364e35\Microsoft.Management.Infrastructure.dll"
if (-not (Test-Path -LiteralPath $taskCompiler) -or -not (Test-Path -LiteralPath $taskMi)) { throw 'Inbox compiler/MI assembly missing. No installation attempted.' }
$taskBuild = Join-Path $taskRoot 'lab\build'
$null = New-Item -ItemType Directory -Path $taskBuild -Force
$taskBinary = Join-Path $taskBuild 'AictAppLockerEnforceSmoke.exe'
& $taskCompiler /nologo /target:exe /platform:x64 /optimize+ /r:System.Management.dll /r:System.Web.Extensions.dll ('/r:' + $taskMi) ('/out:' + $taskBinary) (Join-Path $taskRoot 'lab\AppLockerEnforceSmoke.cs')
if ($LASTEXITCODE -ne 0) { throw 'Enforce harness compilation failed.' }
& $taskBinary --validate
if ($LASTEXITCODE -ne 0) { throw 'Offline Enforce policy/verdict contracts failed.' }
$taskFixture = Join-Path $taskBuild 'AictAppLockerFixture.exe'
& $taskCompiler /nologo /target:exe /platform:x64 /optimize+ ('/out:' + $taskFixture) (Join-Path $taskRoot 'lab\AppLockerFixture.cs')
if ($LASTEXITCODE -ne 0) { throw 'Inert fixture compilation failed.' }
$taskFixtureOutput = & $taskFixture
if ($LASTEXITCODE -ne 0 -or $taskFixtureOutput -cne 'AICT_FIXTURE_OK') { throw 'Inert fixture marker check failed.' }
Write-Host 'Built and checked laboratory binaries only. No policy or task applied.'
