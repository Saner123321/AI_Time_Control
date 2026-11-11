#Requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$taskIdentity=[Security.Principal.WindowsIdentity]::GetCurrent()
if(-not(New-Object Security.Principal.WindowsPrincipal($taskIdentity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) -or -not [Environment]::Is64BitProcess -or $PSVersionTable.PSEdition -ne 'Desktop') { throw 'Administrator 64-bit Windows PowerShell 5.1 is required.' }
Import-Module -Name (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1') -Force -ErrorAction Stop
$taskRoot=Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'AICT-Beta-32c8bab7ba164a01b5988c4c7c03554f'
$taskName='AICT-Beta-32c8bab7ba164a01b5988c4c7c03554f'
$taskAgentName='AICT-Beta-Agent-32c8bab7ba164a01b5988c4c7c03554f'
$taskSafetyName='AICT-Beta-TrialSafety-32c8bab7ba164a01b5988c4c7c03554f'
$taskSafetyPaused=$false;$taskSafetyTrigger=$null;$taskSafetyWasEnabled=$false

$taskStatus=[ordered]@{kind='AICT_MASK_V17_UPDATE';updaterVersion='17.0';atUtc=[DateTimeOffset]::UtcNow.ToString('o');root=$taskRoot;phase='checking';failedPhase=$null;completed=$false;continuousUse=$true;obsoleteSafetyRemoved=$false;desktopStartup=$null;rulesReset=$false;databaseReplaced=$false;newServiceStartAttempted=$false;newServiceStarted=$false;rollbackBeforeServiceStart=$false;oldBackendRestored=$false;collectorRestartRequested=$false;browserBridgeRestored=$false;rollbackErrors=@();modifiedFiles=@();error=$null}
$taskBackup=$null;$taskAgentWasEnabled=$false;$taskStopped=$false;$taskHost=$null;$taskInstalled=$null;$taskMetadata=$null
$taskPackage=$PSScriptRoot;$taskModifiedFiles=New-Object 'System.Collections.Generic.List[string]'
function Test-UpdateViewerReady($Viewing) {
    if($null -eq $Viewing){return $false}
    return ($Viewing.connected -and $Viewing.processProtected -and ($Viewing.applied -or ($Viewing.observation -ceq 'Unavailable' -and $Viewing.visiblePages -eq 0 -and $Viewing.requiredMasks -eq 0 -and $Viewing.renderedMasks -eq 0 -and $Viewing.fault -like '内置网页暂未识别：*')))
}
function Save-UpdateStatus { [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'update-result.json'),($taskStatus|ConvertTo-Json -Depth 12),(New-Object Text.UTF8Encoding($false))) }
function Set-UpdateOwner([string]$Path) { $taskAcl=Get-Acl -LiteralPath $Path;$taskAcl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')));Set-Acl -LiteralPath $Path -AclObject $taskAcl }
function Assert-UpdateProtected([string]$Path) {
    $taskInfo=Get-Item -LiteralPath $Path
    if($taskInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse privileged input refused.' }
    $taskAcl=Get-Acl -LiteralPath $Path
    if($taskAcl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin @('S-1-5-18','S-1-5-32-544')) { throw 'Privileged ownership mismatch.' }
    $taskWrite=[Security.AccessControl.FileSystemRights]::Write -bor [Security.AccessControl.FileSystemRights]::Delete -bor [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles
    foreach($taskRule in $taskAcl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) { if($taskRule.AccessControlType -eq 'Allow' -and $taskRule.IdentityReference.Value -notin @('S-1-5-18','S-1-5-32-544') -and ($taskRule.FileSystemRights -band $taskWrite)) { throw 'Ordinary-user-writable privileged input refused.' } }
}
function Ensure-UpdateParent([string]$Name) {
    $taskCurrent=$taskRoot
    foreach($taskPart in (($Name -split '/')[0..(($Name -split '/').Length-2)])) {
        $taskCurrent=Join-Path $taskCurrent $taskPart
        if(-not(Test-Path -LiteralPath $taskCurrent)){ $null=New-Item -ItemType Directory -Path $taskCurrent;Set-UpdateOwner $taskCurrent }
        Assert-UpdateProtected $taskCurrent
    }
}
function Write-UpdateJson([string]$Path,$Value) {
    $taskTemp=$Path+'.update-'+[guid]::NewGuid().ToString('N')+'.tmp'
    try {
        [IO.File]::WriteAllText($taskTemp,($Value|ConvertTo-Json -Depth 30),(New-Object Text.UTF8Encoding($false)));Set-UpdateOwner $taskTemp
        if(Test-Path -LiteralPath $Path){$taskPrevious=$Path+'.previous-'+[guid]::NewGuid().ToString('N')+'.bak';[IO.File]::Replace($taskTemp,$Path,$taskPrevious,$true)}else{[IO.File]::Move($taskTemp,$Path)}
    }finally { if(Test-Path -LiteralPath $taskTemp){Remove-Item -LiteralPath $taskTemp -Force} }
}
function Set-UpdatePhase([string]$Phase){$taskStatus.phase=$Phase;Save-UpdateStatus}
function Copy-UpdateProgramFile([string]$Name,[string]$ExpectedHash){
    if($Name.Contains('/')){Ensure-UpdateParent $Name}
    if(-not $taskModifiedFiles.Contains($Name)){$taskModifiedFiles.Add($Name)}
    $taskStatus.modifiedFiles=@($taskModifiedFiles.ToArray())
    $taskDest=Join-Path $taskRoot $Name
    Copy-Item -LiteralPath (Join-Path $taskPackage ('payload/'+$Name)) -Destination $taskDest -Force
    Set-UpdateOwner $taskDest;Assert-UpdateProtected $taskDest
    if((Get-FileHash -LiteralPath $taskDest -Algorithm SHA256).Hash.ToLowerInvariant() -cne $ExpectedHash){throw ('Installed update readback mismatch: '+$Name)}
}
function Restore-UpdateAfterFailure {
    if(-not $taskStopped -or $taskStatus.newServiceStartAttempted -or $null -eq $taskBackup){return}
    $taskProgramsSafe=$true
    foreach($taskRelative in @($taskModifiedFiles.ToArray())){
        $taskPrevious=Join-Path $taskBackup $taskRelative;$taskDest=Join-Path $taskRoot $taskRelative
        try {
            if(Test-Path -LiteralPath $taskPrevious){
                $taskExpected=(Get-FileHash -LiteralPath $taskPrevious -Algorithm SHA256).Hash
                if(-not(Test-Path -LiteralPath $taskDest) -or (Get-FileHash -LiteralPath $taskDest -Algorithm SHA256).Hash -cne $taskExpected){Copy-Item -LiteralPath $taskPrevious -Destination $taskDest -Force;Set-UpdateOwner $taskDest}
                if((Get-FileHash -LiteralPath $taskDest -Algorithm SHA256).Hash -cne $taskExpected){throw 'Rollback readback mismatch'}
            }elseif(Test-Path -LiteralPath $taskDest){
                # This file did not exist in the old manifest. Only remove the exact new product file.
                $taskExpected=$taskMetadata.files.PSObject.Properties[$taskRelative].Value
                if((Get-FileHash -LiteralPath $taskDest -Algorithm SHA256).Hash.ToLowerInvariant() -cne $taskExpected){throw 'New file changed after copy; left untouched'}
                Remove-Item -LiteralPath $taskDest -Force
            }
        }catch{$taskProgramsSafe=$false;$taskStatus.rollbackErrors+=('Restore '+$taskRelative+': '+$_.Exception.Message)}
    }
    if($null -ne $taskHost){try{
        $taskHostPath=Join-Path $taskRoot 'native-host.json';$taskCurrentHost=Get-Content -Raw -Encoding UTF8 -LiteralPath $taskHostPath|ConvertFrom-Json
        $taskHost.path=Join-Path $taskRoot 'Agent\Aict.Beta.Agent.exe'
        if($taskCurrentHost.path -ine $taskHost.path){Write-UpdateJson $taskHostPath $taskHost}
        $taskStatus.browserBridgeRestored=$true
    }catch{$taskStatus.rollbackErrors+=('Browser bridge: '+$_.Exception.Message)}}
    if($taskAgentWasEnabled){try{$null=Enable-ScheduledTask -TaskName $taskAgentName}catch{$taskStatus.rollbackErrors+=('Enable collector: '+$_.Exception.Message)}}
    # A bridge/task recovery error must not hide the chance to restart an intact old backend.
    # Never start a mixed set of binaries when program restoration itself failed.
    if($taskProgramsSafe){try{Start-Service -Name $taskName;$taskController=Get-Service -Name $taskName;$taskController.WaitForStatus('Running',[TimeSpan]::FromSeconds(30));$taskStatus.oldBackendRestored=$true}catch{$taskStatus.rollbackErrors+=('Start old backend: '+$_.Exception.Message)}}
    if($taskStatus.oldBackendRestored -and $taskAgentWasEnabled){try{Start-ScheduledTask -TaskName $taskAgentName;$taskStatus.collectorRestartRequested=$true}catch{$taskStatus.rollbackErrors+=('Start collector: '+$_.Exception.Message)}}
    $taskStatus.rollbackBeforeServiceStart=$taskProgramsSafe -and $taskStatus.oldBackendRestored -and @($taskStatus.rollbackErrors).Count -eq 0
}
function Assert-UpdateTrialSafety($Task) {
    if(@($Task.Actions).Count -ne 1 -or $Task.Actions[0].Execute -ine (Join-Path $taskRoot 'Service\Aict.Beta.Service.exe') -or $Task.Actions[0].Arguments -cne '--host-trial-safety' -or $Task.Principal.UserId -notin @('SYSTEM','S-1-5-18') -or $Task.State -eq 'Running'){throw 'Independent safety task ownership/state mismatch.'}
}
function Pause-UpdateTrialSafety {
    $taskSafety=Get-ScheduledTask -TaskName $taskSafetyName -ErrorAction SilentlyContinue
    if($null -eq $taskSafety){return}
    Assert-UpdateTrialSafety $taskSafety
    $script:taskSafetyTrigger=$taskSafety.Triggers;$script:taskSafetyWasEnabled=$taskSafety.State -ne 'Disabled'
    $null=Disable-ScheduledTask -TaskName $taskSafetyName;$script:taskSafetyPaused=$true
    $taskReadback=Get-ScheduledTask -TaskName $taskSafetyName
    Assert-UpdateTrialSafety $taskReadback
    if($taskReadback.State -ne 'Disabled'){throw 'Old safety task did not remain disabled; backend left unchanged.'}
}
function Set-UpdateContinuousMode {
    Copy-Item -LiteralPath (Join-Path $taskRoot 'config.json') -Destination (Join-Path $taskBackup 'config.json')
    $taskModifiedFiles.Add('config.json')
    $taskConfig | Add-Member -NotePropertyName continuousUse -NotePropertyValue $true -Force
    $taskConfig.hostTrialEndsUtc=$null
    Write-UpdateJson (Join-Path $taskRoot 'config.json') $taskConfig
    $taskActual=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskRoot 'config.json')|ConvertFrom-Json
    if(-not $taskActual.hostTrial -or -not $taskActual.continuousUse -or $null -ne $taskActual.hostTrialEndsUtc){throw 'Continuous mode readback mismatch.'}
}
function Complete-UpdateTrialSafety {
    if(-not $taskSafetyPaused){return}
    $taskSafety=Get-ScheduledTask -TaskName $taskSafetyName -ErrorAction SilentlyContinue
    if($null -ne $taskSafety){Assert-UpdateTrialSafety $taskSafety;Unregister-ScheduledTask -TaskName $taskSafetyName -Confirm:$false}
    $script:taskSafetyPaused=$false;$taskStatus.obsoleteSafetyRemoved=$true
}
function Restore-UpdateTrialSafety {
    if(-not $taskSafetyPaused -or $taskStatus.newServiceStartAttempted){return}
    # Re-arm only after rollback restored the original finite trial configuration.
    $taskRestored=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskRoot 'config.json')|ConvertFrom-Json
    if(($taskRestored.PSObject.Properties['continuousUse'] -and $taskRestored.continuousUse) -or $null -eq $taskRestored.hostTrialEndsUtc){return}
    $taskSafety=Get-ScheduledTask -TaskName $taskSafetyName -ErrorAction SilentlyContinue
    if($null -eq $taskSafety){throw 'Original safety task missing during rollback.'};Assert-UpdateTrialSafety $taskSafety
    if($null -ne $taskSafetyTrigger){$null=Set-ScheduledTask -TaskName $taskSafetyName -Trigger $taskSafetyTrigger}
    if($taskSafetyWasEnabled){$null=Enable-ScheduledTask -TaskName $taskSafetyName}
    $script:taskSafetyPaused=$false
}
function New-UpdateShortcut {
    $taskShell=New-Object -ComObject WScript.Shell
    try {
        $taskShortcutPath=Join-Path ([Environment]::GetFolderPath('Programs')) '专注计划.lnk'
        $taskShortcut=$taskShell.CreateShortcut($taskShortcutPath);$taskTarget=Join-Path $taskRoot 'App\Aict.Beta.Desktop.exe'
        if((Test-Path -LiteralPath $taskShortcutPath) -and $taskShortcut.TargetPath -ine $taskTarget){
            $taskShortcutPath=Join-Path ([Environment]::GetFolderPath('Programs')) '专注计划（持续使用）.lnk';$taskShortcut=$taskShell.CreateShortcut($taskShortcutPath)
            if((Test-Path -LiteralPath $taskShortcutPath) -and $taskShortcut.TargetPath -ine $taskTarget){throw 'Unrelated Start menu shortcuts left unchanged.'}
        }
        $taskShortcut.TargetPath=$taskTarget;$taskShortcut.WorkingDirectory=$taskRoot;$taskShortcut.Description='专注计划：持续执行日程、额度和锁定规则';$taskShortcut.Save()
    }finally{[Runtime.InteropServices.Marshal]::FinalReleaseComObject($taskShell)|Out-Null}
}
try {
    Save-UpdateStatus
    Assert-UpdateProtected $taskRoot
    foreach($taskPart in @('config.json','MANIFEST.json','Service','Agent','App','state','state\engine.sqlite')) { Assert-UpdateProtected (Join-Path $taskRoot $taskPart) }
    $taskConfig=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskRoot 'config.json')|ConvertFrom-Json
    if(-not $taskConfig.hostTrial){throw 'This update requires the authorized host installation.'};if($taskConfig.runId -cne '32c8bab7ba164a01b5988c4c7c03554f' -or $taskConfig.probeSid -cne $taskIdentity.User.Value) { throw 'This update only supports the installation/account in the supplied report.' }
    $taskMetadataPath=Join-Path $PSScriptRoot 'UPDATE.json'
    if((Get-FileHash -LiteralPath $taskMetadataPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne '0000000000000000000000000000000000000000000000000000000000000000') { throw 'Pinned update metadata mismatch.' }
    $taskMetadata=Get-Content -Raw -Encoding UTF8 -LiteralPath $taskMetadataPath|ConvertFrom-Json
    $taskManifestPath=Join-Path $taskRoot 'MANIFEST.json';$taskCurrentManifest=(Get-FileHash -LiteralPath $taskManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if($taskCurrentManifest -notin @($taskMetadata.acceptedBaseManifestSha256 + @($taskMetadata.targetManifestSha256))) { throw 'Installed version does not match this update.' }
    $taskInstalled=Get-Content -Raw -Encoding UTF8 -LiteralPath $taskManifestPath|ConvertFrom-Json
    foreach($taskFile in $taskInstalled.files.PSObject.Properties) { if($taskFile.Name -match '(^[\\/]|\.\.|:)'){throw 'Invalid installed path'};$taskPath=Join-Path $taskRoot $taskFile.Name;Assert-UpdateProtected $taskPath;if((Get-FileHash -LiteralPath $taskPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $taskFile.Value){throw ('Installed file changed: '+$taskFile.Name)} }
    foreach($taskFile in $taskMetadata.files.PSObject.Properties) { if($taskFile.Name -notmatch '^(Service/|Agent/|Viewer/|App/|Extension/|Targets/|scripts/Install-Beta\.ps1$|Sources/|BetaBridge\.exe$)' -or $taskFile.Name -match '(\.\.|:)'){throw 'Update destination outside immutable product files'};if((Get-FileHash -LiteralPath (Join-Path $PSScriptRoot ('payload/'+$taskFile.Name)) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $taskFile.Value){throw 'Update payload hash mismatch'} }
    foreach($taskPair in @(@('TARGET-MANIFEST.json',$taskMetadata.targetManifestSha256),@('TARGET-SHA256SUMS.txt',$taskMetadata.targetSumsSha256))) { if((Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $taskPair[0]) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $taskPair[1]){throw 'Target metadata hash mismatch'} }
    foreach($taskProcess in @(Get-CimInstance Win32_Process -Filter "Name='Aict.Beta.Desktop.exe'")) { if($taskProcess.ExecutablePath -ieq (Join-Path $taskRoot 'App\Aict.Beta.Desktop.exe')) { throw '请先退出专注计划界面。有托盘图标时右键选择“退出界面”；旧版关闭窗口后再重试。' } }
    $taskService=Get-CimInstance Win32_Service -Filter ("Name='"+$taskName+"'")
    if($null -eq $taskService -or $taskService.PathName -ine ('"'+(Join-Path $taskRoot 'Service\Aict.Beta.Service.exe')+'" --vm-beta')) { throw 'Existing service ownership mismatch.' }
    $taskAgent=Get-ScheduledTask -TaskName $taskAgentName
    if(@($taskAgent.Actions).Count -ne 1 -or $taskAgent.Actions[0].Execute -ine (Join-Path $taskRoot 'Agent\Aict.Beta.Agent.exe') -or $taskAgent.Actions[0].Arguments -cne '--session-agent') { throw 'Owned collector task changed.' }
    $taskAgentWasEnabled=$taskAgent.State -ne 'Disabled'
    $taskUpdates=Join-Path $taskRoot 'state\updates';if(-not(Test-Path -LiteralPath $taskUpdates)){$null=New-Item -ItemType Directory -Path $taskUpdates;Set-UpdateOwner $taskUpdates};Assert-UpdateProtected $taskUpdates
    $taskBackup=Join-Path $taskUpdates ('mask-v17-'+[DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'));$null=New-Item -ItemType Directory -Path $taskBackup;Set-UpdateOwner $taskBackup
    foreach($taskFile in $taskMetadata.files.PSObject.Properties) { $taskSource=Join-Path $taskRoot $taskFile.Name;if(Test-Path -LiteralPath $taskSource){$taskDest=Join-Path $taskBackup $taskFile.Name;$null=New-Item -ItemType Directory -Path (Split-Path -Parent $taskDest) -Force;Copy-Item -LiteralPath $taskSource -Destination $taskDest} }
    foreach($taskNamePart in @('MANIFEST.json','SHA256SUMS.txt')) { Copy-Item -LiteralPath (Join-Path $taskRoot $taskNamePart) -Destination (Join-Path $taskBackup $taskNamePart) }
    foreach($taskEntry in @(Get-ChildItem -LiteralPath $taskBackup -Recurse)){Set-UpdateOwner $taskEntry.FullName;Assert-UpdateProtected $taskEntry.FullName}
    Set-UpdatePhase 'preflight-file-operations'
    $taskPreflight=Join-Path $taskBackup 'json-preflight.json'
    Write-UpdateJson $taskPreflight @{sequence=1};Write-UpdateJson $taskPreflight @{sequence=2}
    if((Get-Content -Raw -Encoding UTF8 -LiteralPath $taskPreflight|ConvertFrom-Json).sequence -ne 2){throw 'Atomic JSON preflight failed; backend left running.'}
    Pause-UpdateTrialSafety
    Set-UpdatePhase 'stopping-backend'
    $taskStopped=$true;Stop-Service -Name $taskName -ErrorAction Stop
    $taskController=Get-Service -Name $taskName;$taskController.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(60))
    if($taskService.ProcessId -gt 0){$taskOldProcess=Get-Process -Id $taskService.ProcessId -ErrorAction SilentlyContinue;if($null -ne $taskOldProcess -and -not $taskOldProcess.HasExited){if(-not $taskOldProcess.WaitForExit(15000)){throw 'Old backend process has not exited; no program files replaced.'}}}
    $taskHostPath=Join-Path $taskRoot 'native-host.json';Assert-UpdateProtected $taskHostPath
    $taskHost=Get-Content -Raw -Encoding UTF8 -LiteralPath $taskHostPath|ConvertFrom-Json
    if($taskHost.name -cne 'com.aict.beta' -or $taskHost.path -notin @((Join-Path $taskRoot 'Agent\Aict.Beta.Agent.exe'),(Join-Path $taskRoot 'Agent\upgrade-paused.exe'))) { throw 'Owned browser bridge changed.' }
    Set-UpdatePhase 'pausing-collectors'
    $taskHost.path=Join-Path $taskRoot 'Agent\upgrade-paused.exe';Write-UpdateJson $taskHostPath $taskHost
    $null=Disable-ScheduledTask -TaskName $taskAgentName;Stop-ScheduledTask -TaskName $taskAgentName
    foreach($taskChild in @(Get-Process -Name 'Aict.Beta.Agent' -ErrorAction SilentlyContinue)) {
        try { if(-not $taskChild.HasExited -and $taskChild.MainModule.FileName -ieq (Join-Path $taskRoot 'Agent\Aict.Beta.Agent.exe')) { $taskChild.Kill();if(-not $taskChild.WaitForExit(10000)){throw 'Owned collector has not exited.'} } }
        catch [InvalidOperationException] {}
        catch [ComponentModel.Win32Exception] { if(-not $taskChild.HasExited){throw} }
    }
    foreach($taskViewer in @(Get-Process -Name 'Aict.Beta.Viewer' -ErrorAction SilentlyContinue)) {
        try{if(-not $taskViewer.HasExited -and $taskViewer.MainModule.FileName -ieq (Join-Path $taskRoot 'Viewer\Aict.Beta.Viewer.exe')){$taskViewer.Kill();if(-not $taskViewer.WaitForExit(10000)){throw 'Owned viewer has not exited.'}}}catch [InvalidOperationException] {}
    }
    Set-UpdateContinuousMode
    Set-UpdatePhase 'copying-files'
    foreach($taskFile in $taskMetadata.files.PSObject.Properties) { Copy-UpdateProgramFile $taskFile.Name $taskFile.Value }
    foreach($taskNamePart in @('MANIFEST.json','SHA256SUMS.txt')){$taskModifiedFiles.Add($taskNamePart)}
    $taskStatus.modifiedFiles=@($taskModifiedFiles.ToArray())
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'TARGET-MANIFEST.json') -Destination $taskManifestPath -Force;Set-UpdateOwner $taskManifestPath
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'TARGET-SHA256SUMS.txt') -Destination (Join-Path $taskRoot 'SHA256SUMS.txt') -Force;Set-UpdateOwner (Join-Path $taskRoot 'SHA256SUMS.txt')
    if((Get-FileHash -LiteralPath $taskManifestPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $taskMetadata.targetManifestSha256 -or (Get-FileHash -LiteralPath (Join-Path $taskRoot 'SHA256SUMS.txt') -Algorithm SHA256).Hash.ToLowerInvariant() -cne $taskMetadata.targetSumsSha256){throw 'Installed target metadata readback mismatch'}
    $taskHost.path=Join-Path $taskRoot 'Agent\Aict.Beta.Agent.exe';Write-UpdateJson $taskHostPath $taskHost
    if($taskAgentWasEnabled){$null=Enable-ScheduledTask -TaskName $taskAgentName}
    Set-UpdatePhase 'starting-backend'
    $taskStartedUtc=[DateTime]::UtcNow;$taskStatus.newServiceStartAttempted=$true;Start-Service -Name $taskName;$taskStatus.newServiceStarted=$true;Start-ScheduledTask -TaskName $taskAgentName
    Set-UpdatePhase 'checking-health'
    $taskWatch=[Diagnostics.Stopwatch]::StartNew();$taskHealthy=$false
    while($taskWatch.Elapsed.TotalSeconds -lt 100){try{$taskStatusPath=Join-Path $taskRoot 'state\status.json';$taskObserved=Get-Content -Raw -Encoding UTF8 -LiteralPath $taskStatusPath|ConvertFrom-Json;if($taskObserved.status.revision -ceq 'mask-v17' -and $taskObserved.status.mode -ceq 'HOST_CONTINUOUS' -and $null -eq $taskObserved.status.trialEndsUtc -and -not $taskObserved.status.trialExpired -and (Get-Item -LiteralPath $taskStatusPath).LastWriteTimeUtc -ge $taskStartedUtc -and $taskObserved.status.storageHealthy -and $taskObserved.status.telemetryConnected -and $taskObserved.status.execution.confirmed -and $taskObserved.status.clockConsistent -and (Test-UpdateViewerReady $taskObserved.status.websites.manualViewing)){$taskHealthy=$true;break}}catch{};Start-Sleep -Milliseconds 500}
    if(-not $taskHealthy){throw 'Updated backend did not confirm healthy status; data and new files retained.'}
    Complete-UpdateTrialSafety
    Set-UpdatePhase 'configuring-login-startup'
    $taskStartupOutput=& (Join-Path $taskRoot 'Service\Aict.Beta.Service.exe') --configure-desktop-startup
    if($LASTEXITCODE -ne 0){throw 'Login startup configuration failed; existing rules retained.'}
    $taskStatus.desktopStartup=($taskStartupOutput|ConvertFrom-Json)
    if(-not $taskStatus.desktopStartup.configured){throw 'Login startup did not confirm configuration.'}
    New-UpdateShortcut
    $taskStatus.completed=$true;$taskStatus.phase='completed'
}catch {
    $taskStatus.error=$_.Exception.ToString();$taskStatus.failedPhase=$taskStatus.phase;$taskStatus.phase='failed'
    Restore-UpdateAfterFailure
    try{Restore-UpdateTrialSafety}catch{$taskStatus.rollbackErrors+=('Restore safety task: '+$_.Exception.Message)}
    foreach($taskPair in @(@('state\status.json','service-status-after-update.json'),@('state\service-fault.json','service-fault-after-update.json'))){try{$taskPath=Join-Path $taskRoot $taskPair[0];if(Test-Path -LiteralPath $taskPath){Copy-Item -LiteralPath $taskPath -Destination (Join-Path $PSScriptRoot $taskPair[1])}}catch{}}
}finally { Save-UpdateStatus }
if(-not $taskStatus.completed){throw 'Update did not complete; preserve update-result.json. No database reset was performed.'}
