#Requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition -ne 'Desktop') { throw 'Use inbox Windows PowerShell 5.1.' }
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskBuild=(Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskRoot 'lab\build\desktop-browser\last-build.json') | ConvertFrom-Json).buildDirectory
$taskExe=Join-Path $taskBuild '专注计划.exe'
$taskAssembly=[Reflection.Assembly]::LoadFile($taskExe)
$taskProgram=$taskAssembly.GetType('Aict.DesktopPortal.Program',$true)
$taskFlags=[Reflection.BindingFlags]::Static -bor [Reflection.BindingFlags]::NonPublic
$taskRead=$taskProgram.GetMethod('Read',$taskFlags)
$taskItems=$taskProgram.GetMethod('Items',$taskFlags)
$taskJson=$taskRead.Invoke($null,[object[]]@([string](Join-Path $taskRoot 'reports\interactive-session-20261103T035831893Z\workspace-status.json')))
$taskItemArgs=New-Object object[] 1;$taskItemArgs[0]=$taskJson['status']['policy']['groups'].PSObject.BaseObject
$taskGroups=@($taskItems.Invoke($null,$taskItemArgs))
if($taskGroups.Count -ne 1 -or $taskGroups[0]['name'] -cne '我的应用限制' -or $taskGroups[0]['cycle']['blockFor'] -cne '00:02:00') { throw 'Returned real rule cannot be read by normal desktop entry.' }
$taskHashes=$taskRead.Invoke($null,[object[]]@([string](Join-Path $taskBuild 'BASE-APP-HASHES.json')))
if(-not $taskHashes.ContainsKey('App/Aict.Desktop.exe') -or -not $taskHashes.ContainsKey('App/Aict.Desktop.dll')) { throw 'Frozen original GUI identities missing.' }
$taskGuest=(Get-CimInstance Win32_ComputerSystem).Manufacturer -match 'VMware'
if($taskGuest) { throw 'Run physical-host refusal checks on the development host.' }
$taskCount=0
foreach($taskMode in @('Guard','Shortcut','Apply','Restore')) {
    $taskArgs=switch($taskMode) { 'Apply' { @('Edge') };'Restore' { @('12345678123412341234123456789abc') };default { @() } }
    $taskRefused=$false
    $taskInvokeArgs=@($taskArgs | ForEach-Object { $_.PSObject.BaseObject })
    try { $null=$taskProgram.GetMethod($taskMode,$taskFlags).Invoke($null,[object[]]$taskInvokeArgs) }
    catch { if($_.Exception.InnerException.Message -match 'VMware guest required') { $taskRefused=$true } else { throw } }
    if(-not $taskRefused) { throw ('Physical-host operation not refused: '+$taskMode) };$taskCount++
}
$taskStart=New-Object Diagnostics.ProcessStartInfo;$taskStart.FileName=$taskExe;$taskStart.Arguments='--host-refusal-check';$taskStart.UseShellExecute=$false;$taskStart.CreateNoWindow=$true;$taskStart.RedirectStandardError=$true
$taskChild=[Diagnostics.Process]::Start($taskStart);$taskError=$taskChild.StandardError.ReadToEndAsync();if(-not $taskChild.WaitForExit(10000) -or $taskChild.ExitCode -ne 1 -or $taskError.Result -notmatch 'VMware guest required') { throw 'Normal entry executable failed physical-host refusal.' }
Write-Output ('Real returned rule parsed, frozen GUI identity metadata present; '+$taskCount+' operations and normal executable refused host before writes.')
