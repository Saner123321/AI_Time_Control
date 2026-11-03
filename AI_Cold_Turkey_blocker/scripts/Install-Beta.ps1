#Requires -Version 5.1
[CmdletBinding()]
param([switch]$StartExisting,[switch]$Recover,[switch]$NewSession)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$taskPackage=Split-Path -Parent $PSScriptRoot
$taskHostBuild=$false
$taskMachine=Get-CimInstance Win32_ComputerSystem
if(-not $taskHostBuild -and ($taskMachine.Manufacturer -notmatch 'VMware' -or $taskMachine.Model -notmatch 'VMware')) { throw 'VMware guest required; no files, tasks, services or policies changed.' }
$taskIdentity=[Security.Principal.WindowsIdentity]::GetCurrent()
if(-not(New-Object Security.Principal.WindowsPrincipal($taskIdentity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) -or -not [Environment]::Is64BitProcess -or $PSVersionTable.PSEdition -ne 'Desktop') { throw 'Administrator 64-bit Windows PowerShell 5.1 is required.' }
Import-Module -Name (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1') -Force -ErrorAction Stop
$taskSid=$taskIdentity.User.Value
function Write-BetaJson([string]$Path,$Value) { [IO.File]::WriteAllText($Path,($Value|ConvertTo-Json -Depth 30),(New-Object Text.UTF8Encoding($false))) }
function Set-BetaOwner([string]$Path) { $taskAcl=Get-Acl -LiteralPath $Path;$taskAcl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')));Set-Acl -LiteralPath $Path -AclObject $taskAcl }
function Assert-BetaProtected([string]$Path) {
    $taskInfo=Get-Item -LiteralPath $Path
    if($taskInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse privileged input refused.' }
    $taskAcl=Get-Acl -LiteralPath $Path
    if($taskAcl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin @('S-1-5-18','S-1-5-32-544')) { throw 'Existing privileged input is not owned by SYSTEM/Administrators.' }
    $taskWrite=[Security.AccessControl.FileSystemRights]::Write -bor [Security.AccessControl.FileSystemRights]::Delete -bor [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles
    foreach($taskRule in $taskAcl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) { if($taskRule.AccessControlType -eq 'Allow' -and $taskRule.IdentityReference.Value -notin @('S-1-5-18','S-1-5-32-544') -and ($taskRule.FileSystemRights -band $taskWrite)) { throw 'Existing privileged input is writable by an ordinary account.' } }
}
function Read-BetaManifest([string]$Folder) {
    $taskManifest=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $Folder 'MANIFEST.json') | ConvertFrom-Json
    if($taskManifest.kind -cne 'AICT_INTEGRATED_BETA_V1') { throw 'Wrong integrated package.' }
    foreach($taskFile in $taskManifest.files.PSObject.Properties) { if($taskFile.Name -match '(^[\\/]|\.\.|:)' -or (Get-FileHash -LiteralPath (Join-Path $Folder $taskFile.Name) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $taskFile.Value) { throw ('Package integrity mismatch: '+$taskFile.Name) } }
    return $taskManifest
}
function Invoke-BetaProcess([string]$File,[string[]]$Arguments) {
    $taskStart=New-Object Diagnostics.ProcessStartInfo;$taskStart.FileName=$File;$taskStart.Arguments=($Arguments | ForEach-Object { if($_ -match '["\r\n]') { throw 'Invalid fixed helper argument.' };'"'+$_+'"' }) -join ' ';$taskStart.UseShellExecute=$false;$taskStart.CreateNoWindow=$true;$taskStart.StandardOutputEncoding=[Text.Encoding]::UTF8;$taskStart.StandardErrorEncoding=[Text.Encoding]::UTF8;$taskStart.RedirectStandardOutput=$true;$taskStart.RedirectStandardError=$true
    $taskChild=[Diagnostics.Process]::Start($taskStart);$taskOut=$taskChild.StandardOutput.ReadToEndAsync();$taskErr=$taskChild.StandardError.ReadToEndAsync();if(-not $taskChild.WaitForExit(120000)) { $taskChild.Kill();throw 'Privileged helper timeout.' };$taskCode=$taskChild.ExitCode;$taskChild.Dispose();if($taskCode -ne 0) { throw ($taskErr.Result+$taskOut.Result) };return $taskOut.Result
}
function Set-BetaTaskSecurity([string]$Name) {
    $taskScheduler=New-Object -ComObject Schedule.Service
    try {$taskScheduler.Connect();$taskTask=$taskScheduler.GetFolder('\').GetTask($Name);$taskTask.SetSecurityDescriptor('D:P(A;;FA;;;SY)(A;;FA;;;BA)',16);$taskSd=New-Object Security.AccessControl.RawSecurityDescriptor($taskTask.GetSecurityDescriptor(4));foreach($taskAce in $taskSd.DiscretionaryAcl) { if($taskAce.AceQualifier -eq [Security.AccessControl.AceQualifier]::AccessAllowed -and $taskAce.SecurityIdentifier.Value -notin @('S-1-5-18','S-1-5-32-544')) { throw 'Task grants unexpected ordinary-user control.' } }}finally{[Runtime.InteropServices.Marshal]::FinalReleaseComObject($taskScheduler)|Out-Null}
}
function Ensure-BetaTask($Context,[string]$Kind) {
    $taskName='AICT-Beta-'+$Kind+'-'+$Context.Id
    $taskExe=if($Kind -eq 'Agent') { Join-Path $Context.Root 'Agent\Aict.Beta.Agent.exe' } else { Join-Path $Context.Root 'Service\Aict.Beta.Service.exe' }
    $taskArgs=if($Kind -eq 'Agent') { '--session-agent' } elseif($Kind -eq 'Recovery') { '--beta-cleanup-force' } elseif($Kind -eq 'TrialSafety') { '--host-trial-safety' } else { '--beta-cleanup' }
    $taskPresent=Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if($null -ne $taskPresent) { if(@($taskPresent.Actions).Count -ne 1 -or $taskPresent.Actions[0].Execute -ine $taskExe -or $taskPresent.Actions[0].Arguments -cne $taskArgs) { throw 'Existing task does not match this installation.' };Set-BetaTaskSecurity $taskName;return }
    $taskAction=New-ScheduledTaskAction -Execute $taskExe -Argument $taskArgs
    if($Kind -eq 'Agent') {
        $taskPrincipal=New-ScheduledTaskPrincipal -UserId $taskSid -LogonType Interactive -RunLevel Limited
        $taskTrigger=New-ScheduledTaskTrigger -AtLogOn -User $taskSid
        $taskSettings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
        $null=Register-ScheduledTask -TaskName $taskName -Action $taskAction -Principal $taskPrincipal -Trigger $taskTrigger -Settings $taskSettings
    } else {
        $taskPrincipal=New-ScheduledTaskPrincipal -UserId 'S-1-5-18' -LogonType ServiceAccount -RunLevel Highest
        $taskSettings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 5) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
                if($Kind -eq 'TrialSafety') {
            $taskConfig=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $Context.Root 'config.json')|ConvertFrom-Json
            $taskAt=([DateTimeOffset]::Parse($taskConfig.hostTrialEndsUtc)).AddMinutes(1).LocalDateTime
            $taskTrigger=New-ScheduledTaskTrigger -Once -At $taskAt
            $taskSettings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 5) -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
            $null=Register-ScheduledTask -TaskName $taskName -Action $taskAction -Principal $taskPrincipal -Trigger $taskTrigger -Settings $taskSettings
        } else { $null=Register-ScheduledTask -TaskName $taskName -Action $taskAction -Principal $taskPrincipal -Settings $taskSettings }
    }
    Set-BetaTaskSecurity $taskName
}
$taskResult=[ordered]@{kind='AICT_BETA_SETUP';atUtc=[DateTimeOffset]::UtcNow.ToString('o');completed=$false;root=$null;importedLegacy=$false;recovery=$Recover.IsPresent;error=$null}
$taskContext=$null
try {
    $taskSource=Read-BetaManifest $taskPackage
    if($taskHostBuild){$taskCapability=Invoke-BetaProcess (Join-Path $taskPackage 'Service\Aict.Beta.Service.exe') @('--build-info')|ConvertFrom-Json;if(-not $taskCapability.hostTrial -or $taskCapability.trialMinutes -ne 30){throw 'Host trial build mismatch; no installation performed.'}}
    $taskExisting=@(Get-ChildItem -LiteralPath ([Environment]::GetFolderPath('CommonApplicationData')) -Directory -Filter 'AICT-Beta-*' | Sort-Object CreationTimeUtc -Descending | Where-Object { try { $taskCandidate=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $_.FullName 'config.json') | ConvertFrom-Json;$taskCandidate.probeSid -ceq $taskSid -and ([bool]($taskCandidate.PSObject.Properties['hostTrial'] -and $taskCandidate.hostTrial) -eq $taskHostBuild) }catch{$false} })
    if($NewSession) {
        if($taskExisting.Count -eq 0) { throw 'No previous authorized installation to reinstall.' }
        $taskPrevious=$taskExisting[0].FullName;Assert-BetaProtected $taskPrevious;Assert-BetaProtected (Join-Path $taskPrevious 'MANIFEST.json');$null=Read-BetaManifest $taskPrevious
        foreach($taskFolder in @('Service','Agent','App','state')) { Assert-BetaProtected (Join-Path $taskPrevious $taskFolder) }
        $taskPreviousManifest=Read-BetaManifest $taskPrevious;foreach($taskFile in $taskPreviousManifest.files.PSObject.Properties) { Assert-BetaProtected (Join-Path $taskPrevious $taskFile.Name) }
        Assert-BetaProtected (Join-Path $taskPrevious 'config.json')
        $null=Invoke-BetaProcess (Join-Path $taskPrevious 'Service\Aict.Beta.Service.exe') @('--check-reinstall')
        $taskOwnedRegistry=Join-Path $taskPrevious 'state\registry-owned.json'
        if(Test-Path -LiteralPath $taskOwnedRegistry) {
            foreach($taskEntry in (Get-Content -Raw -Encoding UTF8 -LiteralPath $taskOwnedRegistry | ConvertFrom-Json)) {
                $taskAllowedKeys=@(($taskSid+'\Software\Microsoft\Edge\NativeMessagingHosts\com.aict.beta'),($taskSid+'\Software\Google\Chrome\NativeMessagingHosts\com.aict.beta'))
                if($taskEntry.Key -in $taskAllowedKeys -and $taskEntry.Name -ceq '' -and $taskEntry.Value -ieq (Join-Path $taskPrevious 'native-host.json')) {
                    $taskHostKey=[Microsoft.Win32.Registry]::Users.OpenSubKey($taskEntry.Key,$true)
                    if($null -ne $taskHostKey) { try { if([string]$taskHostKey.GetValue('') -ieq $taskEntry.Value) { $taskHostKey.DeleteValue('',$false);$taskHostKey.Flush() } }finally { $taskHostKey.Dispose() } }
                }
            }
        }
        $taskExisting=@()
    }
    if($taskExisting.Count -gt 0) {
        $taskRoot=$taskExisting[0].FullName;$taskId=(Split-Path -Leaf $taskRoot).Substring(10)
        $taskParsedId=[guid]::Empty;if(-not [guid]::TryParseExact($taskId,'N',[ref]$taskParsedId)) { throw 'Invalid existing ID.' }
        if((Get-Item -LiteralPath $taskRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse installation refused.' }
        Assert-BetaProtected $taskRoot;Assert-BetaProtected (Join-Path $taskRoot 'MANIFEST.json');Assert-BetaProtected (Join-Path $taskRoot 'config.json')
        foreach($taskFolder in @('Service','Agent','App','state')) { Assert-BetaProtected (Join-Path $taskRoot $taskFolder) }
        $taskContext=[pscustomobject]@{Id=$taskId;Root=$taskRoot};$taskInstalledManifest=Read-BetaManifest $taskRoot
        foreach($taskFile in $taskInstalledManifest.files.PSObject.Properties) { Assert-BetaProtected (Join-Path $taskRoot $taskFile.Name) }
    } else {
        if($StartExisting -or $Recover) { throw 'No existing installation for this account; no empty replacement created.' }
        $taskId=[guid]::NewGuid().ToString('N');$taskRoot=Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) ('AICT-Beta-'+$taskId);$taskContext=[pscustomobject]@{Id=$taskId;Root=$taskRoot}
        $taskAcl=New-Object Security.AccessControl.DirectorySecurity;$taskAcl.SetAccessRuleProtection($true,$false);$taskAcl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
        foreach($taskPrivilege in @('S-1-5-18','S-1-5-32-544')) { $taskAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($taskPrivilege)),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))) }
        $taskAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($taskSid)),'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow')))
        if(Test-Path -LiteralPath $taskRoot) { throw 'New installation ID collision; existing directory left untouched.' };$null=[IO.Directory]::CreateDirectory($taskRoot,$taskAcl)
        foreach($taskFile in $taskSource.files.PSObject.Properties) { $taskDest=Join-Path $taskRoot $taskFile.Name;$null=New-Item -ItemType Directory -Path (Split-Path -Parent $taskDest) -Force;Copy-Item -LiteralPath (Join-Path $taskPackage $taskFile.Name) -Destination $taskDest;Set-BetaOwner $taskDest }
        foreach($taskMeta in @('MANIFEST.json','SHA256SUMS.txt')) { Copy-Item -LiteralPath (Join-Path $taskPackage $taskMeta) -Destination (Join-Path $taskRoot $taskMeta);Set-BetaOwner (Join-Path $taskRoot $taskMeta) }
        $null=New-Item -ItemType Directory -Path (Join-Path $taskRoot 'state') -Force
        foreach($taskDir in @(Get-ChildItem -LiteralPath $taskRoot -Directory -Recurse)) { Set-BetaOwner $taskDir.FullName }
        Write-BetaJson (Join-Path $taskRoot 'config.json') @{runId=$taskId;probeSid=$taskSid;targetSipHash=$taskSource.target_sip_hash;targetSize=$taskSource.target_size;interactiveMode=$true;hostTrial=$taskHostBuild;hostTrialEndsUtc=$(if($taskHostBuild){[DateTimeOffset]::UtcNow.AddMinutes(30).ToString('o')}else{$null})};Set-BetaOwner (Join-Path $taskRoot 'config.json')
    }
    foreach($taskRuntimeFolder in @('state','probe')) {
        $taskRuntimePath=Join-Path $taskRoot $taskRuntimeFolder
        if(-not(Test-Path -LiteralPath $taskRuntimePath)) { $null=New-Item -ItemType Directory -Path $taskRuntimePath;Set-BetaOwner $taskRuntimePath }
        Assert-BetaProtected $taskRuntimePath
    }
    $taskResult.root=$taskRoot
    foreach($taskKind in $(if($taskHostBuild -and -not $Recover){@('Recovery','Uninstall','Agent','TrialSafety')}else{@('Recovery','Uninstall','Agent')})) { Ensure-BetaTask $taskContext $taskKind }
    if($Recover) {
        Start-ScheduledTask -TaskName ('AICT-Beta-Recovery-'+$taskId)
        $taskWait=[Diagnostics.Stopwatch]::StartNew();$taskCleanup=Join-Path $taskRoot 'state\cleanup-result.json'
        while($taskWait.Elapsed.TotalSeconds -lt 150 -and -not(Test-Path -LiteralPath $taskCleanup)) { Start-Sleep -Milliseconds 250 }
        if(-not(Test-Path -LiteralPath $taskCleanup) -or -not (Get-Content -Raw -Encoding UTF8 -LiteralPath $taskCleanup | ConvertFrom-Json).completed) { throw 'Recovery did not confirm completion; preserve operation-fault.json and state.' }
        $taskResult.completed=$true
    } else {
        $taskDb=Join-Path $taskRoot 'state\engine.sqlite'
        if(-not(Test-Path -LiteralPath $taskDb) -and -not $NewSession -and -not $taskHostBuild) {
            $taskLegacy=@(Get-ChildItem -LiteralPath ([Environment]::GetFolderPath('CommonApplicationData')) -Directory -Filter 'AICT-Interactive-*' | Sort-Object CreationTimeUtc -Descending | Where-Object { try { $taskOld=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $_.FullName 'config.json') | ConvertFrom-Json;$taskOld.probeSid -ceq $taskSid -and (Test-Path -LiteralPath (Join-Path $_.FullName 'state\engine.sqlite')) }catch{$false} })
            if($taskLegacy.Count -gt 0) { $taskOldId=$taskLegacy[0].Name.Substring(17);$null=Invoke-BetaProcess (Join-Path $taskRoot 'Service\Aict.Beta.Service.exe') @('--import-v1',$taskOldId);$taskResult.importedLegacy=$true }
        }
        if(Test-Path -LiteralPath $taskDb) { $null=Invoke-BetaProcess (Join-Path $taskRoot 'Service\Aict.Beta.Service.exe') @('--check-state') }
        if((Get-Service -Name BFE).Status -ne 'Running') { Start-Service BFE }
        if((Get-Service -Name AppIDSvc).Status -ne 'Running') { Start-Service AppIDSvc }
        $taskService=Get-Service -Name ('AICT-Beta-'+$taskId) -ErrorAction SilentlyContinue
        if($null -eq $taskService) { $null=Invoke-BetaProcess (Join-Path $taskRoot 'BetaBridge.exe') @('--install') } elseif($taskService.Status -ne 'Running') { $null=Invoke-BetaProcess (Join-Path $taskRoot 'BetaBridge.exe') @('--start') }
        Start-ScheduledTask -TaskName ('AICT-Beta-Agent-'+$taskId)
        $taskWait=[Diagnostics.Stopwatch]::StartNew();$taskHealthy=$false
        while($taskWait.Elapsed.TotalSeconds -lt 90) { try { $taskStatus=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskRoot 'state\status.json') | ConvertFrom-Json;if($taskStatus.ok -and $taskStatus.status.storageHealthy -and $taskStatus.status.execution.confirmed -and $taskStatus.status.telemetryConnected -and $taskStatus.status.clockConsistent) { $taskHealthy=$true;break } }catch{};Start-Sleep -Milliseconds 250 }
        if(-not $taskHealthy) { throw 'Backend policy readback not healthy; preserve state/service-fault.json, status.json and setup-result.json.' }
        $taskShell=New-Object -ComObject WScript.Shell
        try { $taskShortcutPath=Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) $(if($taskHostBuild){'专注计划（主机测试）.lnk'}else{'专注计划.lnk'});$taskShortcut=$taskShell.CreateShortcut($taskShortcutPath);if((Test-Path -LiteralPath $taskShortcutPath) -and -not $taskShortcut.TargetPath.StartsWith((Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'AICT-Beta-'),[StringComparison]::OrdinalIgnoreCase)) { throw 'Existing unrelated desktop shortcut left unchanged.' };$taskShortcut.TargetPath=Join-Path $taskRoot 'App\Aict.Beta.Desktop.exe';$taskShortcut.WorkingDirectory=$taskRoot;$taskShortcut.Description='专注计划：应用与网站规则、日程、额度和锁定';$taskShortcut.Save() }finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($taskShell)|Out-Null }
        $taskArp='HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AICT-Beta-'+$taskId;$null=New-Item -Path $taskArp -Force
        foreach($taskPair in @(@('DisplayName','专注计划（自用测试版）'),@('DisplayVersion','0.1.0'),@('Publisher','AICT personal project'),@('InstallLocation',$taskRoot),@('UninstallString',('"'+(Join-Path $taskRoot 'App\Aict.Beta.Desktop.exe')+'" --uninstall')))) { $null=New-ItemProperty -LiteralPath $taskArp -Name $taskPair[0] -Value $taskPair[1] -PropertyType String -Force }
        $taskResult.completed=$true
    }
}catch { $taskResult.error=$_.Exception.ToString() }
finally { if($null -ne $taskContext) { Write-BetaJson (Join-Path $taskContext.Root 'setup-result.json') $taskResult;Set-BetaOwner (Join-Path $taskContext.Root 'setup-result.json') };if($taskPackage -ne $taskResult.root){try { Write-BetaJson (Join-Path $taskPackage 'setup-result.json') $taskResult }catch{}} }
if(-not $taskResult.completed) { throw 'Beta setup did not complete. Saved rules and diagnostic evidence were retained.' }
