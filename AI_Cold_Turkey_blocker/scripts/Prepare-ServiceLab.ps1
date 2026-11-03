#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][ValidateNotNullOrEmpty()][string]$SnapshotReference)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'ServiceLabCommon.ps1')
Assert-AictServiceGuest
$taskPackage=Split-Path -Parent $PSScriptRoot
$taskManifest=Read-AictServiceManifest -Package $taskPackage
$taskLocator=Join-Path $taskPackage 'ACTIVE-SERVICE-LAB.json'
if (Test-Path -LiteralPath $taskLocator) { throw 'This extraction already has an experiment. Finish/recover it first; do not overwrite its locator.' }
$taskRunId=[guid]::NewGuid().ToString('N')
$taskContext=New-AictServiceContext -Package $taskPackage -RunId $taskRunId -Manifest $taskManifest
$taskOwner=New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
$taskSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$taskUser=New-Object Security.Principal.SecurityIdentifier($taskSid)
$taskAcl=New-Object Security.AccessControl.DirectorySecurity
$taskAcl.SetAccessRuleProtection($true,$false); $taskAcl.SetOwner($taskOwner)
foreach ($taskAclSid in @('S-1-5-18','S-1-5-32-544')) { $taskAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($taskAclSid)),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))) }
$taskAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($taskUser,'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow')))
$null=[IO.Directory]::CreateDirectory($taskContext.Root,$taskAcl)
foreach ($taskSub in @('state','probe')) { $null=New-Item -ItemType Directory -Path (Join-Path $taskContext.Root $taskSub); Set-AictLabOwner -Path (Join-Path $taskContext.Root $taskSub) }
$taskProbeDirectory=Join-Path $taskContext.Root 'probe'
$taskProbeAcl=Get-Acl -LiteralPath $taskProbeDirectory
$taskProbeAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($taskUser,'Modify','ContainerInherit,ObjectInherit','None','Allow')))
Set-Acl -LiteralPath $taskProbeDirectory -AclObject $taskProbeAcl
Copy-Item -LiteralPath (Join-Path $taskPackage 'lab\AictServiceLab.exe') -Destination (Join-Path $taskContext.Root 'AictServiceLab.exe')
Set-AictLabOwner -Path (Join-Path $taskContext.Root 'AictServiceLab.exe')
Write-AictLabJson -Path (Join-Path $taskContext.Root 'config.json') -Value @{RunId=$taskRunId;ProbeSid=$taskSid}
[IO.File]::WriteAllText((Join-Path $taskContext.Root 'protection-canary.bin'),'AICT file permissions canary')
Set-AictLabOwner -Path (Join-Path $taskContext.Root 'protection-canary.bin')
foreach ($taskPath in @($taskContext.Root,"$env:SystemRoot\System32\cmd.exe")) { if ($taskPath -match '[%&!^|<>"\r\n]') { throw 'Unexpected shell characters in fixed lab paths.' } }
if ((Get-FileHash -LiteralPath (Join-Path $taskContext.Root 'AictServiceLab.exe') -Algorithm SHA256).Hash.ToLowerInvariant() -cne $taskManifest.files.'lab/AictServiceLab.exe') { throw 'Staged binary mismatch.' }
Write-AictLabJson -Path $taskLocator -Value @{RunId=$taskRunId;ProtectedLabDirectory=$taskContext.Root}
$taskCleanupBatch=Join-Path $taskContext.Root 'Run-cleanup.cmd'
$taskCleanupLines=@('@echo off',('"'+(Join-Path $taskContext.Root 'AictServiceLab.exe')+'" --cleanup 1>"'+(Join-Path $taskContext.Root 'watchdog-stdout.txt')+'" 2>"'+(Join-Path $taskContext.Root 'watchdog-stderr.txt')+'"'),'set "AICT_CLEANUP_EXIT=%ERRORLEVEL%"',('>"'+(Join-Path $taskContext.Root 'watchdog-exitcode.txt')+'" echo %AICT_CLEANUP_EXIT%'),'exit /b %AICT_CLEANUP_EXIT%')
[IO.File]::WriteAllLines($taskCleanupBatch,[string[]]$taskCleanupLines,[Text.Encoding]::ASCII); Set-AictLabOwner -Path $taskCleanupBatch
$taskVersion=Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$taskStatus=[ordered]@{SchemaVersion=1;Kind='AICT_SERVICE_LAB_RUNNER';Stage='Prepare';RunId=$taskRunId;CollectedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');SnapshotReferenceOperatorSupplied=$SnapshotReference;SnapshotRestoreVerified=$false;VmwareGuestVerified=$true;EditionId=$taskVersion.EditionID;Build=([string]$taskVersion.CurrentBuildNumber+'.'+$taskVersion.UBR);WorkerSha256=$taskManifest.files.'lab/AictServiceLab.exe';CleanupTaskName=$taskContext.CleanupTask;ProtectedLabDirectory=$taskContext.Root;NoBlockingPolicyApplied=$true;PermissionsPassed=$false;CrashRecoveryPassed=$false;PreparePassed=$false;ServiceLeftForReboot=$false;CleanupTaskRegistered=$false;ServiceAbsent=$false;CleanupTaskRemoved=$false;Error=$null}
$taskLeaveService=$false
Write-Host 'Guest service-only lab: permissions, one intentional crash and recovery. No application/site blocking policy is applied.'
try {
    $taskAction=New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\cmd.exe" -Argument ('/d /c "'+$taskCleanupBatch+'"')
    $taskBootTrigger=New-ScheduledTaskTrigger -AtStartup; $taskBootTrigger.Delay='PT10M'
    $taskOnceTrigger=New-ScheduledTaskTrigger -Once -At ((Get-Date).AddMinutes(30))
    $taskPrincipal=New-ScheduledTaskPrincipal -UserId 'S-1-5-18' -LogonType ServiceAccount -RunLevel Highest
    $taskSettings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 2) -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    $null=Register-ScheduledTask -TaskName $taskContext.CleanupTask -Action $taskAction -Trigger @($taskOnceTrigger,$taskBootTrigger) -Principal $taskPrincipal -Settings $taskSettings -ErrorAction Stop
    $taskStatus.CleanupTaskRegistered=$true
    $null=Invoke-AictServiceNative -Context $taskContext -Mode '--install' -Tag 'install-result'
    $taskBefore=Invoke-AictServiceNative -Context $taskContext -Mode '--inspect' -Tag 'inspect-before-crash'
    $null=Invoke-AictLimitedServiceProbe -Context $taskContext -Phase 'before-crash'
    $taskStatus.PermissionsPassed=$true
    $null=Invoke-AictServiceNative -Context $taskContext -Mode '--crash' -Tag 'crash-request'
    $taskClock=[Diagnostics.Stopwatch]::StartNew(); $taskAfter=$null
    while ($taskClock.Elapsed.TotalSeconds -lt 25) {
        Start-Sleep -Milliseconds 200
        try {
            $taskCandidate=Get-AictServiceState -Context $taskContext
            if ($taskCandidate.InstanceId -cne $taskBefore.Heartbeat.InstanceId -and $taskCandidate.ServicePhase -eq 'Running' -and $taskCandidate.PersistentStartCount -gt $taskBefore.Heartbeat.PersistentStartCount -and (Get-Service -Name ('AICT-Lab-'+$taskRunId)).Status -eq 'Running') { $taskAfter=$taskCandidate;$taskClock.Stop();break }
        } catch { }
    }
    if ($null -eq $taskAfter) { throw 'SCM recovery did not produce a new running instance within 25 seconds.' }
    $taskInspection=Invoke-AictServiceNative -Context $taskContext -Mode '--inspect' -Tag 'inspect-after-crash'
    $null=Invoke-AictLimitedServiceProbe -Context $taskContext -Phase 'after-crash'
    if ($taskInspection.State -ne 4 -or $taskInspection.ProcessId -eq $taskBefore.ProcessId -or $taskInspection.Heartbeat.BootTimeUtc -cne $taskBefore.Heartbeat.BootTimeUtc -or $taskInspection.Heartbeat.PersistentStartCount -ne ($taskBefore.Heartbeat.PersistentStartCount+1) -or $taskInspection.Heartbeat.PersistentHeartbeatCount -lt $taskBefore.Heartbeat.PersistentHeartbeatCount -or -not $taskInspection.SecurityMatches -or -not $taskInspection.Recovery.MatchesFixedRecovery) { throw 'Recovery identity/security/readback did not match.' }
    $taskStatus.CrashRecoveryPassed=$true; $taskStatus.RecoveryObservedSeconds=$taskClock.Elapsed.TotalSeconds
    $taskStatus.PreparePassed=$true; $taskStatus.ServiceLeftForReboot=$true
    Write-AictLabJson -Path (Join-Path $taskContext.Root 'prepare-summary.json') -Value $taskStatus
    $taskLeaveService=$true
} catch { $taskStatus.Error=$_.Exception.Message; Write-Host ('Prepare diagnostic: '+$taskStatus.Error) }
finally {
    if (-not $taskLeaveService) {
        try { $taskCleanup=Invoke-AictServiceNative -Context $taskContext -Mode '--cleanup' -Tag ('cleanup-prepare-'+[DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')); $taskStatus.ServiceAbsent=$taskCleanup.ServiceAbsent;if ($taskCleanup.ServiceAbsent) { $taskStatus.CleanupTaskRemoved=Remove-AictServiceWatchdog -Context $taskContext } }
        catch { $taskStatus.CleanupError=$_.Exception.Message }
    }
    Export-AictServiceEvidence -Context $taskContext -Status $taskStatus -Stage 'prepare'
}
if (-not $taskStatus.PreparePassed) { throw 'Prepare did not pass. Preserve its evidence; do not restart or rerun blindly.' }
Write-Host 'Prepare passed. Restart ONLY this virtual machine, sign back into the same account, then run the Finish command from the parent of the extracted folder.'
Write-Host '.\AICT-service-boot-lab-v1\scripts\Finish-ServiceLab.ps1'
Write-Host 'Finish promptly: independent cleanup runs 30 minutes after preparation or 10 minutes after the next startup. Keep the protected directory.'
