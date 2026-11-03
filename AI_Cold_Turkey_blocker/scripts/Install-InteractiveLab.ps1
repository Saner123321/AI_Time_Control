#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][ValidateNotNullOrEmpty()][string]$SnapshotReference)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'InteractiveLabCommon.ps1')
Assert-AictPolicyGuest
$taskPackage=Split-Path -Parent $PSScriptRoot;$taskManifest=Read-AictPolicyManifest -Package $taskPackage -ExpectedKind 'AICT_INTERACTIVE_LAB_V1'
if(Test-Path -LiteralPath (Join-Path $taskPackage 'active-interactive.json')) { throw 'This extraction already has a session. Reopen/collect/recover it instead of reinstalling.' }
if((Get-Service -Name AppIDSvc).Status -ne 'Running') { throw 'AppIDSvc must already be running; its configuration is left unchanged.' }
$taskContext=New-AictInteractiveContext -Package $taskPackage -RunId ([guid]::NewGuid().ToString('N'))
$taskSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value;$taskOwner=New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544');$taskUser=New-Object Security.Principal.SecurityIdentifier($taskSid)
$taskAcl=New-Object Security.AccessControl.DirectorySecurity;$taskAcl.SetAccessRuleProtection($true,$false);$taskAcl.SetOwner($taskOwner)
foreach($taskPrivilege in @('S-1-5-18','S-1-5-32-544')) { $taskAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($taskPrivilege)),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))) }
$taskAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($taskUser,'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow')))
$null=[IO.Directory]::CreateDirectory($taskContext.Root,$taskAcl)
foreach($taskSub in @('Service','App','state','Targets','probe')) { $null=New-Item -ItemType Directory -Path (Join-Path $taskContext.Root $taskSub);Set-AictPolicyOwner -Path (Join-Path $taskContext.Root $taskSub) }
$taskProbeAcl=Get-Acl -LiteralPath (Join-Path $taskContext.Root 'probe');$taskProbeAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($taskUser,'Modify','ContainerInherit,ObjectInherit','None','Allow')));Set-Acl -LiteralPath (Join-Path $taskContext.Root 'probe') -AclObject $taskProbeAcl
foreach($taskFolder in @('Service','App')) {
    foreach($taskFile in @(Get-ChildItem -LiteralPath (Join-Path $taskPackage $taskFolder) -File -Recurse)) {
        $taskRelative=$taskFile.FullName.Substring((Join-Path $taskPackage $taskFolder).Length).TrimStart('\');$taskDestination=Join-Path (Join-Path $taskContext.Root $taskFolder) $taskRelative
        $null=New-Item -ItemType Directory -Path (Split-Path -Parent $taskDestination) -Force;Copy-Item -LiteralPath $taskFile.FullName -Destination $taskDestination;Set-AictPolicyOwner -Path $taskDestination
    }
    foreach($taskDirectory in @(Get-ChildItem -LiteralPath (Join-Path $taskContext.Root $taskFolder) -Directory -Recurse)) { Set-AictPolicyOwner -Path $taskDirectory.FullName }
}
Copy-Item -LiteralPath (Join-Path $taskPackage 'lab\AictInteractiveBridge.exe') -Destination (Join-Path $taskContext.Root 'AictInteractiveBridge.exe');Set-AictPolicyOwner -Path (Join-Path $taskContext.Root 'AictInteractiveBridge.exe')
foreach($taskRelative in @('Targets\Target.exe','Targets\Renamed.exe','MovedTarget.exe','Targets\AllowedControl.exe','Targets\AictDemo.exe')) {
    $taskSource=if($taskRelative.EndsWith('AictDemo.exe')) { 'lab\AictDemo.exe' } elseif($taskRelative.EndsWith('AllowedControl.exe')) { 'lab\AictPolicyControl.exe' } else { 'lab\AictPolicyTarget.exe' }
    Copy-Item -LiteralPath (Join-Path $taskPackage $taskSource) -Destination (Join-Path $taskContext.Root $taskRelative);Set-AictPolicyOwner -Path (Join-Path $taskContext.Root $taskRelative)
}
Write-AictPolicyJson -Path (Join-Path $taskContext.Root 'config.json') -Value @{runId=$taskContext.RunId;probeSid=$taskSid;targetSipHash=$taskManifest.target_sip_hash;targetSize=$taskManifest.target_size;interactiveMode=$true}
Write-AictPolicyJson -Path (Join-Path $taskContext.Root 'connection.json') -Value @{runId=$taskContext.RunId;mode='VM_INTERACTIVE_APPLICATION_CONTROL'}
[IO.File]::WriteAllText((Join-Path $taskContext.Root 'protection-canary.bin'),'AICT protected permissions canary');Set-AictPolicyOwner -Path (Join-Path $taskContext.Root 'protection-canary.bin')
foreach($taskEntry in $taskManifest.files.PSObject.Properties) {
    if($taskEntry.Name.StartsWith('Service/') -or $taskEntry.Name.StartsWith('App/')) { $taskPath=Join-Path $taskContext.Root $taskEntry.Name }
    elseif($taskEntry.Name -ceq 'lab/AictInteractiveBridge.exe') { $taskPath=Join-Path $taskContext.Root 'AictInteractiveBridge.exe' } else { continue }
    if((Get-FileHash -LiteralPath $taskPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $taskEntry.Value) { throw 'Staged executable integrity failed before service installation.' }
}
Write-AictPolicyJson -Path (Join-Path $taskPackage 'active-interactive.json') -Value @{runId=$taskContext.RunId;snapshotReference=$SnapshotReference}
foreach($taskPath in @($taskContext.Root,"$env:SystemRoot\System32\cmd.exe")) { if($taskPath -match '[%&!^|<>"\r\n]') { throw 'Unexpected metacharacter in fixed paths.' } }
$taskBatch=Join-Path $taskContext.Root 'Run-cleanup.cmd';$taskLines=@('@echo off',('"'+(Join-Path $taskContext.Root 'AictInteractiveBridge.exe')+'" --cleanup 1>"'+(Join-Path $taskContext.Root 'watchdog-stdout.txt')+'" 2>"'+(Join-Path $taskContext.Root 'watchdog-stderr.txt')+'"'),'set "AICT_CLEANUP_EXIT=%ERRORLEVEL%"','exit /b %AICT_CLEANUP_EXIT%')
[IO.File]::WriteAllLines($taskBatch,[string[]]$taskLines,[Text.Encoding]::ASCII);Set-AictPolicyOwner -Path $taskBatch
$taskResult=[ordered]@{kind='AICT_INTERACTIVE_SESSION';stage='Install';runId=$taskContext.RunId;sourceManifestSha256=(Get-FileHash -LiteralPath (Join-Path $taskPackage 'MANIFEST.json') -Algorithm SHA256).Hash.ToLowerInvariant();snapshotReference=$SnapshotReference;installed=$false;systemStateObserved=$false;serviceAbsent=$false;ownPoliciesAbsent=$false;cleanupTaskRemoved=$false;uiTaskRemoved=$false;manualResistanceChecksRequired=$true;exportRefusedFiles=@();error=$null;cleanupError=$null}
$taskLeave=$false
try {
    $taskCleanupAction=New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\cmd.exe" -Argument ('/d /c "'+$taskBatch+'"')
    $taskOnce=New-ScheduledTaskTrigger -Once -At ((Get-Date).AddMinutes(60));$taskBoot=New-ScheduledTaskTrigger -AtStartup;$taskBoot.Delay='PT5M'
    $taskSystem=New-ScheduledTaskPrincipal -UserId 'S-1-5-18' -LogonType ServiceAccount -RunLevel Highest
    $taskSettings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 3) -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    $null=Register-ScheduledTask -TaskName $taskContext.TaskName -Action $taskCleanupAction -Principal $taskSystem -Trigger @($taskOnce,$taskBoot) -Settings $taskSettings -ErrorAction Stop
    $null=Invoke-AictPolicyNative -Context $taskContext -Mode '--install' -Tag 'install-result'
    $taskWait=[Diagnostics.Stopwatch]::StartNew();$taskHealthy=$false
    while($taskWait.Elapsed.TotalSeconds -lt 40) {
        try { $taskStatus=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskContext.Root 'state\workspace-status.json') | ConvertFrom-Json
            if($taskStatus.ok -and $taskStatus.status.clockConsistent -and $taskStatus.status.execution.verified -and $null -eq $taskStatus.status.execution.fault) { $taskHealthy=$true;break }
        } catch { }
        Start-Sleep -Milliseconds 200
    }
    if(-not $taskHealthy) { throw 'The editable service did not produce a healthy empty-policy readback. Preserve its fault report.' }
    $taskResult.systemStateObserved=$true
    Write-Host 'Running limited-token checks with inert samples before opening the rule editor...'
    $taskSmokeName='AICT-Interactive-Smoke-'+$taskContext.RunId
    $taskSmokeAction=New-ScheduledTaskAction -Execute (Join-Path $taskContext.Root 'Service\Aict.Service.exe') -Argument '--workspace-smoke'
    $taskSmokePrincipal=New-ScheduledTaskPrincipal -UserId $taskSid -LogonType Interactive -RunLevel Limited
    $taskSmokeSettings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 2) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    $taskSmokeRegistered=$false
    try {
        $null=Register-ScheduledTask -TaskName $taskSmokeName -Action $taskSmokeAction -Principal $taskSmokePrincipal -Settings $taskSmokeSettings -ErrorAction Stop;$taskSmokeRegistered=$true
        Start-ScheduledTask -TaskName $taskSmokeName -ErrorAction Stop
        $taskSmokePath=Join-Path $taskContext.Root 'probe\workspace-smoke.json';$taskSmokeWait=[Diagnostics.Stopwatch]::StartNew()
        while(-not(Test-Path -LiteralPath $taskSmokePath) -and $taskSmokeWait.Elapsed.TotalSeconds -lt 100) { Start-Sleep -Milliseconds 200 }
        if(-not(Test-Path -LiteralPath $taskSmokePath)) { throw 'Limited-token application smoke did not produce evidence.' }
        Assert-AictPolicyProbeFile -Path $taskSmokePath
        $taskSmoke=Get-Content -Raw -Encoding UTF8 -LiteralPath $taskSmokePath | ConvertFrom-Json
        if($taskSmoke.runId -cne $taskContext.RunId -or -not $taskSmoke.passed -or -not $taskSmoke.limitedToken) { throw 'Application smoke did not pass; its report and system state are retained.' }
        $taskResult.Add('automaticApplicationChecksPassed',$true)
    } finally {
        if($taskSmokeRegistered) { if((Get-ScheduledTask -TaskName $taskSmokeName).State -eq 'Running') { Stop-ScheduledTask -TaskName $taskSmokeName -ErrorAction Stop };Unregister-ScheduledTask -TaskName $taskSmokeName -Confirm:$false -ErrorAction Stop }
    }
    $taskUiAction=New-ScheduledTaskAction -Execute (Join-Path $taskContext.Root 'App\Aict.Desktop.exe') -Argument ('--vm-service "'+(Join-Path $taskContext.Root 'connection.json')+'"')
    $taskUiPrincipal=New-ScheduledTaskPrincipal -UserId $taskSid -LogonType Interactive -RunLevel Limited
    $taskUiSettings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    $null=Register-ScheduledTask -TaskName $taskContext.UiTaskName -Action $taskUiAction -Principal $taskUiPrincipal -Settings $taskUiSettings -ErrorAction Stop
    Start-ScheduledTask -TaskName $taskContext.UiTaskName -ErrorAction Stop
    $taskResult.installed=$true;$taskLeave=$true
} catch { $taskResult.error=$_.Exception.Message }
finally {
    if(-not $taskLeave) {
        try { $taskCleanup=Invoke-AictPolicyNative -Context $taskContext -Mode '--cleanup' -Tag 'cleanup-result';$taskResult.serviceAbsent=$taskCleanup.serviceAbsent;$taskResult.ownPoliciesAbsent=$taskCleanup.ownPoliciesAbsent
            if($taskResult.serviceAbsent -and $taskResult.ownPoliciesAbsent) { $taskResult.uiTaskRemoved=Remove-AictInteractiveUiTask -Context $taskContext;$taskResult.cleanupTaskRemoved=Remove-AictPolicyWatchdog -Context $taskContext }
        } catch { $taskResult.cleanupError=$_.Exception.Message }
    }
    Export-AictInteractiveEvidence -Context $taskContext -Result $taskResult
}
if(-not $taskResult.installed) { throw 'Interactive installation did not pass; preserve the reported evidence directory.' }
Write-Host 'Interactive application UI started with a limited token. No reboot or repeat snapshot restore is required.'
Write-Host 'Begin with the inert demo. The VM lab expires after 60 minutes; this is not a host installation.'
