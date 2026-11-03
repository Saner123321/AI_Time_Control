#Requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskCompiler="$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path -LiteralPath $taskCompiler)) { throw 'Inbox x64 Framework compiler unavailable; no installation attempted.' }
$taskBuild=Join-Path $taskRoot 'lab\build\service'
$null=New-Item -ItemType Directory -Path $taskBuild -Force
$taskBinary=Join-Path $taskBuild 'AictServiceLab.exe'
& $taskCompiler /nologo /target:exe /platform:x64 /optimize+ /r:System.Management.dll /r:System.ServiceProcess.dll /r:System.Web.Extensions.dll ('/out:'+$taskBinary) (Join-Path $taskRoot 'lab\ServiceLab.cs')
if ($LASTEXITCODE -ne 0) { throw 'Service lab compilation failed' }
& $taskBinary --validate
if ($LASTEXITCODE -ne 0) { throw 'SCM ABI/security offline contract failed' }
Write-Host 'Built phase0 service lab only; no service/SCM access performed.'
