#Requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$taskMachine=Get-CimInstance Win32_ComputerSystem
if($taskMachine.Manufacturer -notmatch 'VMware' -or $taskMachine.Model -notmatch 'VMware') { throw 'VMware guest required; no files, tasks, services or policies changed.' }
$taskIdentity=[Security.Principal.WindowsIdentity]::GetCurrent()
if(-not(New-Object Security.Principal.WindowsPrincipal($taskIdentity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) -or -not [Environment]::Is64BitProcess -or $PSVersionTable.PSEdition -ne 'Desktop') { throw 'Administrator 64-bit Windows PowerShell 5.1 is required.' }
$taskRoot=Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'AICT-Beta-111e0fe3cfa348b4a055eafb99e25f06'
$taskReport=Join-Path $PSScriptRoot 'repair-result.json'
$taskStatus=[ordered]@{kind='AICT_BETA_SETUP_DIRECTORY_REPAIR';atUtc=[DateTimeOffset]::UtcNow.ToString('o');root=$taskRoot;phase='checking';completed=$false;createdProbe=$false;rulesReset=$false;error=$null}
function Save-RepairStatus { [IO.File]::WriteAllText($taskReport,($taskStatus|ConvertTo-Json -Depth 12),(New-Object Text.UTF8Encoding($false))) }
function Assert-RepairProtected([string]$Path) {
    $taskInfo=Get-Item -LiteralPath $Path
    if($taskInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse input refused.' }
    $taskAcl=Get-Acl -LiteralPath $Path
    if($taskAcl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin @('S-1-5-18','S-1-5-32-544')) { throw 'Privileged ownership mismatch.' }
    $taskWrite=[Security.AccessControl.FileSystemRights]::Write -bor [Security.AccessControl.FileSystemRights]::Delete -bor [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles
    foreach($taskRule in $taskAcl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) { if($taskRule.AccessControlType -eq 'Allow' -and $taskRule.IdentityReference.Value -notin @('S-1-5-18','S-1-5-32-544') -and ($taskRule.FileSystemRights -band $taskWrite)) { throw 'Ordinary-user-writable installation refused.' } }
}
try {
    Save-RepairStatus
    Assert-RepairProtected $taskRoot
    foreach($taskPath in @('config.json','MANIFEST.json','Service','Agent','App','scripts','state')) { Assert-RepairProtected (Join-Path $taskRoot $taskPath) }
    $taskConfig=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskRoot 'config.json')|ConvertFrom-Json
    if($taskConfig.runId -cne '111e0fe3cfa348b4a055eafb99e25f06' -or $taskConfig.probeSid -cne $taskIdentity.User.Value) { throw 'This repair is only for the installation and account from the supplied report.' }
    $taskManifestPath=Join-Path $taskRoot 'MANIFEST.json'
    if((Get-FileHash -LiteralPath $taskManifestPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne 'a048825f96fe2a78c97eb3e8594ee0ed35508111301b3817c172000c50beaf47') { throw 'Original frozen v1 installation manifest mismatch; no repair performed.' }
    $taskManifest=Get-Content -Raw -Encoding UTF8 -LiteralPath $taskManifestPath|ConvertFrom-Json
    foreach($taskFile in $taskManifest.files.PSObject.Properties) {
        if($taskFile.Name -match '(^[\\/]|\.\.|:)') { throw 'Invalid manifest path.' }
        $taskPath=Join-Path $taskRoot $taskFile.Name;Assert-RepairProtected $taskPath
        if((Get-FileHash -LiteralPath $taskPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $taskFile.Value) { throw ('Original installed file changed: '+$taskFile.Name) }
    }
    $taskStatus.phase='creating-runtime-directory';Save-RepairStatus
    $taskProbe=Join-Path $taskRoot 'probe'
    if(-not(Test-Path -LiteralPath $taskProbe)) {
        $null=New-Item -ItemType Directory -Path $taskProbe
        $taskAcl=Get-Acl -LiteralPath $taskProbe;$taskAcl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')));Set-Acl -LiteralPath $taskProbe -AclObject $taskAcl
        $taskStatus.createdProbe=$true
    }
    Assert-RepairProtected $taskProbe
    $taskStatus.phase='continuing-existing-installation';Save-RepairStatus
    & (Join-Path $taskRoot 'scripts\Install-Beta.ps1') -StartExisting
    $taskResult=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskRoot 'setup-result.json')|ConvertFrom-Json
    if(-not $taskResult.completed) { throw 'The original installer did not confirm completion.' }
    $taskStatus.completed=$true;$taskStatus.phase='completed'
}catch {
    $taskStatus.error=$_.Exception.ToString();$taskStatus.phase='failed'
    $taskOriginal=Join-Path $taskRoot 'setup-result.json'
    if(Test-Path -LiteralPath $taskOriginal) { try { Copy-Item -LiteralPath $taskOriginal -Destination (Join-Path $PSScriptRoot 'setup-result-after-repair.json') }catch{} }
}finally { Save-RepairStatus }
if(-not $taskStatus.completed) { throw 'Repair or resumed setup did not complete; see repair-result.json and setup-result-after-repair.json.' }
