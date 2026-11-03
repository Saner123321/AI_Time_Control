#Requires -Version 5.1
# Pure mocked task state tests: no ScheduledTasks cmdlet is executed.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskSource = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts\Close-CspNativeExperiment.ps1'
$taskTokens=$null; $taskErrors=$null
$taskAst=[Management.Automation.Language.Parser]::ParseFile($taskSource,[ref]$taskTokens,[ref]$taskErrors)
if($taskErrors.Count){throw 'Closeout source does not parse'}
$taskHelper=$taskAst.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Close-AictExactTask'},$true)
if($taskHelper.Count -ne 1){throw 'Expected one production helper'}
# Load only that function into this test scope, never the script entry point.
. ([scriptblock]::Create($taskHelper[0].Extent.Text))
$script:MockTask=$null; $script:StopCount=0; $script:RemoveCount=0; $script:KeepAfterRemove=$false
function Get-ScheduledTask { param($TaskName,$ErrorAction) return $script:MockTask }
function Stop-ScheduledTask { param($TaskName,$ErrorAction) $script:StopCount++;$script:MockTask.State='Ready' }
function Unregister-ScheduledTask { param($TaskName,[switch]$Confirm,$ErrorAction) $script:RemoveCount++;if(-not $script:KeepAfterRemove){$script:MockTask=$null} }
function Reset-Mock {
    $script:MockTask=[pscustomobject]@{Principal=[pscustomobject]@{UserId='S-1-5-18'};Actions=@([pscustomobject]@{Execute='fixed.exe';Arguments='--cleanup'});State='Ready'}
    $script:StopCount=0;$script:RemoveCount=0;$script:KeepAfterRemove=$false
}
$taskResult=Close-AictExactTask -Name 'fixed' -Execute 'fixed.exe' -Arguments '--cleanup' -MayStop $true
if($taskResult.Outcome -ne 'ALREADY_ABSENT' -or $script:RemoveCount){throw 'Absent task guard failed'}
Reset-Mock;$script:MockTask.Actions[0].Execute='changed.exe'
$taskResult=Close-AictExactTask -Name 'fixed' -Execute 'fixed.exe' -Arguments '--cleanup' -MayStop $true
if($taskResult.Outcome -ne 'DEFINITION_CHANGED_LEFT_UNTOUCHED' -or $script:RemoveCount){throw 'Changed action guard failed'}
Reset-Mock;$script:MockTask.Principal.UserId='another-user'
$taskResult=Close-AictExactTask -Name 'fixed' -Execute 'fixed.exe' -Arguments '--cleanup' -MayStop $true
if($taskResult.Outcome -ne 'DEFINITION_CHANGED_LEFT_UNTOUCHED' -or $script:RemoveCount){throw 'Principal guard failed'}
Reset-Mock;$script:MockTask.State='Running'
$taskResult=Close-AictExactTask -Name 'fixed' -Execute 'fixed.exe' -Arguments '--cleanup' -MayStop $false
if($taskResult.Outcome -ne 'RUNNING_AUDIT_LEFT_UNTOUCHED' -or $script:RemoveCount -or $script:StopCount){throw 'Running audit guard failed'}
Reset-Mock;$script:MockTask.State='Running'
$taskResult=Close-AictExactTask -Name 'fixed' -Execute 'fixed.exe' -Arguments '--cleanup' -MayStop $true
if($taskResult.Outcome -ne 'REMOVED_AND_ABSENCE_VERIFIED' -or $script:RemoveCount -ne 1 -or $script:StopCount -ne 1){throw 'Own cleanup task removal failed'}
Reset-Mock;$script:KeepAfterRemove=$true;$taskRejected=$false
try{$null=Close-AictExactTask -Name 'fixed' -Execute 'fixed.exe' -Arguments '--cleanup' -MayStop $true}catch{$taskRejected=$true}
if(-not $taskRejected){throw 'Task persistence after removal was ignored'}
Write-Output '{"kind":"MOCKED_CLOSEOUT_GUARDS_NO_TASK_CHANGES","cases":6,"passed":true}'
