#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][ValidateNotNullOrEmpty()][string]$SnapshotReference)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskMachine = Get-CimInstance Win32_ComputerSystem
if ($taskMachine.Manufacturer -notmatch 'VMware' -or $taskMachine.Model -notmatch 'VMware') { throw 'VMware guest required. No task or policy created.' }
$taskPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $taskPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) -or -not [Environment]::Is64BitProcess -or $PSVersionTable.PSEdition -ne 'Desktop') { throw 'Use administrator 64-bit Windows PowerShell 5.1 inside the guest.' }
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskManifest = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskRoot 'MANIFEST.json') | ConvertFrom-Json
$taskSource = Join-Path $taskRoot 'lab\AictAppLockerNativeAudit.exe'
$taskExpectedHash = $taskManifest.files.'lab/AictAppLockerNativeAudit.exe'
if ((Get-FileHash -LiteralPath $taskSource -Algorithm SHA256).Hash.ToLowerInvariant() -cne $taskExpectedHash) { throw 'Native worker hash mismatch.' }
$taskRunId = [guid]::NewGuid().ToString('N')
$taskDirectory = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) ('AICT-CspNative-' + $taskRunId)
$taskOutput = Join-Path $taskRoot ('csp-native-audit-' + [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
$taskProbeName = 'AICT-Native-Probe-' + $taskRunId
$taskCleanupName = 'AICT-Native-Cleanup-' + $taskRunId
$taskLegacyId = '375860026bf2434088478e4ac27f57fe'
$taskLegacyTaskName = 'AICT-Phase0-Cleanup-' + $taskLegacyId
$taskStatus = [ordered]@{
    SchemaVersion = 2
    Kind = 'APPLOCKER_NATIVE_AUDIT_ONLY_LAB_RUNNER'
    CollectedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    SnapshotReferenceOperatorSupplied = $SnapshotReference
    SnapshotRestoreVerified = $false
    RunId = $taskRunId
    ProtectedLabDirectory = $taskDirectory
    AuditTaskName = $taskProbeName
    CleanupTaskName = $taskCleanupName
    LegacyCleanupTaskName = $taskLegacyTaskName
    LegacyCleanupTaskRemoved = $false
    CleanupWatchdogRegistered = $false
    CleanupWatchdogRemoved = $false
    AuditTaskRemoved = $false
    NativeExitCode = $null
    WorkerOutcome = 'NO_WORKER_RESULT'
    PolicyEnforcementRequested = $false
    BlockingProven = $false
    Error = $null
}
$taskOwner = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
$taskAcl = New-Object Security.AccessControl.DirectorySecurity
$taskAcl.SetAccessRuleProtection($true, $false)
$taskAcl.SetOwner($taskOwner)
foreach ($taskSid in @('S-1-5-18', 'S-1-5-32-544')) {
    $taskIdentity = New-Object Security.Principal.SecurityIdentifier($taskSid)
    $taskAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($taskIdentity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
}
$null = [IO.Directory]::CreateDirectory($taskDirectory, $taskAcl)
$null = New-Item -ItemType Directory -Path $taskOutput -ErrorAction Stop
$taskWorker = Join-Path $taskDirectory 'AictAppLockerNativeAudit.exe'
Copy-Item -LiteralPath $taskSource -Destination $taskWorker -ErrorAction Stop
$taskWorkerAcl = Get-Acl -LiteralPath $taskWorker
$taskWorkerAcl.SetOwner($taskOwner)
Set-Acl -LiteralPath $taskWorker -AclObject $taskWorkerAcl
if ((Get-FileHash -LiteralPath $taskWorker -Algorithm SHA256).Hash.ToLowerInvariant() -cne $taskExpectedHash) { throw 'Staged native worker hash mismatch.' }
$taskCmd = "$env:SystemRoot\System32\cmd.exe"
foreach ($taskPath in @($taskDirectory, $taskWorker, $taskCmd)) { if ($taskPath -match '[%&!^|<>"\r\n]') { throw 'Unexpected shell metacharacter in fixed native lab path.' } }
foreach ($taskMode in @('audit', 'cleanup')) {
    $taskBatch = Join-Path $taskDirectory ('Run-' + $taskMode + '.cmd')
    $taskLines = @('@echo off', ('"' + $taskWorker + '" --' + $taskMode + ' 1>"' + (Join-Path $taskDirectory ($taskMode + '-stdout.txt')) + '" 2>"' + (Join-Path $taskDirectory ($taskMode + '-stderr.txt')) + '"'), 'set "AICT_NATIVE_EXIT=%ERRORLEVEL%"', ('>"' + (Join-Path $taskDirectory ($taskMode + '-exitcode.txt')) + '" echo %AICT_NATIVE_EXIT%'), 'exit /b %AICT_NATIVE_EXIT%')
    [IO.File]::WriteAllLines($taskBatch, [string[]]$taskLines, [Text.Encoding]::ASCII)
    $taskBatchAcl = Get-Acl -LiteralPath $taskBatch
    $taskBatchAcl.SetOwner($taskOwner)
    Set-Acl -LiteralPath $taskBatch -AclObject $taskBatchAcl
}
$taskSystem = New-ScheduledTaskPrincipal -UserId 'S-1-5-18' -LogonType ServiceAccount -RunLevel Highest
$taskSettings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 2) -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
$taskProbeAction = New-ScheduledTaskAction -Execute $taskCmd -Argument ('/d /c "' + (Join-Path $taskDirectory 'Run-audit.cmd') + '"')
$taskCleanupAction = New-ScheduledTaskAction -Execute $taskCmd -Argument ('/d /c "' + (Join-Path $taskDirectory 'Run-cleanup.cmd') + '"')
$taskProbeRegistered = $false
$taskCleanupRegistered = $false
$taskCanRemoveWatchdog = $false
Write-Host 'Native Windows MI audit experiment; no PowerShell process runs as SYSTEM.'
Write-Host 'Policy mode stays AuditOnly. Execution policies and service settings are unchanged.'
try {
    $taskTrigger = New-ScheduledTaskTrigger -Once -At ((Get-Date).AddMinutes(3))
    $null = Register-ScheduledTask -TaskName $taskCleanupName -Action $taskCleanupAction -Trigger $taskTrigger -Principal $taskSystem -Settings $taskSettings -ErrorAction Stop
    $taskCleanupRegistered = $true
    $taskStatus.CleanupWatchdogRegistered = $true
    $null = Register-ScheduledTask -TaskName $taskProbeName -Action $taskProbeAction -Principal $taskSystem -Settings $taskSettings -ErrorAction Stop
    $taskProbeRegistered = $true
    Start-ScheduledTask -TaskName $taskProbeName -ErrorAction Stop
    $taskExitFile = Join-Path $taskDirectory 'audit-exitcode.txt'
    $taskDeadline = [DateTimeOffset]::UtcNow.AddSeconds(110)
    while (-not (Test-Path -LiteralPath $taskExitFile) -and [DateTimeOffset]::UtcNow -lt $taskDeadline) { Start-Sleep -Seconds 1 }
    if (Test-Path -LiteralPath $taskExitFile) { $taskStatus.NativeExitCode = [int]((Get-Content -Raw -Encoding UTF8 -LiteralPath $taskExitFile).Trim()) }
    $taskResultPath = Join-Path $taskDirectory 'audit-result.json'
    if (-not (Test-Path -LiteralPath $taskResultPath)) {
        $taskStatus.NativeLastTaskResult = (Get-ScheduledTaskInfo -TaskName $taskProbeName).LastTaskResult
        throw ('No native result. Exit=' + $taskStatus.NativeExitCode + ', LastTaskResult=' + $taskStatus.NativeLastTaskResult + '. Startup stderr and journal will be collected; cleanup watchdog retained.')
    }
    $taskResult = Get-Content -Raw -Encoding UTF8 -LiteralPath $taskResultPath | ConvertFrom-Json
    if ($taskResult.Kind -cne 'APPLOCKER_NATIVE_MI_AUDIT_ONLY_NOT_BLOCKING' -or $taskResult.RunId -cne $taskRunId -or $taskResult.SchemaVersion -ne 3 -or $taskResult.PolicyTransportEncoding -cne 'HTML_ENTITY_ENCODED_XML') { throw 'Native result identity or transport-version mismatch.' }
    $taskStatus.WorkerOutcome = $taskResult.Outcome
    $taskCanRemoveWatchdog = (-not $taskResult.MutationAttempted) -or $taskResult.OwnInstancesAbsent
    Write-Host ('Native worker outcome: ' + $taskResult.Outcome)
    Write-Host ('Last stage: ' + $taskResult.LastStage)
    foreach ($taskField in @('Error', 'CleanupError', 'LegacyError')) { if ($taskResult.$taskField) { Write-Host ($taskField + ': ' + ($taskResult.$taskField | ConvertTo-Json -Compress)) } }
    if ($taskResult.LegacyInstancesAbsent) {
        $taskLegacyTask = Get-ScheduledTask -TaskName $taskLegacyTaskName -ErrorAction SilentlyContinue
        if ($null -eq $taskLegacyTask) { $taskStatus.LegacyCleanupTaskRemoved = $true }
        else {
            $taskLegacyScript = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) ('AICT-CspLab-' + $taskLegacyId + '\AppLockerCspWorker.ps1')
            $taskExpectedArguments = '-NoProfile -NonInteractive -File "' + $taskLegacyScript + '" -Mode Cleanup'
            if ($taskLegacyTask.Principal.UserId -in @('SYSTEM', 'S-1-5-18', 'NT AUTHORITY\SYSTEM') -and @($taskLegacyTask.Actions).Count -eq 1 -and $taskLegacyTask.Actions[0].Execute -ieq "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -and $taskLegacyTask.Actions[0].Arguments -ceq $taskExpectedArguments) {
                if ($taskLegacyTask.State -eq 'Running') { Stop-ScheduledTask -TaskName $taskLegacyTaskName -ErrorAction Stop }
                Unregister-ScheduledTask -TaskName $taskLegacyTaskName -Confirm:$false -ErrorAction Stop
                $taskStatus.LegacyCleanupTaskRemoved = $true
            } else { Write-Host 'Legacy task definition changed; task left untouched.' }
        }
    }
} catch { $taskStatus.Error = $_.Exception.Message; Write-Host ('Lab diagnostic: ' + $taskStatus.Error) }
finally {
    if ($taskProbeRegistered) {
        try {
            if ((Get-ScheduledTask -TaskName $taskProbeName).State -eq 'Running') { Stop-ScheduledTask -TaskName $taskProbeName -ErrorAction Stop }
            Unregister-ScheduledTask -TaskName $taskProbeName -Confirm:$false -ErrorAction Stop
            $taskStatus.AuditTaskRemoved = $true
        } catch { $taskStatus.AuditTaskCleanupError = $_.Exception.Message }
    }
    if ($taskCleanupRegistered -and $taskCanRemoveWatchdog) {
        try { Unregister-ScheduledTask -TaskName $taskCleanupName -Confirm:$false -ErrorAction Stop; $taskStatus.CleanupWatchdogRemoved = $true }
        catch { $taskStatus.WatchdogCleanupError = $_.Exception.Message }
    }
    foreach ($taskFile in @(Get-ChildItem -LiteralPath $taskDirectory -File | Where-Object { $_.Extension -in @('.json', '.jsonl', '.txt') })) {
        Copy-Item -LiteralPath $taskFile.FullName -Destination (Join-Path $taskOutput $taskFile.Name) -ErrorAction Stop
    }
    [IO.File]::WriteAllText((Join-Path $taskOutput 'runner-result.json'), ($taskStatus | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
    $taskHashes = @(Get-ChildItem -LiteralPath $taskOutput -File | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Name })
    [IO.File]::WriteAllLines((Join-Path $taskOutput 'SHA256SUMS.txt'), [string[]]$taskHashes, (New-Object Text.UTF8Encoding($false)))
}
$taskStderr = Join-Path $taskOutput 'audit-stderr.txt'
if (Test-Path -LiteralPath $taskStderr) { Get-Content -LiteralPath $taskStderr | Write-Host }
Write-Host ('Evidence directory: ' + $taskOutput)
if ($taskCleanupRegistered -and -not $taskStatus.CleanupWatchdogRemoved) { Write-Host ('Cleanup watchdog retained: ' + $taskCleanupName + '. Wait at least 3 minutes; preserve the protected lab directory.') }
Write-Host 'Copy the csp-native-audit-TIMESTAMP directory back to reports. Audit-only success never proves application blocking.'
