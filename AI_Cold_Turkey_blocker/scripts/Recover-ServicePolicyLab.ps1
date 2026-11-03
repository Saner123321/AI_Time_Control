#Requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'ServicePolicyLabCommon.ps1')
Assert-AictPolicyGuest
$taskPackage=Split-Path -Parent $PSScriptRoot;$taskManifest=Read-AictPolicyManifest -Package $taskPackage
$taskLocator=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskPackage 'active-service-policy.json') | ConvertFrom-Json
$taskContext=New-AictPolicyContext -Package $taskPackage -RunId $taskLocator.runId
if((Get-FileHash -LiteralPath (Join-Path $taskContext.Root 'AictPolicyBridge.exe') -Algorithm SHA256).Hash.ToLowerInvariant() -cne $taskManifest.files.'lab/AictPolicyBridge.exe') { throw 'Recovery worker binary changed. Left untouched; use the verified VM snapshot.' }
$taskCleanup=Invoke-AictPolicyNative -Context $taskContext -Mode '--cleanup' -Tag ('recovery-'+[DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
if(-not $taskCleanup.serviceAbsent -or -not $taskCleanup.ownPoliciesAbsent) { throw 'Cleanup not confirmed. Independent task retained.' }
if(-not(Remove-AictPolicyWatchdog -Context $taskContext)) { throw 'Cleanup task removal unconfirmed.' }
Write-Host 'Exact owned service and fixed fixture policies removed. State/config and all evidence preserved. Do not reuse the locator to run another experiment.'
