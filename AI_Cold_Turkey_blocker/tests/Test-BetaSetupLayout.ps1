#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Bridge,[Parameter(Mandatory=$true)][string]$Payload)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskStage=Join-Path $taskRoot ('artifacts\beta-layout-'+[DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
foreach($taskFolder in @('Service','state','Targets')) { $null=New-Item -ItemType Directory -Path (Join-Path $taskStage $taskFolder) -Force }
foreach($taskFile in @('Targets\Target.exe','Targets\Renamed.exe','Targets\AllowedControl.exe','MovedTarget.exe')) { Copy-Item -LiteralPath (Join-Path $Payload $taskFile) -Destination (Join-Path $taskStage $taskFile) }
function Check-Layout([bool]$Expected) {
    $taskStart=New-Object Diagnostics.ProcessStartInfo;$taskStart.FileName=$Bridge;$taskStart.Arguments='--validate-layout "'+$taskStage+'"';$taskStart.UseShellExecute=$false;$taskStart.CreateNoWindow=$true;$taskStart.RedirectStandardOutput=$true;$taskStart.RedirectStandardError=$true;$taskStart.StandardErrorEncoding=[Text.Encoding]::UTF8;$taskStart.StandardOutputEncoding=[Text.Encoding]::UTF8
    $taskProcess=[Diagnostics.Process]::Start($taskStart);$taskOutput=$taskProcess.StandardOutput.ReadToEndAsync();$taskError=$taskProcess.StandardError.ReadToEndAsync();if(-not $taskProcess.WaitForExit(10000)){$taskProcess.Kill();throw 'Layout validation timeout'}
    if($Expected) { if($taskProcess.ExitCode -ne 0){throw $taskError.Result} }
    elseif($taskProcess.ExitCode -ne 1 -or $taskError.Result -notmatch 'probe') { throw 'Missing runtime directory did not reproduce the reported failure.' }
}
Check-Layout $false
# Run the actual installer block that creates runtime directories, using a project-local stage.
$taskTokens=$null;$taskErrors=$null
$taskAst=[Management.Automation.Language.Parser]::ParseFile((Join-Path $taskRoot 'scripts\Install-Beta.ps1'),[ref]$taskTokens,[ref]$taskErrors)
if($taskErrors.Count){throw 'Installer syntax failed'}
$taskBlock=$taskAst.FindAll({param($node) $node -is [Management.Automation.Language.ForEachStatementAst] -and $node.Variable.VariablePath.UserPath -ceq 'taskRuntimeFolder'},$true)
if(@($taskBlock).Count -ne 1){throw 'Expected exactly one runtime layout initializer'}
function Set-BetaOwner([string]$Path) { if(-not(Test-Path -LiteralPath $Path -PathType Container)){throw 'Runtime directory missing'} }
function Assert-BetaProtected([string]$Path) { if((Get-Item -LiteralPath $Path).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Reparse test input'} }
$taskInstalledRoot=$taskRoot
try { $taskRoot=$taskStage;& ([ScriptBlock]::Create($taskBlock[0].Extent.Text));Check-Layout $true;& ([ScriptBlock]::Create($taskBlock[0].Extent.Text));Check-Layout $true }
finally { $taskRoot=$taskInstalledRoot }
Write-Output ('PASS: original missing-probe failure reproduced; actual installer runtime-directory block restores native layout and is safe to retry. Evidence: '+$taskStage)
