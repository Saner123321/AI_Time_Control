#Requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskMachine=Get-CimInstance Win32_ComputerSystem
if ($taskMachine.Manufacturer -match 'VMware' -and $taskMachine.Model -match 'VMware') { throw 'Host-refusal test is for a non-VMware development host only.' }
$taskBinary=Join-Path $taskRoot 'lab\build\AictAppLockerEnforceSmoke.exe'
foreach ($taskMode in @('--enforce','--cleanup','--probe')) {
    # Capture bytes directly; WinPS formatting can wrap the refusal message mid-word.
    $taskStart=New-Object Diagnostics.ProcessStartInfo
    $taskStart.FileName=$taskBinary; $taskStart.Arguments=$taskMode
    $taskStart.UseShellExecute=$false; $taskStart.CreateNoWindow=$true
    $taskStart.RedirectStandardOutput=$true; $taskStart.RedirectStandardError=$true
    $taskProcess=[Diagnostics.Process]::Start($taskStart)
    $taskText=$taskProcess.StandardOutput.ReadToEnd() + $taskProcess.StandardError.ReadToEnd()
    $taskProcess.WaitForExit(); $taskExit=$taskProcess.ExitCode; $taskProcess.Dispose()
    if ($taskExit -ne 1 -or $taskText -notmatch 'VMware guest required') { throw ('Host refusal failed: ' + $taskMode + '; exit=' + $taskExit + '; output=' + $taskText) }
}
$taskParserErrors=$null; $taskTokens=$null
$null=[Management.Automation.Language.Parser]::ParseFile((Join-Path $taskRoot 'scripts\Invoke-AppLockerEnforceSmoke.ps1'),[ref]$taskTokens,[ref]$taskParserErrors)
if (@($taskParserErrors).Count -ne 0) { throw ($taskParserErrors | Out-String) }
$taskRefused=$false
try { & (Join-Path $taskRoot 'scripts\Invoke-AppLockerEnforceSmoke.ps1') -SnapshotReference 'OFFLINE_GUARD_TEST_NO_VM' }
catch { if ($_.Exception.Message -match 'VMware guest required') { $taskRefused=$true } else { throw } }
if (-not $taskRefused) { throw 'Runner did not refuse physical host.' }
Write-Host 'Three native modes and the runner refused the physical host. WinPS 5.1 syntax passed. No enforcement performed.'
