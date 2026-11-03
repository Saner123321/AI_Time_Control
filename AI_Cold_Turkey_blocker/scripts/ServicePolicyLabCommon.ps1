#Requires -Version 5.1
# Functions only. All public entries call the guest guard before inspecting a package/locator.
function Assert-AictPolicyGuest {
    $taskMachine=Get-CimInstance Win32_ComputerSystem
    if($taskMachine.Manufacturer -notmatch 'VMware' -or $taskMachine.Model -notmatch 'VMware') { throw 'VMware guest required. No files, SCM or policy changes performed.' }
    $taskIdentity=[Security.Principal.WindowsIdentity]::GetCurrent()
    if(-not(New-Object Security.Principal.WindowsPrincipal($taskIdentity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) -or -not [Environment]::Is64BitProcess -or $PSVersionTable.PSEdition -ne 'Desktop' -or [Diagnostics.Process]::GetCurrentProcess().SessionId -eq 0) { throw 'Use administrator 64-bit Windows PowerShell 5.1 on the guest desktop.' }
}
function Read-AictPolicyManifest {
    param([string]$Package,[ValidateSet('AICT_SERVICE_POLICY_LAB_V1','AICT_BOOT_POLICY_LAB_V1','AICT_INTERACTIVE_LAB_V1')][string]$ExpectedKind='AICT_SERVICE_POLICY_LAB_V1')
    $taskManifest=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $Package 'MANIFEST.json') | ConvertFrom-Json
    if($taskManifest.kind -cne $ExpectedKind) { throw 'Unexpected integration package.' }
    foreach($taskFile in $taskManifest.files.PSObject.Properties) {
        if($taskFile.Name -match '(^[\\/]|\.\.|:)' -or (Get-FileHash -LiteralPath (Join-Path $Package $taskFile.Name) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $taskFile.Value) { throw ('Package integrity failed: '+$taskFile.Name) }
    }
    return $taskManifest
}
function New-AictPolicyContext {
    param([string]$Package,[string]$RunId)
    if($RunId -notmatch '^[a-f0-9]{32}$') { throw 'Invalid ownership ID.' }
    return [pscustomobject]@{Package=$Package;RunId=$RunId;Root=(Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) ('AICT-ServicePolicy-'+$RunId));TaskName=('AICT-ServicePolicy-Cleanup-'+$RunId)}
}
function Set-AictPolicyOwner {
    param([string]$Path)
    $taskAcl=Get-Acl -LiteralPath $Path;$taskAcl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')));Set-Acl -LiteralPath $Path -AclObject $taskAcl
}
function Write-AictPolicyJson {
    param([string]$Path,$Value)
    $taskBytes=(New-Object Text.UTF8Encoding($false)).GetBytes(($Value | ConvertTo-Json -Depth 25))
    $taskFile=New-Object IO.FileStream($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $taskFile.Write($taskBytes,0,$taskBytes.Length);$taskFile.Flush($true) } finally { $taskFile.Dispose() }
    Set-AictPolicyOwner -Path $Path
}
function Invoke-AictPolicyNative {
    param($Context,[ValidateSet('--install','--inspect','--crash','--cleanup','--arm-boot','--boot-metadata')][string]$Mode,[string]$Tag)
    if($Mode -eq '--cleanup') {
        # The device MDM bridge needs LocalSystem. Never retry MI policy cleanup as the interactive administrator.
        $taskOutPath=Join-Path $Context.Root 'watchdog-stdout.txt';$taskBeforeId=$null
        if(Test-Path -LiteralPath $taskOutPath) { try { $taskBeforeId=(Get-Content -Raw -Encoding UTF8 -LiteralPath $taskOutPath | ConvertFrom-Json).invocationId } catch { } }
        $task=Get-ScheduledTask -TaskName $Context.TaskName -ErrorAction Stop
        $taskExpected='/d /c "'+(Join-Path $Context.Root 'Run-cleanup.cmd')+'"'
        if(@($task.Actions).Count -ne 1 -or $task.Actions[0].Execute -ine "$env:SystemRoot\System32\cmd.exe" -or $task.Actions[0].Arguments -cne $taskExpected -or $task.Principal.UserId -notin @('SYSTEM','S-1-5-18','NT AUTHORITY\SYSTEM')) { throw 'Cleanup task identity changed; refused execution.' }
        Start-ScheduledTask -TaskName $Context.TaskName -ErrorAction Stop
        $taskWait=[Diagnostics.Stopwatch]::StartNew();$taskValue=$null
        while($taskWait.Elapsed.TotalSeconds -lt 95) {
            if((Get-ScheduledTask -TaskName $Context.TaskName).State -ne 'Running' -and (Test-Path -LiteralPath $taskOutPath)) {
                try { $taskCandidate=Get-Content -Raw -Encoding UTF8 -LiteralPath $taskOutPath | ConvertFrom-Json
                    if($taskCandidate.runId -ceq $Context.RunId -and $taskCandidate.invocationId -and $taskCandidate.invocationId -cne $taskBeforeId) { $taskValue=$taskCandidate;break }
                } catch { }
                if((Get-ScheduledTaskInfo -TaskName $Context.TaskName).LastTaskResult -ne 0) { break }
            }
            Start-Sleep -Milliseconds 100
        }
        if($null -eq $taskValue) { throw 'Independent SYSTEM cleanup did not confirm completion. Keep its task, stderr and protected directory.' }
        if($Tag) { Write-AictPolicyJson -Path (Join-Path $Context.Root ($Tag+'.json')) -Value $taskValue }
        return $taskValue
    }
    $taskStart=New-Object Diagnostics.ProcessStartInfo
    $taskBridgeName=if($Context.PSObject.Properties['BridgeFileName']) { $Context.BridgeFileName } else { 'AictPolicyBridge.exe' }
    $taskStart.FileName=Join-Path $Context.Root $taskBridgeName;$taskStart.Arguments=$Mode;$taskStart.UseShellExecute=$false;$taskStart.CreateNoWindow=$true
    $taskStart.RedirectStandardOutput=$true;$taskStart.RedirectStandardError=$true
    $taskStart.StandardOutputEncoding=New-Object Text.UTF8Encoding($false);$taskStart.StandardErrorEncoding=New-Object Text.UTF8Encoding($false)
    $taskChild=[Diagnostics.Process]::Start($taskStart);$taskOut=$taskChild.StandardOutput.ReadToEndAsync();$taskErr=$taskChild.StandardError.ReadToEndAsync()
    if(-not $taskChild.WaitForExit(90000)) { $taskChild.Kill();$taskChild.WaitForExit();throw ('Fixed native operation timeout: '+$Mode) }
    $taskExit=$taskChild.ExitCode;$taskChild.Dispose()
    if($taskExit -ne 0) {
        Write-AictPolicyJson -Path (Join-Path $Context.Root ('native-error-'+[guid]::NewGuid().ToString('N')+'.json')) -Value @{mode=$Mode;exitCode=$taskExit;stdout=$taskOut.Result;stderr=$taskErr.Result}
        throw ('Native '+$Mode+' rejected: '+$taskErr.Result.Trim())
    }
    $taskValue=$taskOut.Result | ConvertFrom-Json
    if($taskValue.runId -cne $Context.RunId) { throw 'Native result ownership mismatch.' }
    if($Tag) { Write-AictPolicyJson -Path (Join-Path $Context.Root ($Tag+'.json')) -Value $taskValue }
    return $taskValue
}
function Read-AictPolicyStatus {
    param($Context)
    $taskFile=New-Object IO.FileStream((Join-Path $Context.Root 'state\service-status.json'),[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    $taskReader=New-Object IO.StreamReader($taskFile,(New-Object Text.UTF8Encoding($false)))
    try { return $taskReader.ReadToEnd() | ConvertFrom-Json } finally { $taskReader.Dispose() }
}
function Assert-AictPolicyProbeFile {
    param([string]$Path)
    $taskFile=Get-Item -LiteralPath $Path
    $taskParent=Get-Item -LiteralPath (Split-Path -Parent $Path)
    if($taskParent.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse probe directory refused.' }
    if($taskFile.PSIsContainer -or $taskFile.Length -gt 1048576 -or ($taskFile.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Unsafe/oversized probe output; refused elevated read/export.' }
    if((Get-Acl -LiteralPath $Path).GetOwner([Security.Principal.SecurityIdentifier]).Value -cne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value) { throw 'Probe file owner mismatch; refused elevated read/export.' }
}
function Invoke-AictPolicyProbe {
    param($Context,[ValidateSet('baseline','blocked','during-crash','after-crash','restored','pipe','after-boot')][string]$Phase)
    $taskProbe=Join-Path $Context.Root 'probe';$taskName='AICT-ServicePolicy-Probe-'+$Context.RunId+'-'+$Phase
    $taskBatch=Join-Path $Context.Root ('Run-probe-'+$Phase+'.cmd');$taskExitPath=Join-Path $taskProbe ($Phase+'-exit.txt')
    $taskExe=if($Phase -eq 'pipe') { Join-Path $Context.Root 'Service\Aict.Service.exe' } else { Join-Path $Context.Root 'AictPolicyBridge.exe' }
    $taskArgs=if($Phase -eq 'pipe') { '--lab-client' } else { '--probe '+$Phase }
    $taskLines=@('@echo off',('"'+$taskExe+'" '+$taskArgs+' 1>"'+(Join-Path $taskProbe ($Phase+'-stdout.txt'))+'" 2>"'+(Join-Path $taskProbe ($Phase+'-stderr.txt'))+'"'),'set "AICT_POLICY_EXIT=%ERRORLEVEL%"',('>"'+$taskExitPath+'" echo %AICT_POLICY_EXIT%'),'exit /b %AICT_POLICY_EXIT%')
    [IO.File]::WriteAllLines($taskBatch,[string[]]$taskLines,[Text.Encoding]::ASCII);Set-AictPolicyOwner -Path $taskBatch
    $taskPrincipal=New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value) -LogonType Interactive -RunLevel Limited
    $taskAction=New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\cmd.exe" -Argument ('/d /c "'+$taskBatch+'"')
    $taskSettings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Seconds 65) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    $taskRegistered=$false
    try {
        $null=Register-ScheduledTask -TaskName $taskName -Action $taskAction -Principal $taskPrincipal -Settings $taskSettings -ErrorAction Stop;$taskRegistered=$true
        Start-ScheduledTask -TaskName $taskName -ErrorAction Stop
        $taskWatch=[Diagnostics.Stopwatch]::StartNew()
        while(-not(Test-Path -LiteralPath $taskExitPath) -and $taskWatch.Elapsed.TotalSeconds -lt 60) { Start-Sleep -Milliseconds 100 }
        if(-not(Test-Path -LiteralPath $taskExitPath)) { throw ('Limited probe timeout: '+$Phase) }
        Assert-AictPolicyProbeFile -Path $taskExitPath
        if([int](Get-Content -Raw -LiteralPath $taskExitPath) -ne 0) { throw ('Limited probe failed: '+$Phase+'. Preserve probe stderr and evidence.') }
        $taskJsonName=if($Phase -eq 'pipe') { 'pipe-client.json' } else { $Phase+'.json' }
        $taskJsonPath=Join-Path $taskProbe $taskJsonName;Assert-AictPolicyProbeFile -Path $taskJsonPath
        $taskValue=Get-Content -Raw -Encoding UTF8 -LiteralPath $taskJsonPath | ConvertFrom-Json
        if($taskValue.runId -cne $Context.RunId -or -not $taskValue.limitedToken -or ($Phase -ne 'pipe' -and -not $taskValue.passed)) { throw 'Probe identity or observations did not pass.' }
        return $taskValue
    } finally {
        if($taskRegistered) {
            if((Get-ScheduledTask -TaskName $taskName).State -eq 'Running') { Stop-ScheduledTask -TaskName $taskName -ErrorAction Stop }
            Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction Stop
            if($null -ne (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue)) { throw 'Probe task removal unconfirmed.' }
        }
    }
}
function Remove-AictPolicyWatchdog {
    param($Context)
    $task=Get-ScheduledTask -TaskName $Context.TaskName -ErrorAction SilentlyContinue
    if($null -eq $task) { return $true }
    $taskExpected='/d /c "'+(Join-Path $Context.Root 'Run-cleanup.cmd')+'"'
    if(@($task.Actions).Count -ne 1 -or $task.Actions[0].Execute -ine "$env:SystemRoot\System32\cmd.exe" -or $task.Actions[0].Arguments -cne $taskExpected -or $task.Principal.UserId -notin @('SYSTEM','S-1-5-18','NT AUTHORITY\SYSTEM')) { throw 'Cleanup task changed; left untouched.' }
    if($task.State -eq 'Running') { throw 'Independent cleanup currently running; let it finish and preserve evidence.' }
    Unregister-ScheduledTask -TaskName $Context.TaskName -Confirm:$false -ErrorAction Stop
    return $null -eq (Get-ScheduledTask -TaskName $Context.TaskName -ErrorAction SilentlyContinue)
}
function Export-AictPolicyEvidence {
    param($Context,$Result,[ValidateSet('service-policy','boot-policy-prepare','boot-policy-finish','boot-policy-recovery')][string]$Prefix='service-policy')
    $taskOutput=Join-Path $Context.Package ($Prefix+'-'+[DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
    $null=New-Item -ItemType Directory -Path $taskOutput
    foreach($taskFile in @(Get-ChildItem -LiteralPath $Context.Root -File | Where-Object { $_.Extension -eq '.json' -and $_.Name -ne 'config.json' })) { Copy-Item -LiteralPath $taskFile.FullName -Destination (Join-Path $taskOutput $taskFile.Name) }
    foreach($taskName in @('watchdog-stdout.txt','watchdog-stderr.txt')) { $taskSource=Join-Path $Context.Root $taskName;if(Test-Path -LiteralPath $taskSource) { Copy-Item -LiteralPath $taskSource -Destination (Join-Path $taskOutput $taskName) } }
    foreach($taskName in @('service-status.json','service-fault.json')) { $taskSource=Join-Path $Context.Root ('state\'+$taskName);if(Test-Path -LiteralPath $taskSource) { Copy-Item -LiteralPath $taskSource -Destination (Join-Path $taskOutput ('final-'+$taskName)) } }
    $taskProbeNames=@('pipe-client.json')
    foreach($taskPhase in @('baseline','blocked','during-crash','after-crash','restored','pipe','boot-window','after-boot')) { $taskProbeNames+=@(($taskPhase+'.json'),($taskPhase+'-stdout.txt'),($taskPhase+'-stderr.txt'),($taskPhase+'-exit.txt')) }
    foreach($taskName in $taskProbeNames) {
        $taskSource=Join-Path (Join-Path $Context.Root 'probe') $taskName
        if(-not(Test-Path -LiteralPath $taskSource)) { continue }
        try { Assert-AictPolicyProbeFile -Path $taskSource;Copy-Item -LiteralPath $taskSource -Destination (Join-Path $taskOutput ('probe-'+$taskName)) }
        catch { $Result.exportRefusedFiles+=@($taskName) }
    }
    Write-AictPolicyJson -Path (Join-Path $taskOutput 'runner-result.json') -Value $Result
    $taskHashes=@(Get-ChildItem -LiteralPath $taskOutput -File | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()+'  '+$_.Name })
    [IO.File]::WriteAllLines((Join-Path $taskOutput 'SHA256SUMS.txt'),[string[]]$taskHashes,(New-Object Text.UTF8Encoding($false)))
    Write-Host ('Evidence directory: '+$taskOutput)
}
