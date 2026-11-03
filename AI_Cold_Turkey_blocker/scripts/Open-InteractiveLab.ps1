#Requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'InteractiveLabCommon.ps1')
Assert-AictPolicyGuest
$taskPackage=Split-Path -Parent $PSScriptRoot;$taskManifest=Read-AictPolicyManifest -Package $taskPackage -ExpectedKind 'AICT_INTERACTIVE_LAB_V1'
$taskContext=Read-AictInteractiveContext -Package $taskPackage -Manifest $taskManifest
if((Get-Service -Name ('AICT-Interactive-'+$taskContext.RunId)).Status -ne 'Running') { throw 'Service not running. Keep state and collect/recover; this script does not reset it.' }
if((Get-ScheduledTask -TaskName $taskContext.UiTaskName).State -ne 'Running') { Start-ScheduledTask -TaskName $taskContext.UiTaskName -ErrorAction Stop }
Write-Host 'Existing service UI opened. Rules and pending modifications were not reset.'
