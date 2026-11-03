#Requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskMachine=Get-CimInstance Win32_ComputerSystem
if($taskMachine.Manufacturer -match 'VMware' -and $taskMachine.Model -match 'VMware') { throw 'This read-only refusal check is for the physical development host.' }
$taskBuild=Join-Path $taskRoot 'lab\build\service-policy'
foreach($taskEntry in @(@{File='AictPolicyBridge.exe';Modes=@('--install','--inspect','--crash','--cleanup','--policy-read','--policy-block','--policy-allow','--probe baseline')},@{File='Service\Aict.Service.exe';Modes=@('--vm-lab','--lab-client')})) {
    foreach($taskMode in $taskEntry.Modes) {
        $taskStart=New-Object Diagnostics.ProcessStartInfo
        $taskStart.FileName=Join-Path $taskBuild $taskEntry.File;$taskStart.Arguments=$taskMode;$taskStart.UseShellExecute=$false;$taskStart.CreateNoWindow=$true
        $taskStart.RedirectStandardOutput=$true;$taskStart.RedirectStandardError=$true
        $taskChild=[Diagnostics.Process]::Start($taskStart);$taskOut=$taskChild.StandardOutput.ReadToEndAsync();$taskErr=$taskChild.StandardError.ReadToEndAsync()
        if(-not $taskChild.WaitForExit(15000)) { $taskChild.Kill();throw 'Host refusal hung.' }
        $taskExit=$taskChild.ExitCode;$taskChild.Dispose()
        if($taskExit -ne 1 -or ($taskOut.Result+$taskErr.Result) -notmatch 'VMware guest required') { throw ('Physical host was not explicitly refused: '+$taskMode) }
    }
}
foreach($taskName in @('ServicePolicyLabCommon.ps1','Invoke-ServicePolicyLab.ps1','Recover-ServicePolicyLab.ps1','Build-ServicePolicyLab.ps1')) {
    $taskErrors=$null;$taskTokens=$null;$null=[Management.Automation.Language.Parser]::ParseFile((Join-Path $taskRoot ('scripts\'+$taskName)),[ref]$taskTokens,[ref]$taskErrors)
    if(@($taskErrors).Count) { throw ($taskErrors | Out-String) }
}
foreach($taskName in @('Invoke-ServicePolicyLab.ps1','Recover-ServicePolicyLab.ps1')) {
    $taskRefused=$false
    try { if($taskName -eq 'Invoke-ServicePolicyLab.ps1') { & (Join-Path $taskRoot ('scripts\'+$taskName)) -SnapshotReference 'HOST_REFUSAL_ONLY' -SnapshotRestoreConfirmed } else { & (Join-Path $taskRoot ('scripts\'+$taskName)) } }
    catch { if($_.Exception.Message -match 'VMware guest required') { $taskRefused=$true } else { throw } }
    if(-not $taskRefused) { throw ('Public host entry not refused: '+$taskName) }
}
Write-Output '10 native modes and 2 guest scripts refused the physical host before writes; 4 PowerShell files parsed.'
