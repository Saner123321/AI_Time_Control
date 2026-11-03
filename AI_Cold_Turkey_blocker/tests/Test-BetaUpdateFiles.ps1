#Requires -Version 5.1
[CmdletBinding()]
param([string]$ScriptPath)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition -ne 'Desktop'){throw 'Use Windows PowerShell 5.1, matching the guest updater.'}
Import-Module -Name (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1') -Force -ErrorAction Stop
$taskProject=Split-Path -Parent $PSScriptRoot
$taskEvidence=Join-Path $taskProject ('artifacts\update-file-tests-'+[DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
$null=New-Item -ItemType Directory -Path $taskEvidence
$taskTokens=$null;$taskErrors=$null
if(-not $ScriptPath){$ScriptPath=Join-Path $taskProject 'scripts\Update-BetaRules.ps1'}
$taskAst=[Management.Automation.Language.Parser]::ParseFile($ScriptPath,[ref]$taskTokens,[ref]$taskErrors)
if(@($taskErrors).Count){throw ($taskErrors|Out-String)}
foreach($taskFunction in @('Write-UpdateJson','Ensure-UpdateParent','Copy-UpdateProgramFile','Restore-UpdateAfterFailure')) {
    $taskFound=$taskAst.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $taskFunction},$true)
    if(@($taskFound).Count -ne 1){throw 'Production updater function missing'}
    . ([ScriptBlock]::Create($taskFound[0].Extent.Text))
}
# Filesystem operations below are production code, using only project-local fixtures.
# Privileged ownership and service/task operations are explicit test substitutes.
function Set-UpdateOwner([string]$Path){if(-not(Test-Path -LiteralPath $Path)){throw 'Fixture ownership input missing'}}
function Assert-UpdateProtected([string]$Path){if((Get-Item -LiteralPath $Path).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Reparse fixture refused'}}
function Start-Service([string]$Name){if($Name -cne 'TEST-OWNED-BACKEND'){throw 'Unexpected service requested'};$script:backendStarts++}
function Get-Service([string]$Name){if($Name -cne 'TEST-OWNED-BACKEND'){throw 'Unexpected service query'};$taskFake=[pscustomobject]@{Status='Running'};$taskFake|Add-Member -MemberType ScriptMethod -Name WaitForStatus -Value {param($Expected,$Timeout) if($Expected -ne 'Running'){throw 'Unexpected wait'}};return $taskFake}
function Enable-ScheduledTask([string]$TaskName){if($TaskName -cne 'TEST-OWNED-COLLECTOR'){throw 'Unexpected task'};$script:taskEnables++}
function Start-ScheduledTask([string]$TaskName){if($TaskName -cne 'TEST-OWNED-COLLECTOR'){throw 'Unexpected task'};$script:collectorStarts++}
function Assert-Test([bool]$Condition,[string]$Message){if(-not $Condition){throw $Message}}
function Initialize-Case([string]$Name){
    $script:taskRoot=Join-Path $taskEvidence $Name;$script:taskPackage=Join-Path $taskRoot 'package';$script:taskBackup=Join-Path $taskRoot 'backup'
    foreach($taskDir in @('Agent','Service','state','backup\Agent','backup\Service','package\payload\Agent','package\payload\Service')){$null=New-Item -ItemType Directory -Path (Join-Path $taskRoot $taskDir) -Force}
    [IO.File]::WriteAllText((Join-Path $taskRoot 'state\engine.sqlite'),'RULES_AND_QUOTA_SENTINEL_NO_RESET')
    [IO.File]::WriteAllText((Join-Path $taskRoot 'Agent\Aict.Beta.Agent.dll'),'old-agent')
    [IO.File]::WriteAllText((Join-Path $taskBackup 'Agent\Aict.Beta.Agent.dll'),'old-agent')
    [IO.File]::WriteAllText((Join-Path $taskRoot 'Service\Aict.Beta.Service.dll'),'old-service')
    [IO.File]::WriteAllText((Join-Path $taskBackup 'Service\Aict.Beta.Service.dll'),'old-service')
    [IO.File]::WriteAllText((Join-Path $taskPackage 'payload\Agent\Aict.Beta.Agent.dll'),'new-agent')
    [IO.File]::WriteAllText((Join-Path $taskPackage 'payload\Service\Aict.Beta.Service.dll'),'new-service')
    $script:taskStatus=[ordered]@{newServiceStartAttempted=$false;oldBackendRestored=$false;collectorRestartRequested=$false;browserBridgeRestored=$false;rollbackErrors=@();modifiedFiles=@();rollbackBeforeServiceStart=$false}
    $script:taskModifiedFiles=New-Object 'System.Collections.Generic.List[string]';$script:taskStopped=$true;$script:taskAgentWasEnabled=$true
    $script:taskName='TEST-OWNED-BACKEND';$script:taskAgentName='TEST-OWNED-COLLECTOR';$script:taskMetadata=[pscustomobject]@{files=[pscustomobject]@{}}
    $script:taskHost=[pscustomobject]@{name='com.aict.beta';path=(Join-Path $taskRoot 'Agent\upgrade-paused.exe')}
    [IO.File]::WriteAllText((Join-Path $taskRoot 'native-host.json'),(@{name='com.aict.beta';path=(Join-Path $taskRoot 'Agent\Aict.Beta.Agent.exe')}|ConvertTo-Json))
    $script:backendStarts=0;$script:collectorStarts=0;$script:taskEnables=0
}
function Check-Data {Assert-Test ([IO.File]::ReadAllText((Join-Path $taskRoot 'state\engine.sqlite')) -ceq 'RULES_AND_QUOTA_SENTINEL_NO_RESET') 'Database fixture changed'}
$taskCases=New-Object 'System.Collections.Generic.List[string]'

Initialize-Case '01-null-backup-reproduction'
$taskSource=Join-Path $taskRoot 'source.json';$taskDest=Join-Path $taskRoot 'destination.json'
[IO.File]::WriteAllText($taskSource,'new');[IO.File]::WriteAllText($taskDest,'old')
$taskReproduced=$false;try{[IO.File]::Replace($taskSource,$taskDest,$null)}catch{$taskReproduced=$_.Exception.ToString() -match 'ArgumentException'}
Assert-Test $taskReproduced 'Original PowerShell null-backup failure was not reproduced'
Assert-Test ([IO.File]::ReadAllText($taskDest) -ceq 'old') 'Original failed replacement modified destination'
$taskCases.Add('Original null-backup failure reproduced on Windows PowerShell 5.1')

Initialize-Case '02-json-create-replace-unicode'
$taskJson=Join-Path $taskRoot '配置 空格.json'
Write-UpdateJson $taskJson @{sequence=1};Write-UpdateJson $taskJson @{sequence=2};Write-UpdateJson $taskJson @{sequence=3}
Assert-Test ((Get-Content -Raw -Encoding UTF8 -LiteralPath $taskJson|ConvertFrom-Json).sequence -eq 3) 'Production JSON replacement failed'
Assert-Test (@(Get-ChildItem -LiteralPath $taskRoot -Filter '*.bak').Count -eq 2) 'Replacement backup missing'
Assert-Test (@(Get-ChildItem -LiteralPath $taskRoot -Filter '*.tmp').Count -eq 0) 'Temporary file was not cleaned'
Check-Data;$taskCases.Add('Production JSON create and repeated replacement with Unicode paths')

Initialize-Case '03-json-lock-failure'
$taskJson=Join-Path $taskRoot 'locked.json';Write-UpdateJson $taskJson @{sequence=1}
$taskLock=[IO.File]::Open($taskJson,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
try{$taskFailed=$false;try{Write-UpdateJson $taskJson @{sequence=2}}catch{$taskFailed=$true};Assert-Test $taskFailed 'Locked replacement unexpectedly succeeded'}finally{$taskLock.Dispose()}
Assert-Test ((Get-Content -Raw -Encoding UTF8 -LiteralPath $taskJson|ConvertFrom-Json).sequence -eq 1) 'Locked destination was corrupted'
Assert-Test (@(Get-ChildItem -LiteralPath $taskRoot -Filter '*.tmp').Count -eq 0) 'Failed JSON temp not cleaned'
Check-Data;$taskCases.Add('Locked JSON fails without altering destination or database')

Initialize-Case '04-original-report-no-files-copied'
$taskLock=[IO.File]::Open((Join-Path $taskRoot 'Agent\Aict.Beta.Agent.dll'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
try{Restore-UpdateAfterFailure;Assert-Test ($backendStarts -eq 1 -and $collectorStarts -eq 1) 'Intact old runtime was not restarted';Assert-Test $taskStatus.rollbackBeforeServiceStart 'Unchanged locked DLL incorrectly blocked recovery'}finally{$taskLock.Dispose()}
Check-Data;$taskCases.Add('Reported preparation failure restarts old runtime without touching locked unchanged DLL')

Initialize-Case '05-copy-and-rollback'
foreach($taskRelative in @('Agent/Aict.Beta.Agent.dll','Service/Aict.Beta.Service.dll')){Copy-UpdateProgramFile $taskRelative (Get-FileHash -LiteralPath (Join-Path $taskPackage ('payload/'+$taskRelative)) -Algorithm SHA256).Hash.ToLowerInvariant()}
Assert-Test ($taskModifiedFiles.Count -eq 2) 'Actual changed file tracking failed'
Restore-UpdateAfterFailure
Assert-Test ([IO.File]::ReadAllText((Join-Path $taskRoot 'Agent\Aict.Beta.Agent.dll')) -ceq 'old-agent') 'Agent rollback failed'
Assert-Test ([IO.File]::ReadAllText((Join-Path $taskRoot 'Service\Aict.Beta.Service.dll')) -ceq 'old-service') 'Service rollback failed'
Assert-Test $taskStatus.oldBackendRestored 'Old runtime restart not recorded'
Check-Data;$taskCases.Add('Actual product copy and selective rollback preserve database')

Initialize-Case '06-partial-restoration-locked-binary'
foreach($taskRelative in @('Agent/Aict.Beta.Agent.dll','Service/Aict.Beta.Service.dll')){Copy-UpdateProgramFile $taskRelative (Get-FileHash -LiteralPath (Join-Path $taskPackage ('payload/'+$taskRelative)) -Algorithm SHA256).Hash.ToLowerInvariant()}
$taskLock=[IO.File]::Open((Join-Path $taskRoot 'Agent\Aict.Beta.Agent.dll'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
try{Restore-UpdateAfterFailure;Assert-Test ($backendStarts -eq 0) 'Mixed program files must not be started';Assert-Test (@($taskStatus.rollbackErrors).Count -eq 1) 'File lock recovery error was not isolated';Assert-Test ([IO.File]::ReadAllText((Join-Path $taskRoot 'Service\Aict.Beta.Service.dll')) -ceq 'old-service') 'One locked file prevented restoration of another file'}finally{$taskLock.Dispose()}
$taskStatus.rollbackErrors=@();Restore-UpdateAfterFailure;Assert-Test $taskStatus.oldBackendRestored 'Recovery did not succeed after releasing test lock'
Check-Data;$taskCases.Add('Locked changed DLL does not hide other restores; mixed binaries never start')

Initialize-Case '07-bridge-restore-failure'
$taskJson=Join-Path $taskRoot 'native-host.json';Write-UpdateJson $taskJson $taskHost
$taskLock=[IO.File]::Open($taskJson,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
try{Restore-UpdateAfterFailure;Assert-Test ($backendStarts -eq 1 -and $collectorStarts -eq 1) 'Bridge error prevented restart of intact backend';Assert-Test (-not $taskStatus.rollbackBeforeServiceStart -and @($taskStatus.rollbackErrors).Count -eq 1) 'Incomplete recovery incorrectly reported complete'}finally{$taskLock.Dispose()}
Check-Data;$taskCases.Add('Bridge recovery errors are reported while intact old backend is restarted')

Initialize-Case '08-after-new-start-no-downgrade'
Copy-UpdateProgramFile 'Agent/Aict.Beta.Agent.dll' (Get-FileHash -LiteralPath (Join-Path $taskPackage 'payload\Agent\Aict.Beta.Agent.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
$taskStatus.newServiceStartAttempted=$true;Restore-UpdateAfterFailure
Assert-Test ([IO.File]::ReadAllText((Join-Path $taskRoot 'Agent\Aict.Beta.Agent.dll')) -ceq 'new-agent') 'Unsafe downgrade after new backend start attempt'
Assert-Test ($backendStarts -eq 0) 'Old backend started after new backend attempt'
Check-Data;$taskCases.Add('No automatic program/database downgrade after new backend start attempt')

[IO.File]::WriteAllText((Join-Path $taskEvidence 'result.json'),(@{passed=$true;cases=@($taskCases.ToArray());caseCount=$taskCases.Count;runtime=$PSVersionTable.PSVersion.ToString();scope='PROJECT_FILES_ONLY';realServiceOrTaskOperations=$false}|ConvertTo-Json -Depth 8),(New-Object Text.UTF8Encoding($false)))
Write-Output ($taskCases.Count.ToString()+' updater filesystem/recovery cases passed on Windows PowerShell 5.1. Evidence: '+$taskEvidence)
