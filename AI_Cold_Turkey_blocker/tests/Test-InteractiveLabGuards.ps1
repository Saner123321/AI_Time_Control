#Requires -Version 5.1
[CmdletBinding()]
param([string]$Payload)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
if(-not $Payload) { $Payload=(Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskRoot 'lab\build\interactive\last-build.json') | ConvertFrom-Json).payloadDirectory }
$taskMachine=Get-CimInstance Win32_ComputerSystem
if($taskMachine.Manufacturer -match 'VMware' -and $taskMachine.Model -match 'VMware') { throw 'Host refusal checks must run on the physical development host.' }
$taskCount=0
foreach($taskEntry in @(@{File='lab\AictInteractiveBridge.exe';Modes=@('--install','--inspect','--crash','--cleanup','--policy-read','--policy-block','--policy-allow','--probe baseline','--environment')},@{File='Service\Aict.Service.exe';Modes=@('--vm-lab','--vm-interactive','--lab-client','--workspace-smoke')})) {
    foreach($taskMode in $taskEntry.Modes) {
        $taskStart=New-Object Diagnostics.ProcessStartInfo;$taskStart.FileName=Join-Path $Payload $taskEntry.File;$taskStart.Arguments=$taskMode;$taskStart.UseShellExecute=$false;$taskStart.CreateNoWindow=$true;$taskStart.RedirectStandardOutput=$true;$taskStart.RedirectStandardError=$true
        $taskChild=[Diagnostics.Process]::Start($taskStart);$taskOut=$taskChild.StandardOutput.ReadToEndAsync();$taskErr=$taskChild.StandardError.ReadToEndAsync()
        if(-not $taskChild.WaitForExit(15000)) { $taskChild.Kill();throw 'Interactive host refusal hung.' }
        $taskCode=$taskChild.ExitCode;$taskChild.Dispose()
        if($taskCode -ne 1 -or ($taskOut.Result+$taskErr.Result) -notmatch 'VMware guest required') { throw ('Interactive physical-host refusal failed: '+$taskMode) }
        $taskCount++
    }
}
foreach($taskName in @('ServicePolicyLabCommon.ps1','InteractiveLabCommon.ps1','Install-InteractiveLab.ps1','Open-InteractiveLab.ps1','Collect-InteractiveLab.ps1','Recover-InteractiveLab.ps1','Build-InteractiveLab.ps1')) {
    $taskErrors=$null;$taskTokens=$null;$null=[Management.Automation.Language.Parser]::ParseFile((Join-Path $taskRoot ('scripts\'+$taskName)),[ref]$taskTokens,[ref]$taskErrors)
    if(@($taskErrors).Count) { throw ($taskErrors | Out-String) }
}
foreach($taskName in @('Install-InteractiveLab.ps1','Open-InteractiveLab.ps1','Collect-InteractiveLab.ps1','Recover-InteractiveLab.ps1')) {
    $taskRefused=$false
    try { if($taskName -eq 'Install-InteractiveLab.ps1') { & (Join-Path $taskRoot ('scripts\'+$taskName)) -SnapshotReference 'HOST_REFUSAL_ONLY' } else { & (Join-Path $taskRoot ('scripts\'+$taskName)) } }
    catch { if($_.Exception.Message -match 'VMware guest required') { $taskRefused=$true } else { throw } }
    if(-not $taskRefused) { throw ('Public physical-host entry not refused: '+$taskName) }
}
Write-Output ($taskCount.ToString()+' native modes and 4 interactive scripts refused physical host before writes; 7 PowerShell files parsed.')
