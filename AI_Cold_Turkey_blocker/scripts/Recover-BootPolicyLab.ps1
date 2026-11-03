#Requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'BootPolicyLabCommon.ps1')
Assert-AictPolicyGuest
$taskPackage=Split-Path -Parent $PSScriptRoot;$taskManifest=Read-AictPolicyManifest -Package $taskPackage -ExpectedKind 'AICT_BOOT_POLICY_LAB_V1'
$taskContext=Read-AictBootContext -Package $taskPackage -Manifest $taskManifest
$taskResult=[ordered]@{kind='AICT_BOOT_POLICY_RECOVERY';runId=$taskContext.RunId;serviceAbsent=$false;ownPoliciesAbsent=$false;windowTaskRemoved=$false;cleanupTaskRemoved=$false;exportRefusedFiles=@();error=$null}
try {
    # Device-policy cleanup runs first and does not depend on config/database health.
    $taskCleanup=Invoke-AictPolicyNative -Context $taskContext -Mode '--cleanup' -Tag ('recovery-'+[guid]::NewGuid().ToString('N'))
    $taskResult.serviceAbsent=$taskCleanup.serviceAbsent;$taskResult.ownPoliciesAbsent=$taskCleanup.ownPoliciesAbsent
    if(-not $taskResult.serviceAbsent -or -not $taskResult.ownPoliciesAbsent) { throw 'Owned service or policies remain; independent cleanup retained.' }
    $taskResult.windowTaskRemoved=Remove-AictBootWindow -Context $taskContext
    $taskResult.cleanupTaskRemoved=Remove-AictPolicyWatchdog -Context $taskContext
} catch { $taskResult.error=$_.Exception.Message }
finally { Export-AictPolicyEvidence -Context $taskContext -Result $taskResult -Prefix 'boot-policy-recovery' }
if($null -ne $taskResult.error -or -not $taskResult.windowTaskRemoved -or -not $taskResult.cleanupTaskRemoved) { throw 'Recovery did not confirm complete task cleanup. Keep the protected directory and recovery baseline.' }
Write-Host 'Exact owned service, fixture policies, logon probe and cleanup task removed. Saved state and evidence retained.'
