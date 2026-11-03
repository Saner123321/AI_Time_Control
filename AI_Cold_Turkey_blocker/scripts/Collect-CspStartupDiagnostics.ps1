#Requires -Version 5.1
# Diagnostic recovery for one existing lab run. Never invokes Audit mode.
[CmdletBinding()]
param([ValidatePattern('^[a-f0-9]{32}$')][string]$RunId = '375860026bf2434088478e4ac27f57fe')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskMachine = Get-CimInstance Win32_ComputerSystem
if ($taskMachine.Manufacturer -notmatch 'VMware' -or $taskMachine.Model -notmatch 'VMware') { throw 'VMware guest required. No task created.' }
$taskPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $taskPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) -or -not [Environment]::Is64BitProcess -or $PSVersionTable.PSEdition -ne 'Desktop') { throw 'Use administrator 64-bit Windows PowerShell 5.1 inside the guest.' }
$taskPackageRoot = Split-Path -Parent $PSScriptRoot
$taskManifest = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskPackageRoot 'MANIFEST.json') | ConvertFrom-Json
if ($taskManifest.ExpectedRunId -cne $RunId) { throw 'This diagnostic package is bound to a different laboratory run.' }
$taskOriginal = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) ('AICT-CspLab-' + $RunId)
$taskWorker = Join-Path $taskOriginal 'AppLockerCspWorker.ps1'
$taskOutput = Join-Path $taskPackageRoot ('csp-startup-diagnostic-' + [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
$null = New-Item -ItemType Directory -Path $taskOutput -ErrorAction Stop
$taskRecord = [ordered]@{
    SchemaVersion = 1
    Kind = 'CSP_STARTUP_DIAGNOSTIC_NO_AUDIT_OR_CLEANUP_EXECUTION'
    CollectedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    OriginalRunId = $RunId
    OriginalWorkerHashMatches = $false
    OriginalWorkerProtected = $false
    CallerExecutionPolicies = @(Get-ExecutionPolicy -List | ForEach-Object { [pscustomobject]@{ Scope = [string]$_.Scope; ExecutionPolicy = [string]$_.ExecutionPolicy } })
    OriginalTaskObservations = @()
    CaptureTaskCreated = $false
    CaptureTaskRemoved = $false
    ValidationExitCode = $null
    PolicyReadOrWriteRequested = $false
    ExistingCleanupStatusConfirmed = $false
    Outcome = 'NOT_RUN'
    Error = $null
}
function Get-AictOriginalTask {
    param([string]$Name)
    $taskValue = Get-ScheduledTask -TaskName $Name -ErrorAction SilentlyContinue
    if ($null -eq $taskValue) { return [ordered]@{ Name = $Name; Present = $false } }
    $taskInfo = Get-ScheduledTaskInfo -TaskName $Name -ErrorAction Stop
    return [ordered]@{ Name = $Name; Present = $true; State = [string]$taskValue.State; LastTaskResult = $taskInfo.LastTaskResult; LastRunTime = $taskInfo.LastRunTime.ToString('o'); IsSystem = ($taskValue.Principal.UserId -in @('SYSTEM', 'S-1-5-18')); ActionCount = @($taskValue.Actions).Count }
}
function Test-AictProtectedFile {
    param([string]$Path, [bool]$Directory)
    $taskItem = Get-Item -LiteralPath $Path -ErrorAction Stop
    if ($taskItem.Attributes -band [IO.FileAttributes]::ReparsePoint) { return $false }
    $taskSecurity = Get-Acl -LiteralPath $Path
    if ($taskSecurity.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne 'S-1-5-32-544') { return $false }
    if ($Directory -and -not $taskSecurity.AreAccessRulesProtected) { return $false }
    foreach ($taskRule in $taskSecurity.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($taskRule.AccessControlType -eq 'Allow' -and $taskRule.IdentityReference.Value -notin @('S-1-5-18', 'S-1-5-32-544')) { return $false }
    }
    return $true
}
$taskCaptureName = $null
$taskCaptureRegistered = $false
$taskDiagDirectory = $null
try {
    foreach ($taskName in @(('AICT-Phase0-Probe-' + $RunId), ('AICT-Phase0-Cleanup-' + $RunId))) { $taskRecord.OriginalTaskObservations += Get-AictOriginalTask -Name $taskName }
    if (-not (Test-Path -LiteralPath $taskWorker)) { throw 'Original worker is missing. Do not recreate or rerun audit.' }
    $taskRecord.OriginalWorkerHashMatches = ((Get-FileHash -LiteralPath $taskWorker -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $taskManifest.ExpectedOriginalWorkerSha256)
    if (-not $taskRecord.OriginalWorkerHashMatches) { throw 'Original worker differs from the frozen v1 build. Refusing execution.' }
    $taskRecord.OriginalWorkerProtected = (Test-AictProtectedFile -Path $taskOriginal -Directory $true) -and (Test-AictProtectedFile -Path $taskWorker -Directory $false)
    if (-not $taskRecord.OriginalWorkerProtected) { throw 'Original worker ownership or ACL is unexpected. Refusing execution; no ACL changes made.' }
    foreach ($taskName in @('audit-result.json', 'cleanup-result.json')) {
        $taskFile = Join-Path $taskOriginal $taskName
        if (Test-Path -LiteralPath $taskFile) {
            Copy-Item -LiteralPath $taskFile -Destination (Join-Path $taskOutput $taskName) -ErrorAction Stop
            $taskPrevious = Get-Content -Raw -Encoding UTF8 -LiteralPath $taskFile | ConvertFrom-Json
            if ($taskPrevious.PSObject.Properties['OwnInstancesAbsent'] -and $taskPrevious.OwnInstancesAbsent -eq $true) { $taskRecord.ExistingCleanupStatusConfirmed = $true }
        }
    }
    $taskDiagId = [guid]::NewGuid().ToString('N')
    $taskCaptureName = 'AICT-Phase0-Diagnostic-' + $taskDiagId
    $taskDiagDirectory = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) ('AICT-CspDiag-' + $taskDiagId)
    $taskAcl = New-Object Security.AccessControl.DirectorySecurity
    $taskAcl.SetAccessRuleProtection($true, $false)
    $taskOwner = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $taskAcl.SetOwner($taskOwner)
    foreach ($taskSid in @('S-1-5-18', 'S-1-5-32-544')) {
        $taskIdentity = New-Object Security.Principal.SecurityIdentifier($taskSid)
        $taskAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($taskIdentity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    }
    $null = [IO.Directory]::CreateDirectory($taskDiagDirectory, $taskAcl)
    $taskPowerShell = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
    $taskCmd = "$env:SystemRoot\System32\cmd.exe"
    foreach ($taskPath in @($taskWorker, $taskDiagDirectory, $taskPowerShell, $taskCmd)) { if ($taskPath -match '[%&!^|<>"\r\n]') { throw 'Unexpected shell metacharacter in fixed diagnostic path.' } }
    # -File keeps normal script-policy checks. Only the old worker's offline
    # Validate branch runs. A separate fixed command reads policy settings;
    # it never loads or executes the old worker through -Command.
    $taskBatchLines = @(
        '@echo off',
        ('"' + $taskPowerShell + '" -NoLogo -NoProfile -NonInteractive -File "' + $taskWorker + '" -Mode Validate 1>"' + (Join-Path $taskDiagDirectory 'validation-stdout.txt') + '" 2>"' + (Join-Path $taskDiagDirectory 'validation-stderr.txt') + '"'),
        'set "AICT_DIAG_EXIT=%ERRORLEVEL%"',
        ('"' + $taskPowerShell + '" -NoLogo -NoProfile -NonInteractive -Command "Get-ExecutionPolicy -List | ForEach-Object { [pscustomobject]@{Scope=[string]$_.Scope;ExecutionPolicy=[string]$_.ExecutionPolicy} } | ConvertTo-Json -Compress" 1>"' + (Join-Path $taskDiagDirectory 'system-execution-policy.json') + '" 2>"' + (Join-Path $taskDiagDirectory 'system-policy-stderr.txt') + '"'),
        ('>"' + (Join-Path $taskDiagDirectory 'validation-exitcode.txt') + '" echo %AICT_DIAG_EXIT%'),
        'exit /b %AICT_DIAG_EXIT%'
    )
    $taskBatchPath = Join-Path $taskDiagDirectory 'Run-Validate.cmd'
    [IO.File]::WriteAllLines($taskBatchPath, [string[]]$taskBatchLines, [Text.Encoding]::ASCII)
    $taskBatchAcl = Get-Acl -LiteralPath $taskBatchPath
    $taskBatchAcl.SetOwner($taskOwner)
    Set-Acl -LiteralPath $taskBatchPath -AclObject $taskBatchAcl
    $taskAction = New-ScheduledTaskAction -Execute $taskCmd -Argument ('/d /c "' + $taskBatchPath + '"')
    $taskSystem = New-ScheduledTaskPrincipal -UserId 'S-1-5-18' -LogonType ServiceAccount -RunLevel Highest
    $taskSettings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 1) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    $null = Register-ScheduledTask -TaskName $taskCaptureName -Action $taskAction -Principal $taskSystem -Settings $taskSettings -ErrorAction Stop
    $taskCaptureRegistered = $true
    $taskRecord.CaptureTaskCreated = $true
    Start-ScheduledTask -TaskName $taskCaptureName -ErrorAction Stop
    $taskDeadline = [DateTimeOffset]::UtcNow.AddSeconds(70)
    $taskExitFile = Join-Path $taskDiagDirectory 'validation-exitcode.txt'
    while (-not (Test-Path -LiteralPath $taskExitFile) -and [DateTimeOffset]::UtcNow -lt $taskDeadline) { Start-Sleep -Seconds 1 }
    if (Test-Path -LiteralPath $taskExitFile) {
        $taskRecord.ValidationExitCode = [int]((Get-Content -Raw -Encoding UTF8 -LiteralPath $taskExitFile).Trim())
        $taskRecord.Outcome = 'STARTUP_LOGS_CAPTURED_NO_POLICY_EXECUTION'
    } else {
        $taskInfo = Get-ScheduledTaskInfo -TaskName $taskCaptureName
        $taskRecord.Outcome = 'NO_CAPTURE_EXIT_FILE'
        $taskRecord.CaptureLastTaskResult = $taskInfo.LastTaskResult
    }
} catch {
    $taskRecord.Outcome = 'DIAGNOSTIC_BLOCKED'
    $taskRecord.Error = $_.Exception.Message
} finally {
    if ($taskCaptureRegistered) {
        try {
            $taskCapture = Get-ScheduledTask -TaskName $taskCaptureName -ErrorAction Stop
            if ($taskCapture.State -eq 'Running') { Stop-ScheduledTask -TaskName $taskCaptureName -ErrorAction Stop }
            Unregister-ScheduledTask -TaskName $taskCaptureName -Confirm:$false -ErrorAction Stop
            $taskRecord.CaptureTaskRemoved = $true
        } catch { $taskRecord.CaptureCleanupError = $_.Exception.Message }
    }
    if ($taskDiagDirectory) {
        foreach ($taskName in @('validation-stdout.txt', 'validation-stderr.txt', 'validation-exitcode.txt', 'system-execution-policy.json', 'system-policy-stderr.txt')) {
            $taskFile = Join-Path $taskDiagDirectory $taskName
            if (Test-Path -LiteralPath $taskFile) { Copy-Item -LiteralPath $taskFile -Destination (Join-Path $taskOutput $taskName) -ErrorAction Stop }
        }
    }
    [IO.File]::WriteAllText((Join-Path $taskOutput 'diagnostic-result.json'), ($taskRecord | ConvertTo-Json -Depth 9), (New-Object Text.UTF8Encoding($false)))
    $taskHashes = @(Get-ChildItem -LiteralPath $taskOutput -File | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Name })
    [IO.File]::WriteAllLines((Join-Path $taskOutput 'SHA256SUMS.txt'), [string[]]$taskHashes, (New-Object Text.UTF8Encoding($false)))
}
Write-Host ('Diagnostic outcome: ' + $taskRecord.Outcome)
if ($taskRecord.Error) { Write-Host ('Diagnostic error: ' + $taskRecord.Error) }
$taskErrorLog = Join-Path $taskOutput 'validation-stderr.txt'
if (Test-Path -LiteralPath $taskErrorLog) { Get-Content -LiteralPath $taskErrorLog | Write-Host }
Write-Host ('Evidence directory: ' + $taskOutput)
Write-Host 'Original cleanup task and original lab directory were not changed. Do not rerun Audit mode yet.'
