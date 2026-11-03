#Requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskMachine=Get-CimInstance Win32_ComputerSystem
if ($taskMachine.Manufacturer -match 'VMware' -and $taskMachine.Model -match 'VMware') { throw 'Host refusal test must run on a non-VMware development host.' }
$taskBinary=Join-Path $taskRoot 'lab\build\service\AictServiceLab.exe'
foreach ($taskArgs in @('--service','--install','--inspect','--crash','--cleanup','--probe before-crash')) {
    $taskStart=New-Object Diagnostics.ProcessStartInfo
    $taskStart.FileName=$taskBinary;$taskStart.Arguments=$taskArgs;$taskStart.UseShellExecute=$false;$taskStart.CreateNoWindow=$true
    $taskStart.RedirectStandardOutput=$true;$taskStart.RedirectStandardError=$true
    $taskProcess=[Diagnostics.Process]::Start($taskStart)
    $taskText=$taskProcess.StandardOutput.ReadToEnd()+$taskProcess.StandardError.ReadToEnd()
    $taskProcess.WaitForExit();$taskExit=$taskProcess.ExitCode;$taskProcess.Dispose()
    if ($taskExit -ne 1 -or $taskText -notmatch 'VMware guest required') { throw ('Native host refusal failed: '+$taskArgs+' '+$taskText) }
}
foreach ($taskScript in @('ServiceLabCommon.ps1','Prepare-ServiceLab.ps1','Finish-ServiceLab.ps1','Recover-ServiceLab.ps1')) {
    $taskErrors=$null;$taskTokens=$null
    $null=[Management.Automation.Language.Parser]::ParseFile((Join-Path $taskRoot ('scripts\'+$taskScript)),[ref]$taskTokens,[ref]$taskErrors)
    if (@($taskErrors).Count) { throw ($taskErrors | Out-String) }
}
foreach ($taskScript in @('Prepare-ServiceLab.ps1','Finish-ServiceLab.ps1','Recover-ServiceLab.ps1')) {
    $taskRefused=$false
    try {
        if ($taskScript -eq 'Prepare-ServiceLab.ps1') { & (Join-Path $taskRoot ('scripts\'+$taskScript)) -SnapshotReference 'HOST_REFUSAL_ONLY' }
        else { & (Join-Path $taskRoot ('scripts\'+$taskScript)) }
    } catch { if ($_.Exception.Message -match 'VMware guest required') { $taskRefused=$true } else { throw } }
    if (-not $taskRefused) { throw ('Script host refusal failed: '+$taskScript) }
}
Write-Output 'Six native modes and three public scripts refused physical host before SCM/files; all four WinPS 5.1 scripts parsed.'
