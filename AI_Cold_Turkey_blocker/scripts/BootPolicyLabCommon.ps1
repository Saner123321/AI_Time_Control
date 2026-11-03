#Requires -Version 5.1
# Pure definitions; public entry scripts run Assert-AictPolicyGuest first.
. (Join-Path $PSScriptRoot 'ServicePolicyLabCommon.ps1')
function Read-AictBootContext {
    param([string]$Package,$Manifest)
    $taskLocator=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $Package 'active-boot-policy.json') | ConvertFrom-Json
    $taskContext=New-AictPolicyContext -Package $Package -RunId $taskLocator.runId
    if((Get-Item -LiteralPath $taskContext.Root).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse experiment root refused.' }
    if((Get-FileHash -LiteralPath (Join-Path $taskContext.Root 'AictPolicyBridge.exe') -Algorithm SHA256).Hash.ToLowerInvariant() -cne $Manifest.files.'lab/AictPolicyBridge.exe') { throw 'Staged native bridge changed. Preserve evidence; use the verified VM recovery baseline.' }
    return $taskContext
}
function Initialize-AictBootStage {
    param($Context,$Manifest)
    $taskSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $taskOwner=New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544');$taskUser=New-Object Security.Principal.SecurityIdentifier($taskSid)
    $taskAcl=New-Object Security.AccessControl.DirectorySecurity;$taskAcl.SetAccessRuleProtection($true,$false);$taskAcl.SetOwner($taskOwner)
    foreach($taskPrivileged in @('S-1-5-18','S-1-5-32-544')) { $taskAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($taskPrivileged)),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))) }
    $taskAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($taskUser,'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow')))
    $null=[IO.Directory]::CreateDirectory($Context.Root,$taskAcl)
    foreach($taskSub in @('Service','state','Targets','probe')) { $null=New-Item -ItemType Directory -Path (Join-Path $Context.Root $taskSub);Set-AictPolicyOwner -Path (Join-Path $Context.Root $taskSub) }
    $taskProbe=Join-Path $Context.Root 'probe';$taskProbeAcl=Get-Acl -LiteralPath $taskProbe;$taskProbeAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($taskUser,'Modify','ContainerInherit,ObjectInherit','None','Allow')));Set-Acl -LiteralPath $taskProbe -AclObject $taskProbeAcl
    Copy-Item -LiteralPath (Join-Path $Context.Package 'lab\AictPolicyBridge.exe') -Destination (Join-Path $Context.Root 'AictPolicyBridge.exe')
    foreach($taskFile in @(Get-ChildItem -LiteralPath (Join-Path $Context.Package 'Service') -File -Recurse)) {
        $taskRelative=$taskFile.FullName.Substring((Join-Path $Context.Package 'Service').Length).TrimStart('\');$taskDestination=Join-Path (Join-Path $Context.Root 'Service') $taskRelative
        $null=New-Item -ItemType Directory -Path (Split-Path -Parent $taskDestination) -Force
        Copy-Item -LiteralPath $taskFile.FullName -Destination $taskDestination;Set-AictPolicyOwner -Path $taskDestination
    }
    foreach($taskRelative in @('Targets\Target.exe','Targets\Renamed.exe','MovedTarget.exe','Targets\AllowedControl.exe')) {
        $taskSource=if($taskRelative.EndsWith('AllowedControl.exe')) { 'lab\AictPolicyControl.exe' } else { 'lab\AictPolicyTarget.exe' }
        Copy-Item -LiteralPath (Join-Path $Context.Package $taskSource) -Destination (Join-Path $Context.Root $taskRelative);Set-AictPolicyOwner -Path (Join-Path $Context.Root $taskRelative)
    }
    Write-AictPolicyJson -Path (Join-Path $Context.Root 'config.json') -Value @{runId=$Context.RunId;probeSid=$taskSid;targetSipHash=$Manifest.target_sip_hash;targetSize=$Manifest.target_size}
    [IO.File]::WriteAllText((Join-Path $Context.Root 'protection-canary.bin'),'AICT protected permissions canary')
    foreach($taskFile in @('AictPolicyBridge.exe','protection-canary.bin')) { Set-AictPolicyOwner -Path (Join-Path $Context.Root $taskFile) }
    foreach($taskSub in @('Service','Targets')) { foreach($taskDirectory in @(Get-ChildItem -LiteralPath (Join-Path $Context.Root $taskSub) -Directory -Recurse)) { Set-AictPolicyOwner -Path $taskDirectory.FullName } }
    foreach($taskEntry in $Manifest.files.PSObject.Properties) {
        if($taskEntry.Name.StartsWith('Service/')) { $taskStaged=Join-Path $Context.Root $taskEntry.Name }
        elseif($taskEntry.Name -ceq 'lab/AictPolicyBridge.exe') { $taskStaged=Join-Path $Context.Root 'AictPolicyBridge.exe' }
        else { continue }
        if((Get-FileHash -LiteralPath $taskStaged -Algorithm SHA256).Hash.ToLowerInvariant() -cne $taskEntry.Value) { throw 'Staged runtime/bridge mismatch before installation.' }
    }
    foreach($taskPath in @($Context.Root,"$env:SystemRoot\System32\cmd.exe")) { if($taskPath -match '[%&!^|<>"\r\n]') { throw 'Unexpected shell metacharacter in fixed paths.' } }
    Write-AictPolicyJson -Path (Join-Path $Context.Package 'active-boot-policy.json') -Value @{runId=$Context.RunId}
}
function Register-AictBootCleanup {
    param($Context)
    $taskBatch=Join-Path $Context.Root 'Run-cleanup.cmd'
    $taskLines=@('@echo off',('"'+(Join-Path $Context.Root 'AictPolicyBridge.exe')+'" --cleanup 1>"'+(Join-Path $Context.Root 'watchdog-stdout.txt')+'" 2>"'+(Join-Path $Context.Root 'watchdog-stderr.txt')+'"'),'set "AICT_CLEANUP_EXIT=%ERRORLEVEL%"','exit /b %AICT_CLEANUP_EXIT%')
    [IO.File]::WriteAllLines($taskBatch,[string[]]$taskLines,[Text.Encoding]::ASCII);Set-AictPolicyOwner -Path $taskBatch
    $taskAction=New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\cmd.exe" -Argument ('/d /c "'+$taskBatch+'"')
    $taskOnce=New-ScheduledTaskTrigger -Once -At ((Get-Date).AddMinutes(30));$taskBoot=New-ScheduledTaskTrigger -AtStartup;$taskBoot.Delay='PT10M'
    $taskPrincipal=New-ScheduledTaskPrincipal -UserId 'S-1-5-18' -LogonType ServiceAccount -RunLevel Highest
    $taskSettings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 3) -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    $null=Register-ScheduledTask -TaskName $Context.TaskName -Action $taskAction -Trigger @($taskOnce,$taskBoot) -Principal $taskPrincipal -Settings $taskSettings -ErrorAction Stop
}
function Get-AictBootWindowName {
    param($Context)
    return 'AICT-BootPolicy-Window-'+$Context.RunId
}
function Assert-AictBootWindowDefinition {
    param($Context)
    $taskName=Get-AictBootWindowName -Context $Context
    $taskXml=[xml](Export-ScheduledTask -TaskName $taskName -ErrorAction Stop)
    $taskNs=New-Object Xml.XmlNamespaceManager($taskXml.NameTable);$taskNs.AddNamespace('t','http://schemas.microsoft.com/windows/2004/02/mit/task')
    $taskConfig=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $Context.Root 'config.json') | ConvertFrom-Json
    $taskCommand=$taskXml.SelectSingleNode('/t:Task/t:Actions/t:Exec/t:Command',$taskNs)
    $taskArgs=$taskXml.SelectSingleNode('/t:Task/t:Actions/t:Exec/t:Arguments',$taskNs)
    $taskPrincipal=$taskXml.SelectSingleNode('/t:Task/t:Principals/t:Principal',$taskNs)
    $taskTriggers=$taskXml.SelectNodes('/t:Task/t:Triggers/*',$taskNs)
    $taskExpectedArgs='/d /c "'+(Join-Path $Context.Root 'Run-probe-boot-window.cmd')+'"'
    if($taskXml.SelectNodes('/t:Task/t:Actions/*',$taskNs).Count -ne 1 -or $null -eq $taskCommand -or $null -eq $taskArgs -or $taskCommand.InnerText -ine "$env:SystemRoot\System32\cmd.exe" -or $taskArgs.InnerText -cne $taskExpectedArgs -or $null -eq $taskPrincipal -or $taskPrincipal.UserId -cne $taskConfig.probeSid -or $taskPrincipal.LogonType -cne 'InteractiveToken' -or $taskPrincipal.RunLevel -cne 'LeastPrivilege' -or $taskTriggers.Count -ne 1 -or $taskTriggers[0].LocalName -cne 'LogonTrigger' -or $taskTriggers[0].UserId -cne $taskConfig.probeSid) { throw 'Logon window task definition changed; left untouched.' }
    return $true
}
function Register-AictBootWindow {
    param($Context)
    $taskName=Get-AictBootWindowName -Context $Context;$taskBatch=Join-Path $Context.Root 'Run-probe-boot-window.cmd';$taskProbe=Join-Path $Context.Root 'probe'
    $taskLines=@('@echo off',('if exist "'+(Join-Path $taskProbe 'boot-window-exit.txt')+'" exit /b 0'),('"'+(Join-Path $Context.Root 'AictPolicyBridge.exe')+'" --probe boot-window 1>"'+(Join-Path $taskProbe 'boot-window-stdout.txt')+'" 2>"'+(Join-Path $taskProbe 'boot-window-stderr.txt')+'"'),'set "AICT_WINDOW_EXIT=%ERRORLEVEL%"',('>"'+(Join-Path $taskProbe 'boot-window-exit.txt')+'" echo %AICT_WINDOW_EXIT%'),'exit /b %AICT_WINDOW_EXIT%')
    [IO.File]::WriteAllLines($taskBatch,[string[]]$taskLines,[Text.Encoding]::ASCII);Set-AictPolicyOwner -Path $taskBatch
    $taskSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $taskPrincipal=New-ScheduledTaskPrincipal -UserId $taskSid -LogonType Interactive -RunLevel Limited
    $taskAction=New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\cmd.exe" -Argument ('/d /c "'+$taskBatch+'"')
    $taskTrigger=New-ScheduledTaskTrigger -AtLogOn -User $taskSid
    $taskSettings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 1) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    $null=Register-ScheduledTask -TaskName $taskName -Action $taskAction -Principal $taskPrincipal -Trigger $taskTrigger -Settings $taskSettings -ErrorAction Stop
    $null=Assert-AictBootWindowDefinition -Context $Context
    # Do not manually start this task: only the subsequent real logon should produce window evidence.
    Write-AictPolicyJson -Path (Join-Path $Context.Root 'boot-window-task.json') -Value @{runId=$Context.RunId;kind='LOGON_LIMITED_FIXED';definitionMatched=$true;manuallyStarted=$false}
}
function Remove-AictBootWindow {
    param($Context)
    $taskName=Get-AictBootWindowName -Context $Context;$task=Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if($null -eq $task) { return $true }
    $null=Assert-AictBootWindowDefinition -Context $Context
    if($task.State -eq 'Running') { Stop-ScheduledTask -TaskName $taskName -ErrorAction Stop }
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction Stop
    return $null -eq (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue)
}
function Read-AictBootWindow {
    param($Context)
    $null=Assert-AictBootWindowDefinition -Context $Context
    $taskProbe=Join-Path $Context.Root 'probe';$taskExit=Join-Path $taskProbe 'boot-window-exit.txt';$taskWait=[Diagnostics.Stopwatch]::StartNew()
    while(( -not(Test-Path -LiteralPath $taskExit) -or (Get-ScheduledTask -TaskName (Get-AictBootWindowName -Context $Context)).State -eq 'Running') -and $taskWait.Elapsed.TotalSeconds -lt 60) { Start-Sleep -Milliseconds 100 }
    if(-not(Test-Path -LiteralPath $taskExit)) { throw 'Automatic logon probe did not finish. It is not replaced with a later manual probe.' }
    Assert-AictPolicyProbeFile -Path $taskExit
    if([int](Get-Content -Raw -LiteralPath $taskExit) -ne 0) { throw 'Automatic logon probe failed; preserve its startup logs.' }
    $taskPath=Join-Path $taskProbe 'boot-window.json';Assert-AictPolicyProbeFile -Path $taskPath
    $taskValue=Get-Content -Raw -Encoding UTF8 -LiteralPath $taskPath | ConvertFrom-Json
    if($taskValue.runId -cne $Context.RunId -or $taskValue.phase -cne 'boot-window' -or -not $taskValue.limitedToken -or -not $taskValue.passed) { throw 'Automatic logon observations did not pass.' }
    $taskInfo=Get-ScheduledTaskInfo -TaskName (Get-AictBootWindowName -Context $Context)
    if($taskInfo.LastTaskResult -ne 0 -or $taskInfo.LastRunTime.Year -lt 2000) { throw 'Task Scheduler did not confirm a completed successful logon probe.' }
    Write-AictPolicyJson -Path (Join-Path $Context.Root 'boot-window-execution.json') -Value @{runId=$Context.RunId;lastRunTimeUtc=$taskInfo.LastRunTime.ToUniversalTime().ToString('o');lastTaskResult=$taskInfo.LastTaskResult;manuallyStarted=$false;coverage='POST_LOGON_TASK_OBSERVATIONS_ONLY'}
    return $taskValue
}
function Read-AictBootShutdownEvidence {
    param($Context,[string]$StartUtc,[string]$BootUtc)
    $taskStart=[DateTimeOffset]::Parse($StartUtc);$taskBoot=[DateTimeOffset]::Parse($BootUtc)
    $taskRows=@();$taskQuerySucceeded=$true
    try {
        $taskEvents=@(Get-WinEvent -FilterHashtable @{LogName='System';Id=@(1074,6006);StartTime=$taskStart.LocalDateTime;EndTime=$taskBoot.AddSeconds(30).LocalDateTime} -MaxEvents 32 -ErrorAction Stop)
        foreach($taskEvent in $taskEvents) {
            if(($taskEvent.Id -eq 1074 -and $taskEvent.ProviderName -ieq 'USER32') -or ($taskEvent.Id -eq 6006 -and $taskEvent.ProviderName -ieq 'EventLog')) {
                $taskRows+=@([ordered]@{eventId=$taskEvent.Id;provider=$taskEvent.ProviderName;recordId=$taskEvent.RecordId;atUtc=$taskEvent.TimeCreated.ToUniversalTime().ToString('o')})
            }
        }
    } catch { if($_.FullyQualifiedErrorId -notmatch '^NoMatchingEventsFound') { $taskQuerySucceeded=$false } }
    $taskEvidence=[ordered]@{runId=$Context.RunId;windowStartUtc=$taskStart.ToString('o');windowEndUtc=$taskBoot.AddSeconds(30).ToString('o');querySucceeded=$taskQuerySucceeded;shutdownInitiatedEventObserved=(@($taskRows | Where-Object { $_.eventId -eq 1074 }).Count -gt 0);eventLogCleanStopObserved=(@($taskRows | Where-Object { $_.eventId -eq 6006 }).Count -gt 0);metadataOnly=$true;events=$taskRows}
    Write-AictPolicyJson -Path (Join-Path $Context.Root 'shutdown-evidence.json') -Value $taskEvidence
    return $taskEvidence
}
