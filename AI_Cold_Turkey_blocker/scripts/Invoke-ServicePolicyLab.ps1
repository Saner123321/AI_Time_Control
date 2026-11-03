#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][ValidateNotNullOrEmpty()][string]$SnapshotReference,[switch]$SnapshotRestoreConfirmed)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'ServicePolicyLabCommon.ps1')
Assert-AictPolicyGuest
if(-not $SnapshotRestoreConfirmed) { throw 'Restore the powered-off recovery snapshot and sign back in first; explicitly confirm only after the guest desktop returns. No experiment installed.' }
$taskPackage=Split-Path -Parent $PSScriptRoot;$taskManifest=Read-AictPolicyManifest -Package $taskPackage
$taskLocator=Join-Path $taskPackage 'active-service-policy.json'
if(Test-Path -LiteralPath $taskLocator) { throw 'A locator already exists. Recover/inspect this run; do not overwrite or rerun.' }
if((Get-Service -Name AppIDSvc).Status -ne 'Running') { throw 'AppIDSvc is not running. No service/policy experiment installed; report this preflight result.' }
$taskRunId=[guid]::NewGuid().ToString('N');$taskContext=New-AictPolicyContext -Package $taskPackage -RunId $taskRunId
$taskSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$taskOwner=New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544');$taskUser=New-Object Security.Principal.SecurityIdentifier($taskSid)
$taskAcl=New-Object Security.AccessControl.DirectorySecurity;$taskAcl.SetAccessRuleProtection($true,$false);$taskAcl.SetOwner($taskOwner)
foreach($taskPrivilegedSid in @('S-1-5-18','S-1-5-32-544')) { $taskAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($taskPrivilegedSid)),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))) }
$taskAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($taskUser,'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow')))
$null=[IO.Directory]::CreateDirectory($taskContext.Root,$taskAcl)
foreach($taskSub in @('Service','state','Targets','probe')) { $null=New-Item -ItemType Directory -Path (Join-Path $taskContext.Root $taskSub);Set-AictPolicyOwner -Path (Join-Path $taskContext.Root $taskSub) }
$taskProbe=Join-Path $taskContext.Root 'probe';$taskProbeAcl=Get-Acl -LiteralPath $taskProbe;$taskProbeAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($taskUser,'Modify','ContainerInherit,ObjectInherit','None','Allow')));Set-Acl -LiteralPath $taskProbe -AclObject $taskProbeAcl
Copy-Item -LiteralPath (Join-Path $taskPackage 'lab\AictPolicyBridge.exe') -Destination (Join-Path $taskContext.Root 'AictPolicyBridge.exe')
foreach($taskFile in @(Get-ChildItem -LiteralPath (Join-Path $taskPackage 'Service') -File -Recurse)) {
    $taskRelative=$taskFile.FullName.Substring((Join-Path $taskPackage 'Service').Length).TrimStart('\')
    $taskDestination=Join-Path (Join-Path $taskContext.Root 'Service') $taskRelative
    $null=New-Item -ItemType Directory -Path (Split-Path -Parent $taskDestination) -Force
    Copy-Item -LiteralPath $taskFile.FullName -Destination $taskDestination
}
foreach($taskRelative in @('Targets\Target.exe','Targets\Renamed.exe','MovedTarget.exe','Targets\AllowedControl.exe')) {
    $taskSource=if($taskRelative.EndsWith('AllowedControl.exe')) { 'lab\AictPolicyControl.exe' } else { 'lab\AictPolicyTarget.exe' }
    Copy-Item -LiteralPath (Join-Path $taskPackage $taskSource) -Destination (Join-Path $taskContext.Root $taskRelative)
}
Write-AictPolicyJson -Path (Join-Path $taskContext.Root 'config.json') -Value @{runId=$taskRunId;probeSid=$taskSid;targetSipHash=$taskManifest.target_sip_hash;targetSize=$taskManifest.target_size}
[IO.File]::WriteAllText((Join-Path $taskContext.Root 'protection-canary.bin'),'AICT protected permissions canary')
foreach($taskProtectedSub in @('Service','Targets')) {
    foreach($taskItem in @(Get-ChildItem -LiteralPath (Join-Path $taskContext.Root $taskProtectedSub) -Recurse)) { Set-AictPolicyOwner -Path $taskItem.FullName }
}
foreach($taskFixedFile in @('AictPolicyBridge.exe','MovedTarget.exe','config.json','protection-canary.bin')) { Set-AictPolicyOwner -Path (Join-Path $taskContext.Root $taskFixedFile) }
foreach($taskEntry in $taskManifest.files.PSObject.Properties) {
    if($taskEntry.Name.StartsWith('Service/')) { $taskStaged=Join-Path $taskContext.Root $taskEntry.Name }
    elseif($taskEntry.Name -ceq 'lab/AictPolicyBridge.exe') { $taskStaged=Join-Path $taskContext.Root 'AictPolicyBridge.exe' }
    else { continue }
    if((Get-FileHash -LiteralPath $taskStaged -Algorithm SHA256).Hash.ToLowerInvariant() -cne $taskEntry.Value) { throw 'Protected staged runtime/bridge hash mismatch. Nothing installed.' }
}
Write-AictPolicyJson -Path $taskLocator -Value @{runId=$taskRunId}
foreach($taskPath in @($taskContext.Root,"$env:SystemRoot\System32\cmd.exe")) { if($taskPath -match '[%&!^|<>"\r\n]') { throw 'Unexpected metacharacter in fixed command path.' } }
$taskCleanupBatch=Join-Path $taskContext.Root 'Run-cleanup.cmd'
$taskLines=@('@echo off',('"'+(Join-Path $taskContext.Root 'AictPolicyBridge.exe')+'" --cleanup 1>"'+(Join-Path $taskContext.Root 'watchdog-stdout.txt')+'" 2>"'+(Join-Path $taskContext.Root 'watchdog-stderr.txt')+'"'),'set "AICT_CLEANUP_EXIT=%ERRORLEVEL%"','exit /b %AICT_CLEANUP_EXIT%')
[IO.File]::WriteAllLines($taskCleanupBatch,[string[]]$taskLines,[Text.Encoding]::ASCII);Set-AictPolicyOwner -Path $taskCleanupBatch
$taskVersion=Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$taskResult=[ordered]@{kind='AICT_SERVICE_POLICY_LAB_RUNNER';schemaVersion=1;runId=$taskRunId;collectedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');editionId=$taskVersion.EditionID;build=([string]$taskVersion.CurrentBuildNumber+'.'+$taskVersion.UBR);vmwareGuestVerified=$true;snapshotReference=$SnapshotReference;snapshotRestoreOperatorConfirmed=$true;snapshotRestoreIndependentlyVerified=$false;sourceManifestSha256=(Get-FileHash -LiteralPath (Join-Path $taskPackage 'MANIFEST.json') -Algorithm SHA256).Hash.ToLowerInvariant();fixedFixtureOnly=$true;baselinePassed=$false;scheduledBlockPassed=$false;duringCrashDenialObserved=$false;recoveryPassed=$false;limitedPipePassed=$false;restoredLaunchPassed=$false;serviceAbsent=$false;ownPoliciesAbsent=$false;cleanupTaskRemoved=$false;batchPassed=$false;phase0PassAwarded=$false;exportRefusedFiles=@();error=$null;cleanupError=$null}
Write-Host 'Guest-only integration: one inert application identity; other applications are not targets. No Windows reboot is required.'
try {
    $taskAction=New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\cmd.exe" -Argument ('/d /c "'+$taskCleanupBatch+'"')
    $taskOnce=New-ScheduledTaskTrigger -Once -At ((Get-Date).AddMinutes(8));$taskBoot=New-ScheduledTaskTrigger -AtStartup;$taskBoot.Delay='PT2M'
    $taskPrincipal=New-ScheduledTaskPrincipal -UserId 'S-1-5-18' -LogonType ServiceAccount -RunLevel Highest
    $taskSettings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 3) -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    $null=Register-ScheduledTask -TaskName $taskContext.TaskName -Action $taskAction -Trigger @($taskOnce,$taskBoot) -Principal $taskPrincipal -Settings $taskSettings -ErrorAction Stop
    $null=Invoke-AictPolicyProbe -Context $taskContext -Phase 'baseline';$taskResult.baselinePassed=$true
    Write-AictPolicyJson -Path (Join-Path $taskContext.Root 'baseline-passed.json') -Value @{runId=$taskRunId;passed=$true}
    $null=Invoke-AictPolicyNative -Context $taskContext -Mode '--install' -Tag 'install-result'
    $taskBlocked=Invoke-AictPolicyProbe -Context $taskContext -Phase 'blocked';$taskResult.scheduledBlockPassed=$true
    $taskBefore=Read-AictPolicyStatus -Context $taskContext
    if($null -ne $taskBefore.fault -or -not $taskBefore.clockTrusted -or -not $taskBefore.desiredBlocked -or $taskBefore.policyReadbackBlocked -ne $true -or $null -eq $taskBefore.pendingId) { throw 'Service has no verified blocked policy/pending change with a live initial time anchor.' }
    Write-AictPolicyJson -Path (Join-Path $taskContext.Root 'before-crash-state.json') -Value $taskBefore
    $taskFirstBlocked=@($taskBlocked.transitionAttempts | Where-Object { -not $_.started -and $_.nativeErrorCode -eq 1260 })[0]
    $taskUpperBound=([double]$taskFirstBlocked.endTick-[double]$taskBefore.cycleStartTick)/[double]$taskBefore.clockFrequency
    $taskResult.observedScheduledBlockUpperBoundSeconds=$taskUpperBound;$taskResult.observedUnderTwoSeconds=($taskUpperBound -ge 0 -and $taskUpperBound -le 2)
    $taskPipe=Invoke-AictPolicyProbe -Context $taskContext -Phase 'pipe'
    if(-not $taskPipe.stopRejected -or -not $taskPipe.clockInjectionRejected -or $taskPipe.status.runId -cne $taskRunId) { throw 'Limited service pipe check failed.' }
    $taskResult.limitedPipePassed=$true
    $null=Invoke-AictPolicyNative -Context $taskContext -Mode '--inspect' -Tag 'inspect-before-crash'
    $null=Invoke-AictPolicyNative -Context $taskContext -Mode '--crash' -Tag 'crash-request'
    $taskDuring=Invoke-AictPolicyProbe -Context $taskContext -Phase 'during-crash'
    $taskResult.duringCrashDenialObserved=@($taskDuring.trials | Where-Object { $_.expectedBlocked -and $_.expectationMet -and $_.serviceState -ne 4 }).Count -gt 0
    $taskAfter=$null;$taskWait=[Diagnostics.Stopwatch]::StartNew()
    while($taskWait.Elapsed.TotalSeconds -lt 40) {
        try { $taskCandidate=Read-AictPolicyStatus -Context $taskContext;if($taskCandidate.instanceId -cne $taskBefore.instanceId -and $taskCandidate.policyReadbackBlocked -eq $true -and $null -eq $taskCandidate.fault) { $taskAfter=$taskCandidate;break } } catch { }
        Start-Sleep -Milliseconds 100
    }
    if($null -eq $taskAfter -or $taskAfter.clockTrusted -or $taskAfter.pendingId -cne $taskBefore.pendingId -or $taskAfter.pendingTargetSha256 -cne $taskBefore.pendingTargetSha256 -or $taskAfter.pendingNotBeforeUtc -cne $taskBefore.pendingNotBeforeUtc -or $taskAfter.policySha256 -cne $taskBefore.policySha256 -or $taskAfter.storageRevision -le $taskBefore.storageRevision) { throw 'Recovered service did not preserve policy/pending and conservative clock state.' }
    Write-AictPolicyJson -Path (Join-Path $taskContext.Root 'after-crash-state.json') -Value $taskAfter
    $null=Invoke-AictPolicyProbe -Context $taskContext -Phase 'after-crash';$taskResult.recoveryPassed=$true
    $null=Invoke-AictPolicyNative -Context $taskContext -Mode '--inspect' -Tag 'inspect-after-crash'
} catch { $taskResult.error=$_.Exception.Message;Write-Host ('Integration diagnostic: '+$taskResult.error) }
finally {
    try {
        $taskCleanup=Invoke-AictPolicyNative -Context $taskContext -Mode '--cleanup' -Tag 'cleanup-result'
        $taskResult.serviceAbsent=$taskCleanup.serviceAbsent;$taskResult.ownPoliciesAbsent=$taskCleanup.ownPoliciesAbsent
        if($taskResult.serviceAbsent -and $taskResult.ownPoliciesAbsent) {
            $null=Invoke-AictPolicyProbe -Context $taskContext -Phase 'restored';$taskResult.restoredLaunchPassed=$true
            $taskResult.cleanupTaskRemoved=Remove-AictPolicyWatchdog -Context $taskContext
        }
    } catch { $taskResult.cleanupError=$_.Exception.Message }
    $taskResult.batchPassed=$taskResult.baselinePassed -and $taskResult.scheduledBlockPassed -and $taskResult.duringCrashDenialObserved -and $taskResult.recoveryPassed -and $taskResult.limitedPipePassed -and $taskResult.restoredLaunchPassed -and $taskResult.serviceAbsent -and $taskResult.ownPoliciesAbsent -and $taskResult.cleanupTaskRemoved -and $null -eq $taskResult.error -and $null -eq $taskResult.cleanupError
    Export-AictPolicyEvidence -Context $taskContext -Result $taskResult
}
if(-not $taskResult.batchPassed) { throw 'Integration did not fully pass. Preserve evidence; do not rerun or delete the protected directory. Use the recovery command if cleanup was not confirmed.' }
Write-Host 'VM service/policy integration batch passed and cleaned. Full phase0/reboot/usage gates remain pending. Copy the complete service-policy-TIMESTAMP evidence directory back to the host reports folder.'
