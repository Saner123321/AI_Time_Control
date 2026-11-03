#Requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'BootPolicyLabCommon.ps1')
Assert-AictPolicyGuest
$taskPackage=Split-Path -Parent $PSScriptRoot;$taskManifest=Read-AictPolicyManifest -Package $taskPackage -ExpectedKind 'AICT_BOOT_POLICY_LAB_V1'
$taskContext=Read-AictBootContext -Package $taskPackage -Manifest $taskManifest
$taskPrepare=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskContext.Root 'boot-prepare-summary.json') | ConvertFrom-Json
if($taskPrepare.runId -cne $taskContext.RunId -or -not $taskPrepare.preparePassed) { throw 'No matching successful preparation. Preserve the failure evidence.' }
$taskResult=[ordered]@{kind='AICT_BOOT_POLICY_LAB_RUNNER';schemaVersion=1;stage='Finish';runId=$taskContext.RunId;collectedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');editionId=$taskPrepare.editionId;build=$taskPrepare.build;vmwareGuestVerified=$true;snapshotReference=$taskPrepare.snapshotReference;repeatSnapshotRestoreRequired=$false;sourceManifestSha256=$taskPrepare.sourceManifestSha256;serviceRuntimeUnchanged=$taskManifest.service_runtime_unchanged;fixedFixtureOnly=$true;normalWindowsRebootOperatorRequested=$true;bootChanged=$false;automaticStartObserved=$false;logonWindowPassed=$false;statePreserved=$false;permissionsPassed=$false;limitedPipePassed=$false;restoredLaunchPassed=$false;serviceAbsent=$false;ownPoliciesAbsent=$false;cleanupTaskRemoved=$false;windowTaskRemoved=$false;finishPassed=$false;phase0PassAwarded=$false;powerLossProven=$false;realUsagePersistenceProven=$false;preLogonCoverageProven=$false;exportRefusedFiles=@();error=$null;cleanupError=$null}
$taskNeedsReboot=$false
Write-Host 'Collecting actual Windows reboot/logon observations, then removing only this experiment.'
try {
    $taskBootBefore=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskContext.Root 'boot-before.json') | ConvertFrom-Json
    $taskBootAfter=Invoke-AictPolicyNative -Context $taskContext -Mode '--boot-metadata'
    if(([DateTimeOffset]::Parse($taskBootAfter.bootTimeUtc)).UtcDateTime.Ticks -eq ([DateTimeOffset]::Parse($taskBootBefore.bootTimeUtc)).UtcDateTime.Ticks) { $taskNeedsReboot=$true;throw 'The Windows boot time has not changed. Restart the guest and run Finish again; preparation is retained until watchdog cleanup.' }
    Write-AictPolicyJson -Path (Join-Path $taskContext.Root 'boot-after.json') -Value $taskBootAfter;$taskResult.bootChanged=$true
    $taskShutdown=Read-AictBootShutdownEvidence -Context $taskContext -StartUtc $taskPrepare.collectedAtUtc -BootUtc $taskBootAfter.bootTimeUtc
    if(-not $taskShutdown.querySucceeded -or -not $taskShutdown.shutdownInitiatedEventObserved -or -not $taskShutdown.eventLogCleanStopObserved) { throw 'The normal Windows shutdown/restart metadata is incomplete. Boot change alone is not treated as a clean restart.' }
    if($taskBootBefore.appIdStartType -ne $taskBootAfter.appIdStartType) { throw 'The system AppIDSvc startup setting changed; this test only configures its own service dependency.' }
    $taskWindow=Read-AictBootWindow -Context $taskContext
    if(([DateTimeOffset]::Parse($taskWindow.windowsBootTimeUtc)).UtcDateTime.Ticks -ne ([DateTimeOffset]::Parse($taskBootAfter.bootTimeUtc)).UtcDateTime.Ticks) { throw 'The automatic logon observations belong to a different Windows boot.' }
    $taskResult.logonWindowPassed=$true
    $taskBefore=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskContext.Root 'before-boot-state.json') | ConvertFrom-Json
    $taskAfter=$null;$taskWait=[Diagnostics.Stopwatch]::StartNew()
    while($taskWait.Elapsed.TotalSeconds -lt 60) {
        try { $taskCandidate=Read-AictPolicyStatus -Context $taskContext
            if($taskCandidate.instanceId -cne $taskBefore.instanceId -and $taskCandidate.policyReadbackBlocked -eq $true -and $null -eq $taskCandidate.fault) { $taskAfter=$taskCandidate;break }
        } catch { }
        Start-Sleep -Milliseconds 100
    }
    if($null -eq $taskAfter) { throw 'The automatic service did not recover a healthy blocked-policy readback within the observation window.' }
    $taskInspect=Invoke-AictPolicyNative -Context $taskContext -Mode '--inspect' -Tag 'inspect-after-boot'
    if($taskInspect.state -ne 4 -or $taskInspect.startType -ne 2 -or @($taskInspect.dependencies).Count -ne 1 -or $taskInspect.dependencies[0] -ine 'AppIDSvc' -or -not $taskInspect.imageAndAccountMatch -or -not $taskInspect.serviceSecurityMatches -or -not $taskInspect.recoveryMatches -or $taskInspect.processId -ne $taskAfter.processId) { throw 'Automatic service identity/start type/dependency/security mismatch.' }
    $taskResult.automaticStartObserved=$true
    if($taskAfter.clockTrusted -or -not $taskAfter.desiredBlocked -or $taskAfter.storageRevision -le $taskBefore.storageRevision -or $taskAfter.policySha256 -cne $taskBefore.policySha256 -or $taskAfter.pendingId -cne $taskBefore.pendingId -or $taskAfter.pendingTargetSha256 -cne $taskBefore.pendingTargetSha256 -or $taskAfter.pendingNotBeforeUtc -cne $taskBefore.pendingNotBeforeUtc) { throw 'Rules or frozen modification changed across the Windows reboot, or the restart clock was incorrectly trusted.' }
    Write-AictPolicyJson -Path (Join-Path $taskContext.Root 'after-boot-state.json') -Value $taskAfter;$taskResult.statePreserved=$true
    $null=Invoke-AictPolicyProbe -Context $taskContext -Phase 'after-boot';$taskResult.permissionsPassed=$true
    $taskPipe=Invoke-AictPolicyProbe -Context $taskContext -Phase 'pipe'
    if(-not $taskPipe.stopRejected -or -not $taskPipe.clockInjectionRejected -or $taskPipe.status.processId -ne $taskInspect.processId) { throw 'Limited status pipe authority checks failed after reboot.' }
    $taskResult.limitedPipePassed=$true
} catch { $taskResult.error=$_.Exception.Message;Write-Host ('Finish diagnostic: '+$taskResult.error) }
finally {
    if(-not $taskNeedsReboot) {
        try {
            $taskCleanup=Invoke-AictPolicyNative -Context $taskContext -Mode '--cleanup' -Tag 'cleanup-finish'
            $taskResult.serviceAbsent=$taskCleanup.serviceAbsent;$taskResult.ownPoliciesAbsent=$taskCleanup.ownPoliciesAbsent
            if($taskResult.serviceAbsent -and $taskResult.ownPoliciesAbsent) {
                $taskResult.windowTaskRemoved=Remove-AictBootWindow -Context $taskContext
                $null=Invoke-AictPolicyProbe -Context $taskContext -Phase 'restored';$taskResult.restoredLaunchPassed=$true
                $taskResult.cleanupTaskRemoved=Remove-AictPolicyWatchdog -Context $taskContext
            }
        } catch { $taskResult.cleanupError=$_.Exception.Message }
    }
    $taskResult.finishPassed=$taskResult.bootChanged -and $taskResult.automaticStartObserved -and $taskResult.logonWindowPassed -and $taskResult.statePreserved -and $taskResult.permissionsPassed -and $taskResult.limitedPipePassed -and $taskResult.restoredLaunchPassed -and $taskResult.serviceAbsent -and $taskResult.ownPoliciesAbsent -and $taskResult.windowTaskRemoved -and $taskResult.cleanupTaskRemoved -and $null -eq $taskResult.error -and $null -eq $taskResult.cleanupError
    Export-AictPolicyEvidence -Context $taskContext -Result $taskResult -Prefix 'boot-policy-finish'
}
if($taskNeedsReboot) { Write-Host 'After restarting only the guest, run .\AICT-boot-policy-lab-v1\scripts\Finish-BootPolicyLab.ps1';return }
if(-not $taskResult.finishPassed) { throw 'Boot-policy experiment incomplete. Preserve all evidence. Use Recover if owned policy/service cleanup was not confirmed.' }
Write-Host 'Normal Windows reboot/logon batch passed and cleaned. Copy the complete boot-policy-finish-TIMESTAMP folder to the host reports directory.'
Write-Host 'This does not prove pre-logon/cold-boot/sleep/power-loss/real-usage coverage or full phase0.'
