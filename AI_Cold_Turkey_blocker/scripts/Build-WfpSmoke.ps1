#Requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskCompiler = "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path -LiteralPath $taskCompiler)) { throw '.NET Framework x64 compiler not found. No global SDK installation attempted.' }
$taskDirectory = Join-Path $taskRoot 'lab\build'
$null = New-Item -ItemType Directory -Path $taskDirectory -Force
$taskBinary = Join-Path $taskDirectory 'AictWfpSmoke.exe'
& $taskCompiler /nologo /target:exe /platform:x64 /optimize+ /r:System.Management.dll /r:System.Web.Extensions.dll (('/out:' + $taskBinary)) (Join-Path $taskRoot 'lab\WfpSmoke.cs')
if ($LASTEXITCODE -ne 0) { throw 'Lab harness compilation failed.' }
& $taskBinary --layout-only
if ($LASTEXITCODE -ne 0) { throw 'Native ABI layout validation failed.' }
Write-Host 'Built laboratory harness only. No filtering performed.'
