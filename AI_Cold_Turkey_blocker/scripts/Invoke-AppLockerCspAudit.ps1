#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][ValidateNotNullOrEmpty()][string]$SnapshotReference)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskMachine = Get-CimInstance Win32_ComputerSystem
if ($taskMachine.Manufacturer -notmatch 'VMware' -or $taskMachine.Model -notmatch 'VMware') { throw 'VMware guest required. No tasks or policy created.' }
$taskPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $taskPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run inside the guest from 64-bit administrator Windows PowerShell.' }
if (-not [Environment]::Is64BitProcess) { throw '64-bit Windows PowerShell required.' }
if ($PSVersionTable.PSEdition -ne 'Desktop') { throw 'Use inbox Windows PowerShell 5.1 for this laboratory package.' }
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskWorkerSource = Join-Path $taskRoot 'lab\AppLockerCspWorker.ps1'
$taskManifest = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskRoot 'MANIFEST.json') | ConvertFrom-Json
if ((Get-FileHash -LiteralPath $taskWorkerSource -Algorithm SHA256).Hash.ToLowerInvariant() -ne $taskManifest.files.'lab/AppLockerCspWorker.ps1') { throw 'Worker hash mismatch.' }
$taskRunId = [guid]::NewGuid().ToString('N')
$taskDirectory = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) ('AICT-CspLab-' + $taskRunId)
$taskOutput = Join-Path $taskRoot ('csp-audit-' + [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
$taskProbeName = 'AICT-Phase0-Probe-' + $taskRunId
$taskCleanupName = 'AICT-Phase0-Cleanup-' + $taskRunId
$taskProbeRegistered = $false
$taskCleanupRegistered = $false
$taskCanRemoveWatchdog = $false
$taskStatus = [ordered]@{
    SchemaVersion = 1
    Kind = 'APPLOCKER_AUDIT_ONLY_LAB_RUNNER'
    CollectedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    SnapshotReferenceOperatorSupplied = $SnapshotReference
    SnapshotRestoreVerified = $false
    RunId = $taskRunId
    AuditTaskName = $taskProbeName
    CleanupTaskName = $taskCleanupName
    ProtectedLabDirectory = $taskDirectory
    WorkerOutcome = 'NO_WORKER_RESULT'
    CleanupWatchdogRegistered = $false
    CleanupWatchdogRemoved = $false
    AuditTaskRemoved = $false
    PolicyEnforcementRequested = $false
    BlockingProven = $false
    Error = $null
}
$taskAcl = New-Object Security.AccessControl.DirectorySecurity
$taskAcl.SetAccessRuleProtection($true, $false)
$taskOwner = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
$taskAcl.SetOwner($taskOwner)
foreach ($taskSid in @('S-1-5-18', 'S-1-5-32-544')) {
    $taskIdentity = New-Object Security.Principal.SecurityIdentifier($taskSid)
    $taskRule = New-Object Security.AccessControl.FileSystemAccessRule($taskIdentity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
    $taskAcl.AddAccessRule($taskRule)
}
$null = [IO.Directory]::CreateDirectory($taskDirectory, $taskAcl)
$null = New-Item -ItemType Directory -Path $taskOutput -ErrorAction Stop
$taskWorker = Join-Path $taskDirectory 'AppLockerCspWorker.ps1'
Copy-Item -LiteralPath $taskWorkerSource -Destination $taskWorker -ErrorAction Stop
$taskWorkerAcl = Get-Acl -LiteralPath $taskWorker
$taskWorkerAcl.SetOwner($taskOwner)
Set-Acl -LiteralPath $taskWorker -AclObject $taskWorkerAcl
if ((Get-FileHash -LiteralPath $taskWorker -Algorithm SHA256).Hash.ToLowerInvariant() -ne $taskManifest.files.'lab/AppLockerCspWorker.ps1') { throw 'Staged worker hash mismatch.' }
$taskPowerShell = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
$taskSettings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 1) -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
$taskSystem = New-ScheduledTaskPrincipal -UserId 'S-1-5-18' -LogonType ServiceAccount -RunLevel Highest
$taskProbeAction = New-ScheduledTaskAction -Execute $taskPowerShell -Argument ('-NoProfile -NonInteractive -File "' + $taskWorker + '" -Mode Audit')
$taskCleanupAction = New-ScheduledTaskAction -Execute $taskPowerShell -Argument ('-NoProfile -NonInteractive -File "' + $taskWorker + '" -Mode Cleanup')
Write-Host 'VM-only audit experiment: no application blocking is requested.'
Write-Host 'An independent cleanup task is scheduled before the audit task starts.'
try {
    $taskTrigger = New-ScheduledTaskTrigger -Once -At ((Get-Date).AddMinutes(3))
    $null = Register-ScheduledTask -TaskName $taskCleanupName -Action $taskCleanupAction -Trigger $taskTrigger -Settings $taskSettings -Principal $taskSystem -ErrorAction Stop
    $taskCleanupRegistered = $true
    $taskStatus.CleanupWatchdogRegistered = $true
    $null = Register-ScheduledTask -TaskName $taskProbeName -Action $taskProbeAction -Settings $taskSettings -Principal $taskSystem -ErrorAction Stop
    $taskProbeRegistered = $true
    Start-ScheduledTask -TaskName $taskProbeName -ErrorAction Stop
    $taskDeadline = [DateTimeOffset]::UtcNow.AddSeconds(75)
    $taskResultPath = Join-Path $taskDirectory 'audit-result.json'
    while (-not (Test-Path -LiteralPath $taskResultPath) -and [DateTimeOffset]::UtcNow -lt $taskDeadline) { Start-Sleep -Seconds 1 }
    if (-not (Test-Path -LiteralPath $taskResultPath)) {
        $taskInfo = Get-ScheduledTaskInfo -TaskName $taskProbeName -ErrorAction Stop
        throw ('No worker result. LastTaskResult=' + $taskInfo.LastTaskResult + '. Cleanup watchdog retained; do not retry or delete the lab directory.')
    }
    $taskResult = Get-Content -Raw -Encoding UTF8 -LiteralPath $taskResultPath | ConvertFrom-Json
    if ($taskResult.RunId -cne $taskRunId -or $taskResult.Kind -cne 'APPLOCKER_CSP_AUDIT_ONLY_MANAGEMENT_NOT_BLOCKING') { throw 'Worker result identity mismatch' }
    $taskStatus.WorkerOutcome = $taskResult.Outcome
    $taskCanRemoveWatchdog = (-not $taskResult.MutationAttempted) -or $taskResult.OwnInstancesAbsent
    Write-Host ('Worker outcome: ' + $taskResult.Outcome)
    if ($taskResult.Error) { Write-Host ('Worker diagnostic: ' + ($taskResult.Error | ConvertTo-Json -Compress)) }
    if ($taskResult.CleanupError) { Write-Host ('Cleanup diagnostic: ' + ($taskResult.CleanupError | ConvertTo-Json -Compress)) }
    Write-Host ('Own policy instances absent: ' + $taskResult.OwnInstancesAbsent)
} catch {
    $taskStatus.Error = $_.Exception.Message
    Write-Host ('Lab diagnostic: ' + $taskStatus.Error)
} finally {
    if ($taskProbeRegistered) {
        try {
            $taskProbe = Get-ScheduledTask -TaskName $taskProbeName -ErrorAction Stop
            if ($taskProbe.State -eq 'Running') { Stop-ScheduledTask -TaskName $taskProbeName -ErrorAction Stop }
            Unregister-ScheduledTask -TaskName $taskProbeName -Confirm:$false -ErrorAction Stop
            $taskStatus.AuditTaskRemoved = $true
        } catch { Write-Host ('Own audit task cleanup failed: ' + $_.Exception.Message) }
    }
    if ($taskCleanupRegistered -and $taskCanRemoveWatchdog) {
        try {
            Unregister-ScheduledTask -TaskName $taskCleanupName -Confirm:$false -ErrorAction Stop
            $taskStatus.CleanupWatchdogRemoved = $true
        } catch { Write-Host ('Own watchdog cleanup failed: ' + $_.Exception.Message) }
    }
    foreach ($taskName in @('audit-result.json', 'cleanup-result.json')) {
        $taskFile = Join-Path $taskDirectory $taskName
        if (Test-Path -LiteralPath $taskFile) { Copy-Item -LiteralPath $taskFile -Destination (Join-Path $taskOutput $taskName) -ErrorAction Stop }
    }
    [IO.File]::WriteAllText((Join-Path $taskOutput 'runner-result.json'), ($taskStatus | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
    $taskHashes = @(Get-ChildItem -LiteralPath $taskOutput -File | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Name })
    [IO.File]::WriteAllLines((Join-Path $taskOutput 'SHA256SUMS.txt'), [string[]]$taskHashes, (New-Object Text.UTF8Encoding($false)))
    Write-Host ('Evidence directory: ' + $taskOutput)
    Write-Host ('Protected lab files retained: ' + $taskDirectory)
    if ($taskCleanupRegistered -and -not $taskStatus.CleanupWatchdogRemoved) {
        Write-Host ('Cleanup task retained: ' + $taskCleanupName + '. Wait at least 3 minutes and preserve the protected lab directory.')
    }
}
Write-Host 'Copy the csp-audit-TIMESTAMP directory back to the project reports directory.'
Write-Host 'Even a successful audit roundtrip does not prove application blocking.'
