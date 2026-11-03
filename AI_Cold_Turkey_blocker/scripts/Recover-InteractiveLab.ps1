#Requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'InteractiveLabCommon.ps1')
Assert-AictPolicyGuest
$taskPackage=Split-Path -Parent $PSScriptRoot;$taskManifest=Read-AictPolicyManifest -Package $taskPackage -ExpectedKind 'AICT_INTERACTIVE_LAB_V1'
$taskContext=Read-AictInteractiveContext -Package $taskPackage -Manifest $taskManifest
$taskResult=[ordered]@{kind='AICT_INTERACTIVE_SESSION';stage='Recover';runId=$taskContext.RunId;serviceAbsent=$false;ownPoliciesAbsent=$false;uiTaskRemoved=$false;cleanupTaskRemoved=$false;exportRefusedFiles=@();error=$null}
try {
    $taskCleanup=Invoke-AictPolicyNative -Context $taskContext -Mode '--cleanup' -Tag ('recovery-'+[DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'));$taskResult.serviceAbsent=$taskCleanup.serviceAbsent;$taskResult.ownPoliciesAbsent=$taskCleanup.ownPoliciesAbsent
    if(-not $taskResult.serviceAbsent -or -not $taskResult.ownPoliciesAbsent) { throw 'Owned policy/service cleanup not confirmed; independent cleanup retained.' }
    $taskResult.uiTaskRemoved=Remove-AictInteractiveUiTask -Context $taskContext;$taskResult.cleanupTaskRemoved=Remove-AictPolicyWatchdog -Context $taskContext
} catch { $taskResult.error=$_.Exception.Message }
finally { Export-AictInteractiveEvidence -Context $taskContext -Result $taskResult }
if($null -ne $taskResult.error) { throw 'Recovery incomplete; keep its evidence and protected directory.' }
Write-Host 'Owned application restrictions and service removed. Saved rules/evidence retained. The UI can be closed normally.'
