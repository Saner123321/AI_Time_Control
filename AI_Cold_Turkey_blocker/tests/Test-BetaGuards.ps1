#Requires -Version 5.1
[CmdletBinding()]
param([string]$Payload)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
if(-not $Payload) { $Payload=(Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskRoot 'lab\build\beta\last-build.json') | ConvertFrom-Json).payloadDirectory }
if((Get-CimInstance Win32_ComputerSystem).Manufacturer -match 'VMware') { throw 'Run host refusal checks on physical development host.' }
$taskCount=0
foreach($taskEntry in @(@{Exe='BetaBridge.exe';Modes=@('--install','--start','--inspect','--crash','--cleanup','--policy-read','--policy-block','--policy-allow','--environment','--probe baseline')},@{Exe='Service\Aict.Beta.Service.exe';Modes=@('--vm-beta','--beta-cleanup','--beta-cleanup-force','--request-recovery','--check-state','--check-reinstall','--import-v1 12345678123412341234123456789abc')},@{Exe='Agent\Aict.Beta.Agent.exe';Modes=@('--session-agent','chrome-extension://bihecbkklbnhcieebflmgjpkdjmmaage/')},@{Exe='App\Aict.Beta.Desktop.exe';Modes=@('--guard-check')})) {
    foreach($taskMode in $taskEntry.Modes) {
        $taskStart=New-Object Diagnostics.ProcessStartInfo;$taskStart.FileName=Join-Path $Payload $taskEntry.Exe;$taskStart.Arguments=$taskMode;$taskStart.UseShellExecute=$false;$taskStart.CreateNoWindow=$true;$taskStart.RedirectStandardError=$true;$taskStart.RedirectStandardOutput=$true
        $taskChild=[Diagnostics.Process]::Start($taskStart);$taskOut=$taskChild.StandardOutput.ReadToEndAsync();$taskErr=$taskChild.StandardError.ReadToEndAsync()
        if(-not $taskChild.WaitForExit(15000)) { $taskChild.Kill();throw 'Host refusal process hung.' }
        if($taskChild.ExitCode -ne 1 -or ($taskOut.Result+$taskErr.Result) -notmatch 'VMware guest required') { throw ('Host enforcement entry did not refuse: '+$taskMode) };$taskCount++
    }
}
foreach($taskSwitch in @('', '-StartExisting','-Recover','-NewSession')) {
    $taskRefused=$false
    try { if($taskSwitch -eq '') { & (Join-Path $taskRoot 'scripts\Install-Beta.ps1') }elseif($taskSwitch -eq '-Recover') { & (Join-Path $taskRoot 'scripts\Install-Beta.ps1') -Recover }elseif($taskSwitch -eq '-NewSession') { & (Join-Path $taskRoot 'scripts\Install-Beta.ps1') -NewSession }else { & (Join-Path $taskRoot 'scripts\Install-Beta.ps1') -StartExisting } }catch { if($_.Exception.Message -match 'VMware guest required') { $taskRefused=$true }else{throw} }
    if(-not $taskRefused) { throw 'Privileged installer did not refuse host.' }
}
foreach($taskScript in @('scripts\Install-Beta.ps1','scripts\Build-Beta.ps1','tests\Test-BetaBindings.ps1')) { $taskErrors=$null;$taskTokens=$null;$null=[Management.Automation.Language.Parser]::ParseFile((Join-Path $taskRoot $taskScript),[ref]$taskTokens,[ref]$taskErrors);if(@($taskErrors).Count){throw ($taskErrors|Out-String)} }
& (Join-Path $Payload 'Service\Aict.Beta.Service.exe') --offline-check
if($LASTEXITCODE -ne 0) { throw 'Offline WFP ABI check failed.' }
Write-Output ($taskCount.ToString()+' executable modes and 4 installer modes refused physical host; 3 scripts parsed. No system enforcement attempted.')
