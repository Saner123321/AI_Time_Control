#Requires -Version 5.1
[CmdletBinding()]
param([ValidateSet('Audit', 'Cleanup', 'Validate')][string]$Mode = 'Audit')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function New-AictAuditPolicy {
    param([string]$Target, [string]$AllowId, [string]$DenyId)
    $taskEscaped = [Security.SecurityElement]::Escape($Target)
    return ('<RuleCollection Type="Exe" EnforcementMode="AuditOnly"><FilePathRule Id="' + $AllowId + '" Name="AICT lab allow all" Description="Temporary audit experiment" UserOrGroupSid="S-1-1-0" Action="Allow"><Conditions><FilePathCondition Path="*" /></Conditions></FilePathRule><FilePathRule Id="' + $DenyId + '" Name="AICT lab never-executed sample" Description="Audit only, not enforced" UserOrGroupSid="S-1-1-0" Action="Deny"><Conditions><FilePathCondition Path="' + $taskEscaped + '" /></Conditions></FilePathRule></RuleCollection>')
}
if ($Mode -eq 'Validate') {
    $taskFixtureTarget = 'C:\ProgramData\AICT-CspLab-test\NeverExecuted-AictProbe.exe'
    [xml]$taskFixture = New-AictAuditPolicy -Target $taskFixtureTarget -AllowId ([guid]::NewGuid().ToString()) -DenyId ([guid]::NewGuid().ToString())
    if ($taskFixture.RuleCollection.EnforcementMode -ne 'AuditOnly' -or $taskFixture.RuleCollection.Type -ne 'Exe') { throw 'Policy mode regression' }
    if ($taskFixture.SelectNodes('/RuleCollection/FilePathRule').Count -ne 2) { throw 'Unexpected rule count' }
    if ($taskFixture.SelectSingleNode('/RuleCollection/FilePathRule[@Action="Allow"]/Conditions/FilePathCondition').Path -ne '*') { throw 'Missing allow-all audit baseline' }
    if ($taskFixture.SelectSingleNode('/RuleCollection/FilePathRule[@Action="Deny"]/Conditions/FilePathCondition').Path -ne $taskFixtureTarget) { throw 'Target scope regression' }
    Write-Output '{"kind":"OFFLINE_AUDIT_XML_VALIDATION_NOT_DEPLOYMENT","passed":true,"policy_changes_made":false}'
    return
}
$taskComputer = Get-CimInstance Win32_ComputerSystem
if ($taskComputer.Manufacturer -notmatch 'VMware' -or $taskComputer.Model -notmatch 'VMware') { throw 'VMware guest required. No policy access attempted.' }
if ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne 'S-1-5-18') { throw 'This fixed laboratory worker requires LocalSystem; use the supplied guest runner.' }
$taskFolderName = Split-Path -Leaf $PSScriptRoot
if ($taskFolderName -notmatch '^AICT-CspLab-([a-f0-9]{32})$') { throw 'Unexpected protected lab directory' }
$taskRunId = $Matches[1]
$taskExpectedDirectory = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) $taskFolderName
if (-not [string]::Equals([IO.Path]::GetFullPath($PSScriptRoot), [IO.Path]::GetFullPath($taskExpectedDirectory), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected lab location' }
if ((Get-Item -LiteralPath $PSScriptRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse lab directory refused' }
$taskNamespace = 'root\cimv2\mdm\dmmap'
$taskClass = 'MDM_AppLocker_ApplicationLaunchRestrictions01_EXE03'
$taskGroup = 'AICT-Phase0-' + $taskRunId
$taskParent = './Vendor/MSFT/AppLocker/ApplicationLaunchRestrictions/' + $taskGroup
$taskFilter = "ParentID='$taskParent' and InstanceID='EXE'"
function Remove-AictOwnAudit {
    $taskOwned = @(Get-CimInstance -Namespace $taskNamespace -ClassName $taskClass -Filter $taskFilter -ErrorAction Stop)
    foreach ($taskInstance in $taskOwned) {
        if ($taskInstance.ParentID -cne $taskParent -or $taskInstance.InstanceID -cne 'EXE') { throw 'Ownership mismatch; refusing delete' }
        Remove-CimInstance -CimInstance $taskInstance -ErrorAction Stop
    }
    $taskRemaining = @(Get-CimInstance -Namespace $taskNamespace -ClassName $taskClass -Filter $taskFilter -ErrorAction Stop)
    return ($taskRemaining.Count -eq 0)
}
$taskReport = [ordered]@{
    SchemaVersion = 1
    Kind = 'APPLOCKER_CSP_AUDIT_ONLY_MANAGEMENT_NOT_BLOCKING'
    CollectedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    Mode = $Mode
    RunId = $taskRunId
    LocalSystem = $true
    BaselineInstanceCount = $null
    RegistryKeyPresentBefore = (Test-Path -LiteralPath 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\SrpV2')
    MutationAttempted = $false
    AuditCreateReturned = $false
    AuditReadbackVerified = $false
    OwnInstancesAbsent = $false
    EnforcementEnabled = $false
    BlockingProven = $false
    Phase0PassAwarded = $false
    Outcome = 'NOT_RUN'
    Error = $null
    CleanupError = $null
}
function Get-AictError {
    param($Record)
    $taskError = [ordered]@{ Type = $Record.Exception.GetType().Name; HResult = $Record.Exception.HResult; Message = $Record.Exception.Message }
    if ($Record.Exception.PSObject.Properties['StatusCode']) { $taskError.StatusCode = [string]$Record.Exception.StatusCode }
    if ($Record.Exception.PSObject.Properties['NativeErrorCode']) { $taskError.NativeErrorCode = [string]$Record.Exception.NativeErrorCode }
    return $taskError
}
try {
    if ($Mode -eq 'Cleanup') {
        $taskReport.MutationAttempted = $true
        $taskReport.OwnInstancesAbsent = Remove-AictOwnAudit
        $taskReport.Outcome = 'CLEANUP_ONLY'
    } else {
        if ((Get-Service -Name AppIDSvc).Status -ne 'Running') { throw 'AppIDSvc is not running; service configuration left unchanged.' }
        $null = Get-CimClass -Namespace $taskNamespace -ClassName $taskClass -ErrorAction Stop
        $taskBaseline = @(Get-CimInstance -Namespace $taskNamespace -ClassName $taskClass -ErrorAction Stop)
        $taskReport.BaselineInstanceCount = $taskBaseline.Count
        if ($taskBaseline.Count -ne 0 -or $taskReport.RegistryKeyPresentBefore) { throw 'Existing policy state detected. Lab refuses to merge or overwrite it.' }
        $taskAllowId = [guid]::NewGuid().ToString()
        $taskDenyId = [guid]::NewGuid().ToString()
        $taskTarget = Join-Path $PSScriptRoot 'NeverExecuted-AictProbe.exe'
        $taskPolicy = New-AictAuditPolicy -Target $taskTarget -AllowId $taskAllowId -DenyId $taskDenyId
        $taskReport.MutationAttempted = $true
        $null = New-CimInstance -Namespace $taskNamespace -ClassName $taskClass -Property @{ ParentID = $taskParent; InstanceID = 'EXE'; Policy = $taskPolicy } -ErrorAction Stop
        $taskReport.AuditCreateReturned = $true
        $taskReadback = @(Get-CimInstance -Namespace $taskNamespace -ClassName $taskClass -Filter $taskFilter -ErrorAction Stop)
        if ($taskReadback.Count -ne 1) { throw 'Audit policy readback count mismatch' }
        $taskDocument = New-Object Xml.XmlDocument
        $taskDocument.XmlResolver = $null
        $taskDocument.LoadXml([string]$taskReadback[0].Policy)
        $taskCollection = $taskDocument.DocumentElement
        $taskDeny = $taskDocument.SelectSingleNode('/RuleCollection/FilePathRule[@Action="Deny"]/Conditions/FilePathCondition')
        $taskAllow = $taskDocument.SelectSingleNode('/RuleCollection/FilePathRule[@Action="Allow"]/Conditions/FilePathCondition')
        if ($taskCollection.Name -ne 'RuleCollection' -or $taskCollection.GetAttribute('EnforcementMode') -ne 'AuditOnly' -or $taskCollection.GetAttribute('Type') -ne 'Exe' -or $null -eq $taskDeny -or $taskDeny.GetAttribute('Path') -cne $taskTarget -or $null -eq $taskAllow -or $taskAllow.GetAttribute('Path') -cne '*' -or $taskDocument.SelectNodes('/RuleCollection/FilePathRule').Count -ne 2) { throw 'Audit policy readback mismatch' }
        $taskReport.AuditReadbackVerified = $true
        $taskReport.Outcome = 'AUDIT_READBACK_COMPLETE'
    }
} catch {
    $taskReport.Error = Get-AictError $_
    $taskReport.Outcome = 'ENTRY_OR_ROUNDTRIP_BLOCKED'
} finally {
    if ($Mode -eq 'Audit' -and $taskReport.MutationAttempted) {
        try { $taskReport.OwnInstancesAbsent = Remove-AictOwnAudit } catch { $taskReport.CleanupError = Get-AictError $_ }
    }
    if ($Mode -eq 'Audit' -and $taskReport.AuditReadbackVerified -and $taskReport.OwnInstancesAbsent) { $taskReport.Outcome = 'AUDIT_ROUNDTRIP_PASS_NOT_BLOCKING' }
    $taskReport.RegistryKeyPresentAfter = (Test-Path -LiteralPath 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\SrpV2')
    $taskFileName = $(if ($Mode -eq 'Audit') { 'audit-result.json' } else { 'cleanup-result.json' })
    $taskJson = $taskReport | ConvertTo-Json -Depth 8
    $taskTemporary = Join-Path $PSScriptRoot ($taskFileName + '.' + [guid]::NewGuid().ToString('N') + '.tmp')
    [IO.File]::WriteAllText($taskTemporary, $taskJson, (New-Object Text.UTF8Encoding($false)))
    [IO.File]::Move($taskTemporary, (Join-Path $PSScriptRoot $taskFileName))
}
