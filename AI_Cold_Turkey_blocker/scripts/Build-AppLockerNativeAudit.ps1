#Requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskCompiler = "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$taskMi = "$env:SystemRoot\Microsoft.NET\assembly\GAC_MSIL\Microsoft.Management.Infrastructure\v4.0_1.0.0.0__31bf3856ad364e35\Microsoft.Management.Infrastructure.dll"
if (-not (Test-Path -LiteralPath $taskCompiler) -or -not (Test-Path -LiteralPath $taskMi)) { throw 'Required inbox .NET Framework compiler or Windows MI assembly not found; no installation attempted.' }
$taskBuild = Join-Path $taskRoot 'lab\build'
$null = New-Item -ItemType Directory -Path $taskBuild -Force
$taskBinary = Join-Path $taskBuild 'AictAppLockerNativeAudit.exe'
& $taskCompiler /nologo /target:exe /platform:x64 /optimize+ /r:System.Management.dll /r:System.Web.Extensions.dll ('/r:' + $taskMi) ('/out:' + $taskBinary) (Join-Path $taskRoot 'lab\AppLockerNativeAudit.cs')
if ($LASTEXITCODE -ne 0) { throw 'Native MI audit lab compilation failed.' }
& $taskBinary --validate
if ($LASTEXITCODE -ne 0) { throw 'Offline AuditOnly XML validation failed.' }
Write-Host 'Built laboratory worker only. No task or policy applied.'
