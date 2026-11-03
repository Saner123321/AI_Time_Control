#Requires -Version 5.1
<#
.SYNOPSIS
Collect read-only Windows guest observations; never installs or applies policy.
.DESCRIPTION
Must run inside a VMware guest. Report files are created next to the scripts.
No credentials, account identifiers, existing policy contents or browsing data are collected.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskMachine = Get-CimInstance Win32_ComputerSystem
if ($taskMachine.Manufacturer -notmatch 'VMware' -or $taskMachine.Model -notmatch 'VMware') {
    throw 'VMware guest required. No report written. Do not run this collector on the host.'
}
$taskStamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
$taskOutputDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) ('guest-evidence-' + $taskStamp)
$null = New-Item -ItemType Directory -Path $taskOutputDirectory -ErrorAction Stop
$taskInventoryPath = Join-Path $taskOutputDirectory 'environment.json'
& (Join-Path $PSScriptRoot 'Inspect-Environment.ps1') -OutputPath $taskInventoryPath | Out-Null

$taskObservations = [ordered]@{
    SchemaVersion = 2
    CollectedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    Kind = 'READ_ONLY_GUEST_PREFLIGHT_NOT_ENFORCEMENT'
    VirtualMachineManufacturer = $taskMachine.Manufacturer
    VirtualMachineModel = $taskMachine.Model
    SnapshotVerified = $false
    AppLocker = [ordered]@{
        GetPolicyAvailable = $false
        SetPolicyAvailable = $false
        EffectivePolicyRead = 'NOT_ATTEMPTED'
        EffectivePolicySha256 = $null
        EffectivePolicyRuleCount = $null
        ErrorType = $null
        ManagementClassMetadata = @()
    }
    Wfp = [ordered]@{
        ServerNameEncoding = 'EXPLICIT_NULL_POINTER'
        Attempted = $false
        EngineOpenStatus = $null
        EngineCloseStatus = $null
        ErrorType = $null
        FiltersAddedOrRemoved = $false
        EnforcementTested = $false
    }
    FrameworkCSharpCompilerPresent = (Test-Path -LiteralPath "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe")
    PolicyChangesMade = $false
    ServicesInstalledOrChanged = $false
}

# Importing the inbox module only affects this process, not system policy.
try {
    if (Get-Module -ListAvailable AppLocker) { Import-Module AppLocker -ErrorAction Stop }
    $taskObservations.AppLocker.GetPolicyAvailable = ($null -ne (Get-Command Get-AppLockerPolicy -ErrorAction SilentlyContinue))
    $taskObservations.AppLocker.SetPolicyAvailable = ($null -ne (Get-Command Set-AppLockerPolicy -ErrorAction SilentlyContinue))
    if ($taskObservations.AppLocker.GetPolicyAvailable) {
        $taskXml = Get-AppLockerPolicy -Effective -Xml -ErrorAction Stop
        $taskHash = [Security.Cryptography.SHA256]::Create()
        try {
            $taskObservations.AppLocker.EffectivePolicySha256 = ([BitConverter]::ToString($taskHash.ComputeHash([Text.Encoding]::UTF8.GetBytes($taskXml)))).Replace('-', '').ToLowerInvariant()
        } finally { $taskHash.Dispose() }
        $taskDocument = New-Object Xml.XmlDocument
        $taskDocument.XmlResolver = $null
        $taskDocument.LoadXml($taskXml)
        $taskObservations.AppLocker.EffectivePolicyRuleCount = $taskDocument.SelectNodes('/AppLockerPolicy/RuleCollection/*').Count
        $taskObservations.AppLocker.EffectivePolicyRead = 'READ_SUCCEEDED_NOT_ENFORCEMENT'
    } else { $taskObservations.AppLocker.EffectivePolicyRead = 'MANAGEMENT_CMDLET_NOT_FOUND' }
} catch {
    $taskObservations.AppLocker.EffectivePolicyRead = 'READ_FAILED_NOT_UNSUPPORTED'
    $taskObservations.AppLocker.ErrorType = $_.Exception.GetType().Name
}

# Metadata only, not Get/Set of device policy instances. A class can exist on an
# edition where its CSP is not documented as supported: do not infer support.
foreach ($taskClassName in @(
    'MDM_AppLocker_ApplicationLaunchRestrictions01',
    'MDM_AppLocker_ApplicationLaunchRestrictions01_EXE03'
)) {
    $taskClassObservation = [ordered]@{
        ClassName = $taskClassName
        QueryStatus = 'NOT_ATTEMPTED'
        PropertyNames = @()
        ErrorType = $null
        ErrorCode = $null
        DeploymentTested = $false
        HomeEditionSupportProven = $false
    }
    try {
        $taskClass = Get-CimClass -Namespace 'root\cimv2\mdm\dmmap' -ClassName $taskClassName -ErrorAction Stop
        $taskClassObservation.PropertyNames = @($taskClass.CimClassProperties | Select-Object -ExpandProperty Name)
        $taskClassObservation.QueryStatus = 'CLASS_PRESENT_NOT_DEPLOYMENT_PROOF'
    } catch {
        $taskClassObservation.QueryStatus = 'QUERY_FAILED_NOT_UNSUPPORTED'
        $taskClassObservation.ErrorType = $_.Exception.GetType().Name
        $taskClassObservation.ErrorCode = $_.Exception.HResult
    }
    $taskObservations.AppLocker.ManagementClassMetadata += [pscustomobject]$taskClassObservation
}
. (Join-Path $PSScriptRoot 'Read-WfpSession.ps1')
$taskObservations.Wfp = Get-AictWfpSessionProbe

$taskObservationPath = Join-Path $taskOutputDirectory 'preflight.json'
$taskJson = $taskObservations | ConvertTo-Json -Depth 8
$taskBytes = (New-Object Text.UTF8Encoding($false)).GetBytes($taskJson)
$taskStream = [IO.File]::Open($taskObservationPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
try { $taskStream.Write($taskBytes, 0, $taskBytes.Length) } finally { $taskStream.Dispose() }
$taskManifestPath = Join-Path $taskOutputDirectory 'SHA256SUMS.txt'
$taskManifest = @('environment.json', 'preflight.json') | ForEach-Object {
    $taskFileHash = Get-FileHash -LiteralPath (Join-Path $taskOutputDirectory $_) -Algorithm SHA256
    $taskFileHash.Hash.ToLowerInvariant() + '  ' + $_
}
[IO.File]::WriteAllLines($taskManifestPath, [string[]]$taskManifest, (New-Object Text.UTF8Encoding($false)))
Write-Host 'Read-only guest preflight complete. No blocking rules were applied.'
Write-Host ('Evidence directory: ' + $taskOutputDirectory)
Write-Host 'Copy this evidence directory back to the project reports directory.'
