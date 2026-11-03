#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][ValidateNotNullOrEmpty()][string]$SnapshotReference)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Guard before creating directories, registering tasks or touching policy.
$taskMachine = Get-CimInstance Win32_ComputerSystem
if ($taskMachine.Manufacturer -notmatch 'VMware' -or $taskMachine.Model -notmatch 'VMware') { throw 'VMware guest required. No files, tasks or policy created.' }
$taskIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$taskPrincipal = New-Object Security.Principal.WindowsPrincipal($taskIdentity)
if (-not $taskPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) -or -not [Environment]::Is64BitProcess -or $PSVersionTable.PSEdition -ne 'Desktop' -or [Diagnostics.Process]::GetCurrentProcess().SessionId -eq 0) { throw 'Run in the guest desktop, administrator 64-bit Windows PowerShell 5.1.' }
$taskSid = $taskIdentity.User.Value
if ($taskSid -in @('S-1-5-18','S-1-5-19','S-1-5-20')) { throw 'An interactive user account is required.' }
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskManifest = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskRoot 'MANIFEST.json') | ConvertFrom-Json
if ($taskManifest.kind -cne 'AICT_APPLOCKER_IDENTITY_LATENCY_V1') { throw 'Wrong package manifest.' }
foreach ($taskEntry in $taskManifest.files.PSObject.Properties) {
    $taskFile = Join-Path $taskRoot $taskEntry.Name
    if ((Get-FileHash -LiteralPath $taskFile -Algorithm SHA256).Hash.ToLowerInvariant() -cne $taskEntry.Value) { throw ('Package file hash mismatch: ' + $taskEntry.Name) }
}
$taskRunId = [guid]::NewGuid().ToString('N')
$taskDirectory = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) ('AICT-Identity-' + $taskRunId)
$taskOutput = Join-Path $taskRoot ('identity-latency-' + [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
$taskOwner = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
$taskUser = New-Object Security.Principal.SecurityIdentifier($taskSid)
$taskAcl = New-Object Security.AccessControl.DirectorySecurity
$taskAcl.SetAccessRuleProtection($true, $false)
$taskAcl.SetOwner($taskOwner)
foreach ($taskAclSid in @('S-1-5-18','S-1-5-32-544')) {
    $taskAclIdentity = New-Object Security.Principal.SecurityIdentifier($taskAclSid)
    $taskAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($taskAclIdentity,'FullControl','ContainerInherit,ObjectInherit','None','Allow')))
}
# Limited probe can read fixtures/config, but cannot write policy signals or binaries.
$taskAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($taskUser,'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow')))
$null = [IO.Directory]::CreateDirectory($taskDirectory,$taskAcl)
$null = New-Item -ItemType Directory -Path $taskOutput -ErrorAction Stop
$null = New-Item -ItemType Directory -Path (Join-Path $taskDirectory 'Targets') -ErrorAction Stop
$taskProbeDirectory = Join-Path $taskDirectory 'probe'
$null = New-Item -ItemType Directory -Path $taskProbeDirectory -ErrorAction Stop
$taskProbeAcl = Get-Acl -LiteralPath $taskProbeDirectory
$taskProbeAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($taskUser,'Modify','ContainerInherit,ObjectInherit','None','Allow')))
$taskProbeAcl.SetOwner($taskOwner)
Set-Acl -LiteralPath $taskProbeDirectory -AclObject $taskProbeAcl
$taskTargetsAcl=Get-Acl -LiteralPath (Join-Path $taskDirectory 'Targets')
$taskTargetsAcl.SetOwner($taskOwner)
Set-Acl -LiteralPath (Join-Path $taskDirectory 'Targets') -AclObject $taskTargetsAcl
$taskWorker = Join-Path $taskDirectory 'AictAppLockerIdentityLatency.exe'
Copy-Item -LiteralPath (Join-Path $taskRoot 'lab\AictAppLockerIdentityLatency.exe') -Destination $taskWorker
$taskFixtureSource = Join-Path $taskRoot 'lab\AictIdentityTarget.exe'
foreach ($taskRelative in @('Targets\Target.exe','Targets\Renamed.exe','Targets\AllowedControl.exe','MovedTarget.exe')) {
    $taskSelectedSource=if ($taskRelative -eq 'Targets\AllowedControl.exe') { Join-Path $taskRoot 'lab\AictIdentityControl.exe' } else { $taskFixtureSource }
    Copy-Item -LiteralPath $taskSelectedSource -Destination (Join-Path $taskDirectory $taskRelative)
}
$taskUtf8 = New-Object Text.UTF8Encoding($false)
$taskConfig = [ordered]@{RunId=$taskRunId; ProbeSid=$taskSid; FixtureSha256=$taskManifest.files.'lab/AictIdentityTarget.exe'}
[IO.File]::WriteAllText((Join-Path $taskDirectory 'config.json'),($taskConfig | ConvertTo-Json),$taskUtf8)
$taskCmd = "$env:SystemRoot\System32\cmd.exe"
foreach ($taskPath in @($taskDirectory,$taskWorker,$taskCmd)) { if ($taskPath -match '[%&!^|<>"\r\n]') { throw 'Unexpected shell metacharacter in fixed native lab path.' } }
foreach ($taskMode in @('enforce','cleanup','probe')) {
    $taskBatch = Join-Path $taskDirectory ('Run-' + $taskMode + '.cmd')
    $taskLogDirectory=if ($taskMode -eq 'probe') { $taskProbeDirectory } else { $taskDirectory }
    $taskLines = @('@echo off',('"' + $taskWorker + '" --' + $taskMode + ' 1>"' + (Join-Path $taskLogDirectory ($taskMode + '-stdout.txt')) + '" 2>"' + (Join-Path $taskLogDirectory ($taskMode + '-stderr.txt')) + '"'),'set "AICT_LAB_EXIT=%ERRORLEVEL%"',('>"' + (Join-Path $taskLogDirectory ($taskMode + '-exitcode.txt')) + '" echo %AICT_LAB_EXIT%'),'exit /b %AICT_LAB_EXIT%')
    [IO.File]::WriteAllLines($taskBatch,[string[]]$taskLines,[Text.Encoding]::ASCII)
}
# Stage ownership for every elevated-executable/input file; keep limited probe output separate.
foreach ($taskStageFile in @(Get-ChildItem -LiteralPath $taskDirectory -File -Recurse)) {
    $taskStageAcl = Get-Acl -LiteralPath $taskStageFile.FullName
    $taskStageAcl.SetOwner($taskOwner)
    Set-Acl -LiteralPath $taskStageFile.FullName -AclObject $taskStageAcl
}
if ((Get-FileHash -LiteralPath $taskWorker -Algorithm SHA256).Hash.ToLowerInvariant() -cne $taskManifest.files.'lab/AictAppLockerIdentityLatency.exe') { throw 'Staged worker mismatch.' }
$taskSystemPrincipal = New-ScheduledTaskPrincipal -UserId 'S-1-5-18' -LogonType ServiceAccount -RunLevel Highest
$taskProbePrincipal = New-ScheduledTaskPrincipal -UserId $taskSid -LogonType Interactive -RunLevel Limited
$taskSettings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 8) -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
$taskEnforceName = 'AICT-Identity-Worker-' + $taskRunId
$taskProbeName = 'AICT-Identity-Probe-' + $taskRunId
$taskCleanupName = 'AICT-Identity-Cleanup-' + $taskRunId
$taskEnforceAction = New-ScheduledTaskAction -Execute $taskCmd -Argument ('/d /c "' + (Join-Path $taskDirectory 'Run-enforce.cmd') + '"')
$taskCleanupAction = New-ScheduledTaskAction -Execute $taskCmd -Argument ('/d /c "' + (Join-Path $taskDirectory 'Run-cleanup.cmd') + '"')
$taskProbeAction = New-ScheduledTaskAction -Execute $taskCmd -Argument ('/d /c "' + (Join-Path $taskDirectory 'Run-probe.cmd') + '"')
$taskWindowsVersion=Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$taskStatus = [ordered]@{
    SchemaVersion=1; Kind='APPLOCKER_IDENTITY_LATENCY_RUNNER'; RunId=$taskRunId
    CollectedAtUtc=[DateTimeOffset]::UtcNow.ToString('o'); SnapshotReferenceOperatorSupplied=$SnapshotReference; SnapshotRestoreVerified=$false
    EditionId=$taskWindowsVersion.EditionID
    Build=([string]$taskWindowsVersion.CurrentBuildNumber + '.' + [string]$taskWindowsVersion.UBR); ProtectedLabDirectory=$taskDirectory
    VmwareGuestVerified=$true; FixtureSha256=$taskManifest.files.'lab/AictIdentityTarget.exe'
    ControlSha256=$taskManifest.files.'lab/AictIdentityControl.exe'; TargetSipHash=$taskManifest.target_sip_hash
    WorkerSha256=$taskManifest.files.'lab/AictAppLockerIdentityLatency.exe'
    PolicyScope='FIXTURE_AUTHENTICODE_SHA256_ONLY'; ProbeUsesInteractiveLimitedTask=$true
    NativeExitCode=$null; WorkerOutcome='NO_WORKER_RESULT'; BatchPassed=$false; FunctionalPassed=$false; TimingTargetMet=$false; CleanupConfirmed=$false
    CleanupWatchdogRegistered=$false; CleanupWatchdogRemoved=$false; WorkerTaskRemoved=$false; ProbeTaskRemoved=$false
    IdentityBypassResistanceProven=$false; Phase0PassAwarded=$false; Error=$null
}
$taskRegistered = @{}
$taskCanRemoveWatchdog = $false
Write-Host 'Temporary guest Authenticode hash experiment. Only this package-generated target binary is denied, including renamed/moved copies.'
Write-Host 'One limited interactive task runs 20 apply/remove cycles, with separate different-content controls and 172 formal launch trials.'
Write-Host 'A companion allow-all signed packaged-app rule is installed before EXE enforcement and removed after it.'
Write-Host 'Execution policy, service configuration and browser/network policy are unchanged.'
try {
    # Independent cleanup runs after ten minutes or next startup; normal test is usually much shorter.
    $taskTriggers = @((New-ScheduledTaskTrigger -Once -At ((Get-Date).AddMinutes(10))),(New-ScheduledTaskTrigger -AtStartup))
    $null = Register-ScheduledTask -TaskName $taskCleanupName -Action $taskCleanupAction -Trigger $taskTriggers -Principal $taskSystemPrincipal -Settings $taskSettings -ErrorAction Stop
    $taskRegistered[$taskCleanupName] = $true; $taskStatus.CleanupWatchdogRegistered = $true
    $null = Register-ScheduledTask -TaskName $taskProbeName -Action $taskProbeAction -Principal $taskProbePrincipal -Settings $taskSettings -ErrorAction Stop
    $taskRegistered[$taskProbeName] = $true
    $null = Register-ScheduledTask -TaskName $taskEnforceName -Action $taskEnforceAction -Principal $taskSystemPrincipal -Settings $taskSettings -ErrorAction Stop
    $taskRegistered[$taskEnforceName] = $true
    Start-ScheduledTask -TaskName $taskProbeName -ErrorAction Stop
    Start-ScheduledTask -TaskName $taskEnforceName -ErrorAction Stop
    $taskExitPath = Join-Path $taskDirectory 'enforce-exitcode.txt'
    $taskDeadline = [DateTimeOffset]::UtcNow.AddSeconds(475)
    while (-not (Test-Path -LiteralPath $taskExitPath) -and [DateTimeOffset]::UtcNow -lt $taskDeadline) { Start-Sleep -Milliseconds 500 }
    if (Test-Path -LiteralPath $taskExitPath) { $taskStatus.NativeExitCode = [int]((Get-Content -Raw -Encoding UTF8 -LiteralPath $taskExitPath).Trim()) }
    $taskResultFile = Join-Path $taskDirectory 'worker-result.json'
    if (-not (Test-Path -LiteralPath $taskResultFile)) { throw 'Worker result missing; preserve the lab directory and let independent cleanup run.' }
    $taskResult = Get-Content -Raw -Encoding UTF8 -LiteralPath $taskResultFile | ConvertFrom-Json
    if ($taskResult.Kind -cne 'APPLOCKER_IDENTITY_LATENCY_SMOKE' -or $taskResult.RunId -cne $taskRunId) { throw 'Worker identity mismatch.' }
    $taskStatus.WorkerOutcome=$taskResult.Outcome
    $taskStatus.CleanupConfirmed=$taskResult.OwnInstancesAbsent
    $taskCanRemoveWatchdog=$taskResult.OwnInstancesAbsent
    $taskStatus.FunctionalPassed=$taskResult.FunctionalPassed
    $taskStatus.TimingTargetMet=$taskResult.TimingTargetMet
    $taskStatus.BatchPassed=$taskResult.BatchPassed -and $taskStatus.NativeExitCode -eq 0
    $taskStatus.CompletedCycles=$taskResult.CompletedCycles
    Write-Host ('Worker outcome: ' + $taskStatus.WorkerOutcome)
    foreach ($taskField in @('Error','CleanupError','RecoveryError')) {
        if ($taskResult.PSObject.Properties.Name -contains $taskField -and $null -ne $taskResult.$taskField) { Write-Host ($taskField + ': ' + ($taskResult.$taskField | ConvertTo-Json -Compress)) }
    }
} catch { $taskStatus.Error=$_.Exception.Message; Write-Host ('Experiment diagnostic: ' + $taskStatus.Error) }
finally {
    # Stop only the tasks just created by this invocation, never discover/delete by prefix.
    foreach ($taskRemoval in @(@{Name=$taskEnforceName;Field='WorkerTaskRemoved'},@{Name=$taskProbeName;Field='ProbeTaskRemoved'})) {
        if ($taskRegistered.ContainsKey($taskRemoval.Name)) {
            try {
                $taskStatus[$taskRemoval.Field + 'LastResult']=(Get-ScheduledTaskInfo -TaskName $taskRemoval.Name).LastTaskResult
                if ((Get-ScheduledTask -TaskName $taskRemoval.Name).State -eq 'Running') { Stop-ScheduledTask -TaskName $taskRemoval.Name -ErrorAction Stop }
                Unregister-ScheduledTask -TaskName $taskRemoval.Name -Confirm:$false -ErrorAction Stop
                $taskStatus[$taskRemoval.Field]=$null -eq (Get-ScheduledTask -TaskName $taskRemoval.Name -ErrorAction SilentlyContinue)
            } catch { $taskStatus[$taskRemoval.Field + 'Error']=$_.Exception.Message }
        }
    }
    if ($taskRegistered.ContainsKey($taskCleanupName) -and $taskCanRemoveWatchdog) {
        try {
            Unregister-ScheduledTask -TaskName $taskCleanupName -Confirm:$false -ErrorAction Stop
            $taskStatus.CleanupWatchdogRemoved=$null -eq (Get-ScheduledTask -TaskName $taskCleanupName -ErrorAction SilentlyContinue)
        } catch { $taskStatus.WatchdogCleanupError=$_.Exception.Message }
    }
    # Export only log entries naming this run's generated sample path, never unrelated app paths.
    try {
        . (Join-Path $PSScriptRoot 'Read-AppLockerLabEvent.ps1')
        $taskObservedEvents = @()
        $taskEvents = @(Get-WinEvent -FilterHashtable @{LogName='Microsoft-Windows-AppLocker/EXE and DLL'; StartTime=([DateTimeOffset]::Parse($taskStatus.CollectedAtUtc).LocalDateTime)} -MaxEvents 256 -ErrorAction Stop)
        foreach ($taskEvent in $taskEvents) {
            $taskEventData=ConvertFrom-AppLockerLabEventXml -Xml ($taskEvent.ToXml()) -RunId $taskRunId
            if ($null -ne $taskEventData) {
                $taskObservedEvents += [ordered]@{Id=$taskEvent.Id; RecordId=$taskEvent.RecordId; TimeCreatedUtc=$taskEvent.TimeCreated.ToUniversalTime().ToString('o'); Data=$taskEventData}
            }
        }
        [IO.File]::WriteAllText((Join-Path $taskOutput 'applocker-events.json'),(ConvertTo-Json -InputObject @($taskObservedEvents) -Depth 8),$taskUtf8)
        $taskStatus.MatchingEventCount=$taskObservedEvents.Count
    } catch { $taskStatus.EventCollectionNote=$_.Exception.Message }
    foreach ($taskLog in @(Get-ChildItem -LiteralPath $taskDirectory -File | Where-Object { $_.Extension -in @('.json','.jsonl','.txt') -and $_.Name -ne 'config.json' })) {
        Copy-Item -LiteralPath $taskLog.FullName -Destination (Join-Path $taskOutput $taskLog.Name)
    }
    foreach ($taskLog in @(Get-ChildItem -LiteralPath $taskProbeDirectory -File | Where-Object { $_.Extension -in @('.json','.txt') })) {
        Copy-Item -LiteralPath $taskLog.FullName -Destination (Join-Path $taskOutput ('probe-' + $taskLog.Name))
    }
    [IO.File]::WriteAllText((Join-Path $taskOutput 'runner-result.json'),($taskStatus | ConvertTo-Json -Depth 10),$taskUtf8)
    $taskHashes = @(Get-ChildItem -LiteralPath $taskOutput -File | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Name })
    [IO.File]::WriteAllLines((Join-Path $taskOutput 'SHA256SUMS.txt'),[string[]]$taskHashes,$taskUtf8)
}
Write-Host ('Evidence directory: ' + $taskOutput)
if ($taskStatus.CleanupWatchdogRegistered -and -not $taskStatus.CleanupWatchdogRemoved) {
    Write-Host ('Cleanup watchdog retained: ' + $taskCleanupName + '. Runs ten minutes after test start or next boot; keep the protected directory. See docs/APPLOCKER_IDENTITY_LATENCY.md.')
}
Write-Host ('Identity functional passed: ' + $taskStatus.FunctionalPassed + '; timing target met: ' + $taskStatus.TimingTargetMet + '. Full phase0/service/reboot/browser gates remain pending.')
Write-Host 'Copy the complete identity-latency-TIMESTAMP directory to the project reports directory.'
if (-not $taskStatus.BatchPassed -or -not $taskStatus.CleanupWatchdogRemoved -or -not $taskStatus.WorkerTaskRemoved -or -not $taskStatus.ProbeTaskRemoved) { throw 'Identity/timing experiment incomplete or target not met; preserve evidence.' }
