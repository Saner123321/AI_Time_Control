#Requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'ServiceLabCommon.ps1')
Assert-AictServiceGuest
$taskPackage=Split-Path -Parent $PSScriptRoot
$taskManifest=Read-AictServiceManifest -Package $taskPackage
$taskLocator=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskPackage 'ACTIVE-SERVICE-LAB.json') | ConvertFrom-Json
$taskContext=New-AictServiceContext -Package $taskPackage -RunId $taskLocator.RunId -Manifest $taskManifest
if ($taskLocator.ProtectedLabDirectory -ine $taskContext.Root -or ((Get-Item -LiteralPath $taskContext.Root).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Unexpected lab locator/root.' }
if ((Get-FileHash -LiteralPath (Join-Path $taskContext.Root 'AictServiceLab.exe') -Algorithm SHA256).Hash.ToLowerInvariant() -cne $taskManifest.files.'lab/AictServiceLab.exe') { throw 'Staged recovery/inspection binary changed; refusing to execute it.' }
$taskPrepare=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskContext.Root 'prepare-summary.json') | ConvertFrom-Json
if ($taskPrepare.RunId -cne $taskContext.RunId -or -not $taskPrepare.PreparePassed) { throw 'No matching successful preparation. Preserve its failure evidence.' }
$taskStatus=[ordered]@{SchemaVersion=1;Kind='AICT_SERVICE_LAB_RUNNER';Stage='Finish';RunId=$taskContext.RunId;CollectedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');VmwareGuestVerified=$true;EditionId=$taskPrepare.EditionId;Build=$taskPrepare.Build;SnapshotReferenceOperatorSupplied=$taskPrepare.SnapshotReferenceOperatorSupplied;SnapshotRestoreVerified=$false;WorkerSha256=$taskManifest.files.'lab/AictServiceLab.exe';NoBlockingPolicyApplied=$true;PreparePassed=$true;CrashRecoveryPassed=$taskPrepare.CrashRecoveryPassed;PermissionsPassed=$false;BootObserved=$false;ServiceAutostartObserved=$false;TestStatePersisted=$false;BlockingAcrossBootProven=$false;FinishPassed=$false;ServiceAbsent=$false;CleanupTaskRemoved=$false;Error=$null}
$taskNeedsReboot=$false
Write-Host 'Collecting service-only reboot evidence, then removing this exact lab service and cleanup task. No blocking policies are involved.'
try {
    $taskAfter=Invoke-AictServiceNative -Context $taskContext -Mode '--inspect'
    $taskBefore=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskContext.Root 'inspect-after-crash.json') | ConvertFrom-Json
    if ($taskAfter.Heartbeat.BootTimeUtc -ceq $taskBefore.Heartbeat.BootTimeUtc) {
        $taskNeedsReboot=$true
        throw 'No reboot observed yet. Restart this virtual machine and rerun Finish; the service is left for that test until watchdog cleanup.'
    }
    $taskStatus.BootObserved=$true
    Write-AictLabJson -Path (Join-Path $taskContext.Root ('inspect-after-boot-'+[DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')+'.json')) -Value $taskAfter
    if ($taskAfter.State -ne 4 -or -not $taskAfter.AutoStart -or -not $taskAfter.ImageAndAccountMatch -or -not $taskAfter.SecurityMatches -or -not $taskAfter.Recovery.MatchesFixedRecovery -or -not $taskAfter.Heartbeat.LocalSystem -or $taskAfter.Heartbeat.InstanceId -ceq $taskBefore.Heartbeat.InstanceId) { throw 'Post-reboot service identity/security/autostart did not match.' }
    $taskStatus.ServiceAutostartObserved=$true
    if ($taskAfter.Heartbeat.PersistentStartCount -ne ($taskBefore.Heartbeat.PersistentStartCount+1) -or $taskAfter.Heartbeat.PersistentHeartbeatCount -lt $taskBefore.Heartbeat.PersistentHeartbeatCount) { throw 'Test counters did not persist exactly across a single reboot/start.' }
    $taskStatus.TestStatePersisted=$true
    $null=Invoke-AictLimitedServiceProbe -Context $taskContext -Phase 'after-boot'
    $taskStatus.PermissionsPassed=$true
} catch { $taskStatus.Error=$_.Exception.Message; Write-Host ('Finish diagnostic: '+$taskStatus.Error) }
finally {
    if (-not $taskNeedsReboot) {
        try {
            $taskCleanup=Invoke-AictServiceNative -Context $taskContext -Mode '--cleanup' -Tag ('cleanup-finish-'+[DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
            $taskStatus.ServiceAbsent=$taskCleanup.ServiceAbsent
            if ($taskCleanup.ServiceAbsent) { $taskStatus.CleanupTaskRemoved=Remove-AictServiceWatchdog -Context $taskContext }
        } catch { $taskStatus.CleanupError=$_.Exception.Message }
    }
    $taskStatus.FinishPassed=$taskStatus.BootObserved -and $taskStatus.ServiceAutostartObserved -and $taskStatus.TestStatePersisted -and $taskStatus.PermissionsPassed -and $taskStatus.ServiceAbsent -and $taskStatus.CleanupTaskRemoved -and $null -eq $taskStatus.Error
    Export-AictServiceEvidence -Context $taskContext -Status $taskStatus -Stage 'finish'
}
if ($taskNeedsReboot) { Write-Host 'After restarting the guest, from the parent folder run: .\AICT-service-boot-lab-v1\scripts\Finish-ServiceLab.ps1'; return }
if (-not $taskStatus.FinishPassed) { throw 'Service reboot/cleanup test incomplete. Preserve evidence. A retained watchdog must keep its protected directory.' }
Write-Host 'Service-only lab passed and was removed. Copy the entire service-lab-finish-TIMESTAMP folder to project reports.'
Write-Host 'This proves this test service/state only, not AppLocker/WFP boot enforcement, daily quotas, or full product phase0.'
