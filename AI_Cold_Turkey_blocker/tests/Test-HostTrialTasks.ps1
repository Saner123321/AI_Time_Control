#Requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$taskProject=Split-Path -Parent $PSScriptRoot
$taskStage=Join-Path $taskProject ('artifacts\host-task-bindings-'+[DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
$null=New-Item -ItemType Directory -Path $taskStage
$taskEnd=[DateTimeOffset]::UtcNow.AddMinutes(30)
[IO.File]::WriteAllText((Join-Path $taskStage 'config.json'),(@{hostTrialEndsUtc=$taskEnd.ToString('o')}|ConvertTo-Json))
$taskTokens=$null;$taskErrors=$null;$taskAst=[Management.Automation.Language.Parser]::ParseFile((Join-Path $taskProject 'scripts\Install-Beta.ps1'),[ref]$taskTokens,[ref]$taskErrors)
if(@($taskErrors).Count){throw 'Installer parse failed'}
$taskFunctions=$taskAst.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Ensure-BetaTask'},$true)
. ([ScriptBlock]::Create($taskFunctions[0].Extent.Text))
$script:records=New-Object 'System.Collections.Generic.List[object]';$script:secured=New-Object 'System.Collections.Generic.List[string]'
function Get-ScheduledTask([string]$TaskName,$ErrorAction){return $null}
function New-ScheduledTaskAction([string]$Execute,[string]$Argument){return @{Execute=$Execute;Arguments=$Argument}}
function New-ScheduledTaskPrincipal([string]$UserId,[string]$LogonType,[string]$RunLevel){return @{UserId=$UserId;LogonType=$LogonType;RunLevel=$RunLevel}}
function New-ScheduledTaskTrigger([switch]$Once,[DateTime]$At,[switch]$AtLogOn,[string]$User){return @{Once=$Once.IsPresent;At=$At;AtLogOn=$AtLogOn.IsPresent;User=$User}}
function New-ScheduledTaskSettingsSet([TimeSpan]$ExecutionTimeLimit,[int]$RestartCount,[TimeSpan]$RestartInterval,[switch]$StartWhenAvailable,[switch]$AllowStartIfOnBatteries,[switch]$DontStopIfGoingOnBatteries){return @{StartWhenAvailable=$StartWhenAvailable.IsPresent}}
function Register-ScheduledTask([string]$TaskName,$Action,$Principal,$Trigger,$Settings){$script:records.Add(@{Name=$TaskName;Action=$Action;Principal=$Principal;Trigger=$Trigger;Settings=$Settings})}
function Set-BetaTaskSecurity([string]$Name){$script:secured.Add($Name)}
$taskSid='S-1-5-21-1-2-3-1001';$taskContext=[pscustomobject]@{Id='12345678123412341234123456789abc';Root=$taskStage}
foreach($taskKind in @('Recovery','Uninstall','Agent','TrialSafety')){Ensure-BetaTask $taskContext $taskKind}
if($records.Count -ne 4 -or $secured.Count -ne 4){throw 'Task graph incomplete'}
$taskSafety=@($records | Where-Object {$_.Name -like '*TrialSafety*'})[0]
if($taskSafety.Action.Arguments -cne '--host-trial-safety' -or $taskSafety.Principal.UserId -cne 'S-1-5-18' -or $taskSafety.Principal.RunLevel -cne 'Highest' -or -not $taskSafety.Trigger.Once -or -not $taskSafety.Settings.StartWhenAvailable){throw 'Safety action/principal/trigger mismatch'}
if(($taskSafety.Trigger.At-$taskEnd.AddMinutes(1).LocalDateTime).Duration().TotalMilliseconds -gt 1){throw 'Safety deadline is not trial end plus one minute'}
$taskAgent=@($records|Where-Object {$_.Name -like '*-Agent-*'})[0]
if($taskAgent.Principal.UserId -cne $taskSid -or $taskAgent.Principal.RunLevel -cne 'Limited' -or $taskAgent.Action.Arguments -cne '--session-agent'){throw 'Collector privilege binding mismatch'}
$taskRecovery=@($records|Where-Object {$_.Name -like '*-Recovery-*'})[0]
if($taskRecovery.Action.Arguments -cne '--beta-cleanup-force'){throw 'Independent recovery action mismatch'}
[IO.File]::WriteAllText((Join-Path $taskStage 'result.json'),(@{passed=$true;taskCount=4;safetyDelayAfterTrialSeconds=60;actualServiceOrTaskRegistrations=$false}|ConvertTo-Json))
Write-Output ('4 host task bindings and independent safety deadline passed with explicit registration substitutes. Evidence: '+$taskStage)
