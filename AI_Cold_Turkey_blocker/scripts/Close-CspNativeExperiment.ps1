#Requires -Version 5.1
# No new tasks or policy writes. Uses frozen native absence evidence to remove
# only the exact old lab task definitions, then reads existing scoped event logs.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskMachine = Get-CimInstance Win32_ComputerSystem
if ($taskMachine.Manufacturer -notmatch 'VMware' -or $taskMachine.Model -notmatch 'VMware') { throw 'VMware guest required. No tasks or policy changed.' }
$taskPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $taskPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) -or $PSVersionTable.PSEdition -ne 'Desktop') { throw 'Use administrator Windows PowerShell 5.1 inside the guest.' }
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskManifest = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskRoot 'MANIFEST.json') | ConvertFrom-Json
$taskRunId = [string]$taskManifest.ExpectedNativeRunId
$taskLegacyId = [string]$taskManifest.ExpectedLegacyRunId
if ($taskRunId -cne 'ea3d470f6e0d4ec99ea456b339a19475' -or $taskLegacyId -cne '375860026bf2434088478e4ac27f57fe') { throw 'Unexpected experiment binding.' }
$taskLab = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) ('AICT-CspNative-' + $taskRunId)
$taskProofPath = Join-Path $taskLab 'audit-result.json'
$taskExe = Join-Path $taskLab 'AictAppLockerNativeAudit.exe'
$taskOutput = Join-Path $taskRoot ('csp-closeout-' + [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
$null = New-Item -ItemType Directory -Path $taskOutput -ErrorAction Stop
$taskReport = [ordered]@{
    SchemaVersion = 1
    Kind = 'CSP_TASK_CLOSEOUT_AND_EXISTING_LOGS_NO_POLICY_WRITES'
    CollectedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    NativeRunId = $taskRunId
    LegacyRunId = $taskLegacyId
    AbsenceEvidenceVerified = $false
    Tasks = @()
    PolicyWritesRequested = $false
    NewTasksCreated = $false
    Events = @()
    EventReadErrors = @()
    EventScanLimit = 256
    EventScanCount = 0
    EventWindowStartUtc = $null
    EventWindowEndUtc = $null
    Outcome = 'NOT_RUN'
    Error = $null
}
function Assert-AictProtected {
    param([string]$Path)
    if ((Get-Item -LiteralPath $Path).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unexpected reparse point.' }
    $taskAcl = Get-Acl -LiteralPath $Path
    if ($taskAcl.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne 'S-1-5-32-544' -and $taskAcl.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne 'S-1-5-18') { throw 'Unexpected lab owner.' }
    foreach ($taskRule in $taskAcl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($taskRule.AccessControlType -eq 'Allow' -and $taskRule.IdentityReference.Value -notin @('S-1-5-18', 'S-1-5-32-544')) { throw 'Unexpected lab access rule.' }
    }
}
function Close-AictExactTask {
    param([string]$Name, [string]$Execute, [string]$Arguments, [bool]$MayStop)
    $taskTask = Get-ScheduledTask -TaskName $Name -ErrorAction SilentlyContinue
    if ($null -eq $taskTask) { return [ordered]@{ Name = $Name; Outcome = 'ALREADY_ABSENT' } }
    if ($taskTask.Principal.UserId -notin @('SYSTEM', 'S-1-5-18', 'NT AUTHORITY\SYSTEM') -or @($taskTask.Actions).Count -ne 1 -or $taskTask.Actions[0].Execute -ine $Execute -or $taskTask.Actions[0].Arguments -cne $Arguments) { return [ordered]@{ Name = $Name; Outcome = 'DEFINITION_CHANGED_LEFT_UNTOUCHED' } }
    if ($taskTask.State -eq 'Running') {
        if (-not $MayStop) { return [ordered]@{ Name = $Name; Outcome = 'RUNNING_AUDIT_LEFT_UNTOUCHED' } }
        Stop-ScheduledTask -TaskName $Name -ErrorAction Stop
    }
    Unregister-ScheduledTask -TaskName $Name -Confirm:$false -ErrorAction Stop
    $taskRemaining = Get-ScheduledTask -TaskName $Name -ErrorAction SilentlyContinue
    if ($null -ne $taskRemaining) { throw ('Own task still present: ' + $Name) }
    return [ordered]@{ Name = $Name; Outcome = 'REMOVED_AND_ABSENCE_VERIFIED' }
}
try {
    Assert-AictProtected -Path $taskLab
    Assert-AictProtected -Path $taskProofPath
    Assert-AictProtected -Path $taskExe
    if ((Get-FileHash -LiteralPath $taskProofPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $taskManifest.ExpectedAuditResultSha256) { throw 'Absence evidence changed; refusing task removal.' }
    if ((Get-FileHash -LiteralPath $taskExe -Algorithm SHA256).Hash.ToLowerInvariant() -cne $taskManifest.ExpectedNativeWorkerSha256) { throw 'Native worker changed; refusing task removal.' }
    $taskProof = Get-Content -Raw -Encoding UTF8 -LiteralPath $taskProofPath | ConvertFrom-Json
    if ($taskProof.RunId -cne $taskRunId -or $taskProof.LegacyRunId -cne $taskLegacyId -or -not $taskProof.LocalSystem -or -not $taskProof.OwnInstancesAbsent -or -not $taskProof.LegacyInstancesAbsent -or $taskProof.AuditCreateReturned -or $taskProof.EnforcementEnabled) { throw 'Expected native failure and group-absence evidence not confirmed.' }
    $taskReport.AbsenceEvidenceVerified = $true
    Copy-Item -LiteralPath $taskProofPath -Destination (Join-Path $taskOutput 'native-audit-result.json') -ErrorAction Stop
    # A restarted Audit task would make the old proof stale. Refuse all task
    # removal if either original audit task is currently running.
    foreach ($taskAuditName in @(('AICT-Native-Probe-' + $taskRunId), ('AICT-Phase0-Probe-' + $taskLegacyId))) {
        $taskAudit = Get-ScheduledTask -TaskName $taskAuditName -ErrorAction SilentlyContinue
        if ($null -ne $taskAudit -and $taskAudit.State -eq 'Running') { throw 'An old audit task is running. Closeout stopped without cancelling it.' }
    }
    $taskCmd = "$env:SystemRoot\System32\cmd.exe"
    foreach ($taskMode in @('audit', 'cleanup')) {
        $taskBatch = Join-Path $taskLab ('Run-' + $taskMode + '.cmd')
        Assert-AictProtected -Path $taskBatch
        $taskLines = @('@echo off', ('"' + $taskExe + '" --' + $taskMode + ' 1>"' + (Join-Path $taskLab ($taskMode + '-stdout.txt')) + '" 2>"' + (Join-Path $taskLab ($taskMode + '-stderr.txt')) + '"'), 'set "AICT_NATIVE_EXIT=%ERRORLEVEL%"', ('>"' + (Join-Path $taskLab ($taskMode + '-exitcode.txt')) + '" echo %AICT_NATIVE_EXIT%'), 'exit /b %AICT_NATIVE_EXIT%')
        $taskExpectedBatch = ($taskLines -join [Environment]::NewLine) + [Environment]::NewLine
        if ((Get-Content -Raw -Encoding UTF8 -LiteralPath $taskBatch) -cne $taskExpectedBatch) { throw 'Native task command file changed; refusing task removal.' }
        $taskTaskName = $(if ($taskMode -eq 'audit') { 'AICT-Native-Probe-' + $taskRunId } else { 'AICT-Native-Cleanup-' + $taskRunId })
        $taskReport.Tasks += Close-AictExactTask -Name $taskTaskName -Execute $taskCmd -Arguments ('/d /c "' + $taskBatch + '"') -MayStop ($taskMode -eq 'cleanup')
    }
    $taskLegacyScript = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) ('AICT-CspLab-' + $taskLegacyId + '\AppLockerCspWorker.ps1')
    Assert-AictProtected -Path $taskLegacyScript
    if ((Get-FileHash -LiteralPath $taskLegacyScript -Algorithm SHA256).Hash.ToLowerInvariant() -cne $taskManifest.ExpectedLegacyWorkerSha256) { throw 'Legacy script changed; remaining legacy tasks left untouched.' }
    foreach ($taskMode in @('Audit', 'Cleanup')) {
        $taskTaskName = $(if ($taskMode -eq 'Audit') { 'AICT-Phase0-Probe-' + $taskLegacyId } else { 'AICT-Phase0-Cleanup-' + $taskLegacyId })
        $taskReport.Tasks += Close-AictExactTask -Name $taskTaskName -Execute "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -Arguments ('-NoProfile -NonInteractive -File "' + $taskLegacyScript + '" -Mode ' + $taskMode) -MayStop ($taskMode -eq 'Cleanup')
    }
    foreach ($taskFile in @(Get-ChildItem -LiteralPath $taskLab -File -Filter 'cleanup-result*.json')) { Copy-Item -LiteralPath $taskFile.FullName -Destination (Join-Path $taskOutput $taskFile.Name) -ErrorAction Stop }
    $taskAt = [DateTimeOffset]::Parse([string]$taskProof.CollectedAtUtc)
    $taskReport.EventWindowStartUtc = $taskAt.UtcDateTime.AddMinutes(-2).ToString('o')
    $taskReport.EventWindowEndUtc = $taskAt.UtcDateTime.AddMinutes(3).ToString('o')
    $taskLog = 'Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider/Admin'
    try {
        $taskEvents = @(Get-WinEvent -FilterHashtable @{ LogName = $taskLog; StartTime = $taskAt.UtcDateTime.AddMinutes(-2); EndTime = $taskAt.UtcDateTime.AddMinutes(3) } -MaxEvents 256 -ErrorAction Stop)
        $taskReport.EventScanCount = $taskEvents.Count
        foreach ($taskEvent in $taskEvents) {
            $taskXmlText = $taskEvent.ToXml()
            if ($taskXmlText.IndexOf($taskRunId, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                # Only data tied to this exact random lab group is retained.
                [xml]$taskXml = $taskXmlText
                $taskEventData = @($taskXml.SelectNodes('//*[local-name()="EventData"]/*') | ForEach-Object { [ordered]@{ Name = $_.GetAttribute('Name'); Value = $_.InnerText } })
                $taskReport.Events += [ordered]@{ Id = $taskEvent.Id; AtUtc = $taskEvent.TimeCreated.ToUniversalTime().ToString('o'); RecordId = $taskEvent.RecordId; Data = $taskEventData }
            }
        }
    } catch { $taskReport.EventReadErrors += $_.Exception.Message }
    $taskUnexpected = @($taskReport.Tasks | Where-Object { $_.Outcome -notin @('ALREADY_ABSENT', 'REMOVED_AND_ABSENCE_VERIFIED') })
    $taskReport.Outcome = $(if ($taskUnexpected.Count -eq 0 -and $taskReport.Tasks.Count -eq 4) { 'OWN_TASK_CLOSEOUT_COMPLETE' } else { 'PARTIAL_TASK_CLOSEOUT' })
} catch { $taskReport.Outcome = 'CLOSEOUT_BLOCKED'; $taskReport.Error = $_.Exception.Message }
finally {
    [IO.File]::WriteAllText((Join-Path $taskOutput 'closeout-result.json'), ($taskReport | ConvertTo-Json -Depth 12), (New-Object Text.UTF8Encoding($false)))
    $taskHashes = @(Get-ChildItem -LiteralPath $taskOutput -File | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Name })
    [IO.File]::WriteAllLines((Join-Path $taskOutput 'SHA256SUMS.txt'), [string[]]$taskHashes, (New-Object Text.UTF8Encoding($false)))
}
Write-Host ('Closeout outcome: ' + $taskReport.Outcome)
if ($taskReport.Error) { Write-Host ('Diagnostic: ' + $taskReport.Error) }
Write-Host ('Matching existing CSP events: ' + $taskReport.Events.Count)
Write-Host ('Evidence directory: ' + $taskOutput)
Write-Host 'No new tasks, CSP writes, execution-policy changes or VM file changes were requested.'
