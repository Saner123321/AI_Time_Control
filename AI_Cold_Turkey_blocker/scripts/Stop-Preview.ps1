#Requires -Version 5.1
[CmdletBinding()]
param([string]$BundleRoot=$PSScriptRoot,[switch]$SelfTest)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$taskBundle=[IO.Path]::GetFullPath($BundleRoot)
$taskDataFolder=if($SelfTest) { 'ui-self-test-data' } else { 'data' }
$taskReceipt=Join-Path $taskBundle ($taskDataFolder+'\backend-process.json')
if(-not(Test-Path -LiteralPath $taskReceipt)) { throw 'No preview process receipt in this bundle. No process was stopped.' }
$taskRecord=Get-Content -Raw -Encoding UTF8 -LiteralPath $taskReceipt | ConvertFrom-Json
$taskExpected=[IO.Path]::GetFullPath((Join-Path $taskBundle 'Backend\Aict.Backend.exe'))
if(-not [string]::Equals($taskExpected,[string]$taskRecord.executable,[StringComparison]::OrdinalIgnoreCase)) { throw 'Preview executable path mismatch. No process was stopped.' }
$taskProcess=Get-Process -Id $taskRecord.id -ErrorAction SilentlyContinue
if($null -eq $taskProcess) { Write-Host 'Preview backend already stopped.'; return }
$taskRecordedTime=if($taskRecord.startTimeUtc -is [DateTime]) { $taskRecord.startTimeUtc.ToUniversalTime() } else { ([DateTime]::Parse([string]$taskRecord.startTimeUtc,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind)).ToUniversalTime() }
if(-not [string]::Equals($taskProcess.Path,$taskExpected,[StringComparison]::OrdinalIgnoreCase) -or $taskProcess.StartTime.ToUniversalTime().Ticks -ne $taskRecordedTime.Ticks) { throw 'Process identity changed. No process was stopped.' }
Stop-Process -Id $taskProcess.Id
Write-Host 'Development preview backend stopped. Saved preview rules were retained. This is not a production service control.'
