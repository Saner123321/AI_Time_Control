#Requires -Version 5.1
<#
.SYNOPSIS
Read-only phase-0 inventory. No administrative rights required.
.DESCRIPTION
Does not install, start services, change policy, or create WFP objects.
OutputPath is optional and must not already exist. No network requests.
#>
[CmdletBinding()]
param([string]$OutputPath)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not [Environment]::Is64BitProcess) { throw 'Run 64-bit Windows PowerShell.' }
if ($OutputPath -and (Test-Path -LiteralPath $OutputPath)) { throw 'Output exists; choose a new file.' }
$taskErrors = New-Object 'System.Collections.Generic.List[object]'
function Read-Observation {
    param([string]$Name, [scriptblock]$Read)
    try { & $Read } catch {
        $taskErrors.Add([ordered]@{ Probe = $Name; ErrorType = $_.Exception.GetType().Name; ErrorCode = $_.Exception.HResult })
        # Do not include arbitrary exception text: it may contain personal paths.
        return $null
    }
}

$taskOs = Read-Observation 'OperatingSystem' { Get-CimInstance Win32_OperatingSystem }
$taskComputer = Read-Observation 'Computer' { Get-CimInstance Win32_ComputerSystem }
$taskVersion = Read-Observation 'VersionRegistry' { Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' }
$taskIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$taskPrincipal = New-Object Security.Principal.WindowsPrincipal($taskIdentity)
$taskServices = @(Read-Observation 'Services' {
    Get-CimInstance Win32_Service -Filter "Name='AppIDSvc' OR Name='BFE' OR Name='vmcompute' OR Name='vmms' OR Name='com.docker.service'" |
        Select-Object Name, State, StartMode
})
$taskTools = @()
foreach ($taskTool in @('dotnet.exe', 'VBoxManage.exe', 'vmrun.exe', 'qemu-system-x86_64.exe', 'Get-VM')) {
    $taskCommand = Get-Command $taskTool -ErrorAction SilentlyContinue
    $taskTools += [ordered]@{ Name = $taskTool; FoundOnPath = ($null -ne $taskCommand) }
}
$taskSdks = @()
$taskRuntimes = @()
if (Get-Command dotnet.exe -ErrorAction SilentlyContinue) {
    $taskSdks = @(Read-Observation 'DotNetSdks' { & dotnet.exe --list-sdks | ForEach-Object { ($_ -split ' \[')[0] } })
    $taskRuntimes = @(Read-Observation 'DotNetRuntimes' { & dotnet.exe --list-runtimes | ForEach-Object { ($_ -split ' \[')[0] } })
}
$taskBrowsers = @()
foreach ($taskBrowser in @(
    @{ Name = 'Edge'; Path = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe" },
    @{ Name = 'Chrome'; Path = "$env:ProgramFiles\Google\Chrome\Application\chrome.exe" },
    @{ Name = 'Chrome-x86'; Path = "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe" },
    @{ Name = 'Chrome-user'; Path = "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe" }
)) {
    $taskExists = Test-Path -LiteralPath $taskBrowser.Path
    $taskBrowserVersion = $null
    if ($taskExists) { $taskBrowserVersion = (Get-Item -LiteralPath $taskBrowser.Path).VersionInfo.FileVersion }
    $taskBrowsers += [ordered]@{ Name = $taskBrowser.Name; Exists = $taskExists; Version = $taskBrowserVersion }
}
$taskModuleRoot = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\Modules"
$taskModules = [ordered]@{
    AppLockerWindowsPowerShell = (Test-Path -LiteralPath "$taskModuleRoot\AppLocker")
    HyperVWindowsPowerShell = (Test-Path -LiteralPath "$taskModuleRoot\Hyper-V")
}
$taskVmSoftware = @(Read-Observation 'VmSoftware' {
    Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
        Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -match 'VMware|VirtualBox|QEMU' } |
        Select-Object -ExpandProperty DisplayName
})
$taskVmCandidates = @()
foreach ($taskDirectory in @(
    "$env:USERPROFILE\Documents\Virtual Machines", "$env:USERPROFILE\VirtualBox VMs", 'D:\Virtual Machines', 'D:\VMs'
)) {
    if (Test-Path -LiteralPath $taskDirectory) {
        $taskFiles = @(Read-Observation 'VmCandidateFiles' {
            # Bounded to conventional directories and configuration extensions; not a whole-disk scan.
            Get-ChildItem -LiteralPath $taskDirectory -File -Recurse -Depth 2 -ErrorAction Stop |
                Where-Object { $_.Extension -in @('.vbox', '.vmx', '.vmcx', '.qcow2') }
        })
        $taskVmCandidates += [ordered]@{ ConventionalDirectoryExists = $true; ConfigurationCount = $taskFiles.Count }
    }
}
$taskNativeLibraries = @()
foreach ($taskLibrary in @('fwpuclnt.dll', 'appidapi.dll')) {
    $taskNativeLibraries += [ordered]@{
        Name = $taskLibrary
        Exists = (Test-Path -LiteralPath (Join-Path "$env:SystemRoot\System32" $taskLibrary))
        EnforcementTested = $false
    }
}
$taskRegistryKeys = @(
    'HKLM:\SOFTWARE\Policies\Microsoft\Windows\SrpV2',
    'HKLM:\SOFTWARE\Policies\Microsoft\Edge',
    'HKLM:\SOFTWARE\Policies\Google\Chrome'
)
$taskPolicyPresence = @($taskRegistryKeys | ForEach-Object {
    [ordered]@{ Key = $_; Exists = (Test-Path -LiteralPath $_) }
})
$taskReport = [ordered]@{
    SchemaVersion = 1
    CollectedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    ReadOnlyInventory = $true
    EnforcementTested = $false
    Os = $(if ($taskOs) { [ordered]@{ Caption = $taskOs.Caption; Version = $taskOs.Version; Build = $taskOs.BuildNumber; Architecture = $taskOs.OSArchitecture } } else { $null })
    Edition = $(if ($taskVersion) { $taskVersion.EditionID } else { $null })
    DisplayVersion = $(if ($taskVersion) { $taskVersion.DisplayVersion } else { $null })
    UpdateBuildRevision = $(if ($taskVersion) { $taskVersion.UBR } else { $null })
    CurrentProcessElevated = $taskPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    HypervisorPresent = $(if ($taskComputer) { $taskComputer.HypervisorPresent } else { $null })
    Services = $taskServices
    Modules = $taskModules
    Tools = $taskTools
    DotNetSdks = $taskSdks
    DotNetRuntimes = $taskRuntimes
    Browsers = $taskBrowsers
    VmSoftwareRegistered = $taskVmSoftware
    VmCandidateDirectories = $taskVmCandidates
    NativeLibraries = $taskNativeLibraries
    PolicyKeyPresence = $taskPolicyPresence
    ProbeErrors = @($taskErrors.ToArray())
    Interpretation = 'Presence is not enforcement proof; VM availability requires separate confirmation.'
}
$taskJson = $taskReport | ConvertTo-Json -Depth 10
if ($OutputPath) {
    $taskAbsoluteOutput = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
    # CreateNew avoids overwriting a file even if another process creates it after the initial check.
    $taskStream = [IO.File]::Open($taskAbsoluteOutput, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $taskBytes = (New-Object Text.UTF8Encoding($false)).GetBytes($taskJson)
        $taskStream.Write($taskBytes, 0, $taskBytes.Length)
    } finally { $taskStream.Dispose() }
}
$taskJson
