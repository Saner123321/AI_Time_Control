#Requires -Version 5.1
[CmdletBinding()]
param([string]$UpdaterPath)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$taskProject=Split-Path -Parent $PSScriptRoot
$taskStage=Join-Path $taskProject ('artifacts\continuous-use-'+[DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
$null=New-Item -ItemType Directory -Path $taskStage
$taskRoot=Join-Path $taskStage 'install';$taskBackup=Join-Path $taskStage 'backup'
$null=New-Item -ItemType Directory -Path $taskRoot,$taskBackup
function Import-TestFunctions([string]$Path,[string[]]$Names) {
    $taskTokens=$null;$taskErrors=$null
    $taskAst=[Management.Automation.Language.Parser]::ParseFile($Path,[ref]$taskTokens,[ref]$taskErrors)
    if(@($taskErrors).Count){throw 'Production script parse failed.'}
    foreach($taskFunctionName in $Names){
        $taskNode=@($taskAst.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $taskFunctionName},$true))
        if($taskNode.Count -ne 1){throw 'Production function missing.'}
        $taskDefinition=$taskNode[0].Extent.Text -replace ('^function '+[regex]::Escape($taskFunctionName)),('function script:'+$taskFunctionName)
        . ([ScriptBlock]::Create($taskDefinition))
    }
}
if(-not $UpdaterPath){$UpdaterPath=Join-Path $taskProject 'scripts\Update-Continuous.ps1'}
Import-TestFunctions $UpdaterPath @('Write-UpdateJson','Assert-UpdateTrialSafety','Pause-UpdateTrialSafety','Set-UpdateContinuousMode','Complete-UpdateTrialSafety','Restore-UpdateTrialSafety')
Import-TestFunctions (Join-Path $taskProject 'scripts\Install-Beta.ps1') @('Sync-BetaTrialSafety')
# Real config-file operations, explicit substitutes for all system/task/ownership operations.
function Set-UpdateOwner([string]$Path){if(-not(Test-Path -LiteralPath $Path)){throw 'Missing fixture file'}}
$script:present=$null;$script:disabled=0;$script:enabled=0;$script:removed=0;$script:triggerSets=0;$script:created=0
function Get-ScheduledTask([string]$TaskName,$ErrorAction){if($TaskName -cne $taskSafetyName){throw 'Unexpected task'};return $script:present}
function Disable-ScheduledTask([string]$TaskName){$script:disabled++;$script:present.State='Disabled'}
function Enable-ScheduledTask([string]$TaskName){$script:enabled++;$script:present.State='Ready'}
function Unregister-ScheduledTask([string]$TaskName,[switch]$Confirm){$script:removed++;$script:present=$null}
function Set-ScheduledTask([string]$TaskName,$Trigger){$script:triggerSets++;$script:present.Triggers=$Trigger}
function Ensure-BetaTask($Context,[string]$Kind){if($Kind -cne 'TrialSafety'){throw 'Unexpected task kind'};$script:created++}
function Assert-Test([bool]$Value,[string]$Message){if(-not $Value){throw $Message}}
function Assert-Refused([scriptblock]$Action){$taskRejected=$false;try{& $Action}catch{$taskRejected=$true};Assert-Test $taskRejected 'Changed/running task must be refused.'}
$taskId='12345678123412341234123456789abc';$taskSafetyName='AICT-Beta-TrialSafety-'+$taskId
function New-TestSafety {return [pscustomobject]@{Actions=@([pscustomobject]@{Execute=(Join-Path $taskRoot 'Service\Aict.Beta.Service.exe');Arguments='--host-trial-safety'});Principal=[pscustomobject]@{UserId='S-1-5-18'};State='Ready';Triggers=@('original-deadline')}}
$taskStatus=[ordered]@{newServiceStartAttempted=$false;obsoleteSafetyRemoved=$false}
$taskSafetyPaused=$false;$taskSafetyTrigger=$null;$taskSafetyWasEnabled=$false
$taskModifiedFiles=New-Object 'System.Collections.Generic.List[string]'
$taskEnd=[DateTimeOffset]::UtcNow.AddMinutes(30).ToString('o')
$taskConfig=[pscustomobject]@{hostTrial=$true;hostTrialEndsUtc=$taskEnd;runId=$taskId;probeSid='S-1-5-21-1-2-3-1001'}
Write-UpdateJson (Join-Path $taskRoot 'config.json') $taskConfig
[IO.File]::WriteAllText((Join-Path $taskRoot 'engine.sqlite'),'fixture database bytes remain identical')
$taskDb=[IO.File]::ReadAllBytes((Join-Path $taskRoot 'engine.sqlite'))
$cases=0
Pause-UpdateTrialSafety;Assert-Test (-not $taskSafetyPaused -and $disabled -eq 0) 'Absent task should need no operation';$cases++
$script:present=New-TestSafety;Pause-UpdateTrialSafety;Assert-Test ($taskSafetyPaused -and $disabled -eq 1 -and $taskSafetyWasEnabled) 'Owned task must be paused before migration';$cases++
Set-UpdateContinuousMode
$taskActual=Get-Content -Raw -LiteralPath (Join-Path $taskRoot 'config.json')|ConvertFrom-Json
$taskOld=Get-Content -Raw -LiteralPath (Join-Path $taskBackup 'config.json')|ConvertFrom-Json
Assert-Test ($taskActual.continuousUse -and $null -eq $taskActual.hostTrialEndsUtc -and $taskActual.runId -ceq $taskId -and $taskOld.hostTrialEndsUtc -ceq $taskEnd) 'Explicit continuous mode must preserve identity and backup the original deadline';$cases++
Restore-UpdateTrialSafety;Assert-Test ($enabled -eq 0 -and $taskSafetyPaused) 'Never rearm old timer while config is continuous';$cases++
Complete-UpdateTrialSafety;Assert-Test ($null -eq $present -and $removed -eq 1 -and -not $taskSafetyPaused -and $taskStatus.obsoleteSafetyRemoved) 'Successful migration must remove only owned timer';$cases++
$script:present=New-TestSafety;Pause-UpdateTrialSafety
Copy-Item -LiteralPath (Join-Path $taskBackup 'config.json') -Destination (Join-Path $taskRoot 'config.json') -Force
Restore-UpdateTrialSafety;Assert-Test ($enabled -eq 1 -and $triggerSets -eq 1 -and -not $taskSafetyPaused) 'Rollback to finite trial must restore original timer';$cases++
Pause-UpdateTrialSafety;$taskStatus.newServiceStartAttempted=$true;Restore-UpdateTrialSafety;Assert-Test ($enabled -eq 1 -and $taskSafetyPaused) 'Do not rearm after new backend start attempt';$cases++
$taskStatus.newServiceStartAttempted=$false;$script:present=New-TestSafety;$script:present.Actions[0].Execute='unrelated.exe';$before=$disabled;Assert-Refused {Pause-UpdateTrialSafety};Assert-Test ($disabled -eq $before) 'Foreign action must stay unchanged';$cases++
$script:present=New-TestSafety;$script:present.State='Running';Assert-Refused {Pause-UpdateTrialSafety};$cases++
$context=[pscustomobject]@{Id=$taskId;Root=$taskRoot};$script:present=$null
Sync-BetaTrialSafety $context ([pscustomobject]@{hostTrial=$true;continuousUse=$true});Assert-Test ($created -eq 0) 'Continuous repair must not create safety task';$cases++
$script:present=New-TestSafety;Sync-BetaTrialSafety $context ([pscustomobject]@{hostTrial=$true;continuousUse=$true});Assert-Test ($removed -eq 2 -and $null -eq $present) 'Continuous repair removes owned obsolete task';$cases++
Sync-BetaTrialSafety $context ([pscustomobject]@{hostTrial=$true;hostTrialEndsUtc=$taskEnd});Assert-Test ($created -eq 1) 'Legacy finite trial retains safety task';$cases++
$script:present=New-TestSafety;$script:present.Principal.UserId='S-1-5-21-1-2-3-1001';Assert-Refused {Sync-BetaTrialSafety $context ([pscustomobject]@{hostTrial=$true;continuousUse=$true})};$cases++
Assert-Test ([Convert]::ToBase64String($taskDb) -ceq [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $taskRoot 'engine.sqlite')))) 'Database must remain byte-for-byte unchanged';$cases++
[IO.File]::WriteAllText((Join-Path $taskStage 'result.json'),(@{passed=$true;cases=$cases;actualTaskOrServiceChanges=$false;databaseUnchanged=$true}|ConvertTo-Json))
Write-Output ($cases.ToString()+' continuous-mode/timer ownership/rollback cases passed. Evidence: '+$taskStage)
