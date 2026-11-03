#Requires -Version 5.1
# Shared guest-only orchestration. Dot-sourcing defines functions, performs no actions.
function Assert-AictServiceGuest {
    $taskMachine=Get-CimInstance Win32_ComputerSystem
    if ($taskMachine.Manufacturer -notmatch 'VMware' -or $taskMachine.Model -notmatch 'VMware') { throw 'VMware guest required. No service, task or file changes performed.' }
    $taskIdentity=[Security.Principal.WindowsIdentity]::GetCurrent()
    $taskPrincipal=New-Object Security.Principal.WindowsPrincipal($taskIdentity)
    if (-not $taskPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) -or -not [Environment]::Is64BitProcess -or $PSVersionTable.PSEdition -ne 'Desktop' -or [Diagnostics.Process]::GetCurrentProcess().SessionId -eq 0) { throw 'Use administrator 64-bit Windows PowerShell 5.1 in the guest desktop.' }
}
function Read-AictServiceManifest {
    param([string]$Package)
    $taskManifest=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $Package 'MANIFEST.json') | ConvertFrom-Json
    if ($taskManifest.kind -cne 'AICT_SERVICE_BOOT_LAB_V1') { throw 'Wrong service lab package.' }
    foreach ($taskEntry in $taskManifest.files.PSObject.Properties) {
        if ((Get-FileHash -LiteralPath (Join-Path $Package $taskEntry.Name) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $taskEntry.Value) { throw ('Package hash mismatch: ' + $taskEntry.Name) }
    }
    return $taskManifest
}
function Set-AictLabOwner {
    param([string]$Path)
    $taskAcl=Get-Acl -LiteralPath $Path
    $taskAcl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
    Set-Acl -LiteralPath $Path -AclObject $taskAcl
}
function Write-AictLabJson {
    param([string]$Path,$Value)
    $taskBytes=(New-Object Text.UTF8Encoding($false)).GetBytes(($Value | ConvertTo-Json -Depth 20))
    $taskStream=New-Object IO.FileStream($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $taskStream.Write($taskBytes,0,$taskBytes.Length) } finally { $taskStream.Dispose() }
    Set-AictLabOwner -Path $Path
}
function Invoke-AictServiceNative {
    param($Context,[ValidateSet('--install','--inspect','--crash','--cleanup')][string]$Mode,[string]$Tag)
    $taskStart=New-Object Diagnostics.ProcessStartInfo
    $taskStart.FileName=Join-Path $Context.Root 'AictServiceLab.exe'; $taskStart.Arguments=$Mode
    $taskStart.UseShellExecute=$false; $taskStart.CreateNoWindow=$true
    $taskStart.RedirectStandardOutput=$true; $taskStart.RedirectStandardError=$true
    $taskStart.StandardOutputEncoding=New-Object Text.UTF8Encoding($false)
    $taskStart.StandardErrorEncoding=New-Object Text.UTF8Encoding($false)
    $taskProcess=[Diagnostics.Process]::Start($taskStart)
    $taskOut=$taskProcess.StandardOutput.ReadToEndAsync(); $taskErr=$taskProcess.StandardError.ReadToEndAsync()
    if (-not $taskProcess.WaitForExit(60000)) { $taskProcess.Kill(); $taskProcess.WaitForExit(); throw ('Native lab command timed out: ' + $Mode) }
    $taskExit=$taskProcess.ExitCode; $taskProcess.Dispose()
    $taskResult=[ordered]@{Mode=$Mode; ExitCode=$taskExit; Stdout=$taskOut.Result; Stderr=$taskErr.Result}
    if ($taskExit -ne 0) {
        Write-AictLabJson -Path (Join-Path $Context.Root ('native-failure-' + $Tag + '-' + [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '.json')) -Value $taskResult
        throw ('Native ' + $Mode + ' failed: ' + $taskErr.Result.Trim())
    }
    $taskJson=$taskOut.Result | ConvertFrom-Json
    if ($taskJson.RunId -cne $Context.RunId) { throw 'Native result run identity mismatch.' }
    if ($Tag) { Write-AictLabJson -Path (Join-Path $Context.Root ($Tag + '.json')) -Value $taskJson }
    return $taskJson
}
function New-AictServiceContext {
    param([string]$Package,[string]$RunId,$Manifest)
    if ($RunId -notmatch '^[a-f0-9]{32}$') { throw 'Invalid lab run ID.' }
    return [pscustomobject]@{RunId=$RunId; Package=$Package; Root=(Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) ('AICT-Service-'+$RunId)); CleanupTask=('AICT-Service-Cleanup-'+$RunId); Manifest=$Manifest}
}
function Get-AictServiceState {
    param($Context)
    $taskStream=New-Object IO.FileStream((Join-Path $Context.Root 'state\service-state.json'),[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    $taskReader=New-Object IO.StreamReader($taskStream,(New-Object Text.UTF8Encoding($false)))
    try { return $taskReader.ReadToEnd() | ConvertFrom-Json } finally { $taskReader.Dispose() }
}
function Assert-AictProbeFile {
    param([string]$Path)
    $taskItem=Get-Item -LiteralPath $Path
    if ($taskItem.PSIsContainer -or ($taskItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $taskItem.Length -gt 1048576) { throw 'Unexpected probe output type/size; refused privileged read.' }
    $taskOwner=(Get-Acl -LiteralPath $Path).GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($taskOwner -cne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value) { throw 'Probe output has an unexpected owner; refused privileged read/export.' }
}
function Invoke-AictLimitedServiceProbe {
    param($Context,[ValidateSet('before-crash','after-crash','after-boot')][string]$Phase)
    $taskName='AICT-Service-Probe-'+$Phase+'-'+$Context.RunId
    $taskBatch=Join-Path $Context.Root ('Run-probe-'+$Phase+'.cmd')
    $taskExe=Join-Path $Context.Root 'AictServiceLab.exe'
    $taskProbe=Join-Path $Context.Root 'probe'
    $taskExitPath=Join-Path $taskProbe ($Phase+'-exitcode.txt')
    $taskLines=@('@echo off',('"'+$taskExe+'" --probe '+$Phase+' 1>"'+(Join-Path $taskProbe ($Phase+'-stdout.txt'))+'" 2>"'+(Join-Path $taskProbe ($Phase+'-stderr.txt'))+'"'),'set "AICT_PROBE_EXIT=%ERRORLEVEL%"',('>"'+$taskExitPath+'" echo %AICT_PROBE_EXIT%'),'exit /b %AICT_PROBE_EXIT%')
    [IO.File]::WriteAllLines($taskBatch,[string[]]$taskLines,[Text.Encoding]::ASCII); Set-AictLabOwner -Path $taskBatch
    $taskSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $taskPrincipal=New-ScheduledTaskPrincipal -UserId $taskSid -LogonType Interactive -RunLevel Limited
    $taskAction=New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\cmd.exe" -Argument ('/d /c "'+$taskBatch+'"')
    $taskSettings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 1) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    $taskRegistered=$false
    try {
        $null=Register-ScheduledTask -TaskName $taskName -Action $taskAction -Principal $taskPrincipal -Settings $taskSettings -ErrorAction Stop; $taskRegistered=$true
        Start-ScheduledTask -TaskName $taskName -ErrorAction Stop
        $taskDeadline=[DateTimeOffset]::UtcNow.AddSeconds(35)
        while (-not (Test-Path -LiteralPath $taskExitPath) -and [DateTimeOffset]::UtcNow -lt $taskDeadline) { Start-Sleep -Milliseconds 200 }
        if (-not (Test-Path -LiteralPath $taskExitPath)) { throw 'Limited probe timed out; startup logs will be preserved.' }
        Assert-AictProbeFile -Path $taskExitPath
        if ([int](Get-Content -Raw -Encoding UTF8 -LiteralPath $taskExitPath) -ne 0) { throw ('Limited probe startup failed; see '+$Phase+'-stderr.txt') }
        $taskResultPath=Join-Path $taskProbe ('probe-'+$Phase+'.json')
        Assert-AictProbeFile -Path $taskResultPath
        $taskResult=Get-Content -Raw -Encoding UTF8 -LiteralPath $taskResultPath | ConvertFrom-Json
        if ($taskResult.RunId -cne $Context.RunId -or -not $taskResult.PermissionsPassed -or -not $taskResult.LimitedToken) { throw ('Permission probe failed: '+$Phase) }
    } finally {
        if ($taskRegistered) {
            if ((Get-ScheduledTask -TaskName $taskName).State -eq 'Running') { Stop-ScheduledTask -TaskName $taskName -ErrorAction Stop }
            Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction Stop
            if ($null -ne (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue)) { throw 'Probe task removal unconfirmed.' }
        }
    }
    return $taskResult
}
function Remove-AictServiceWatchdog {
    param($Context)
    $task=Get-ScheduledTask -TaskName $Context.CleanupTask -ErrorAction SilentlyContinue
    if ($null -eq $task) { return $true }
    $taskArgs='/d /c "'+(Join-Path $Context.Root 'Run-cleanup.cmd')+'"'
    if ($task.Principal.UserId -notin @('SYSTEM','S-1-5-18','NT AUTHORITY\SYSTEM') -or @($task.Actions).Count -ne 1 -or $task.Actions[0].Execute -ine "$env:SystemRoot\System32\cmd.exe" -or $task.Actions[0].Arguments -cne $taskArgs) { throw 'Cleanup task definition changed; left untouched.' }
    if ($task.State -eq 'Running') { Stop-ScheduledTask -TaskName $Context.CleanupTask -ErrorAction Stop }
    Unregister-ScheduledTask -TaskName $Context.CleanupTask -Confirm:$false -ErrorAction Stop
    return $null -eq (Get-ScheduledTask -TaskName $Context.CleanupTask -ErrorAction SilentlyContinue)
}
function Export-AictServiceEvidence {
    param($Context,$Status,[string]$Stage)
    $taskOutput=Join-Path $Context.Package ('service-lab-'+$Stage+'-'+[DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
    $null=New-Item -ItemType Directory -Path $taskOutput
    foreach ($taskFile in @(Get-ChildItem -LiteralPath $Context.Root -File | Where-Object { $_.Extension -in @('.json','.txt','.jsonl') -and $_.Name -ne 'config.json' })) { Copy-Item -LiteralPath $taskFile.FullName -Destination (Join-Path $taskOutput $taskFile.Name) }
    foreach ($taskPhase in @('before-crash','after-crash','after-boot')) {
        foreach ($taskName in @(('probe-'+$taskPhase+'.json'),($taskPhase+'-stdout.txt'),($taskPhase+'-stderr.txt'),($taskPhase+'-exitcode.txt'))) {
            $taskProbeFile=Join-Path (Join-Path $Context.Root 'probe') $taskName
            if (Test-Path -LiteralPath $taskProbeFile) {
                try {
                    Assert-AictProbeFile -Path $taskProbeFile
                    Copy-Item -LiteralPath $taskProbeFile -Destination (Join-Path $taskOutput ('probe-'+$taskName))
                } catch {
                    if (-not $Status.Contains('ProbeExportErrors')) { $Status.ProbeExportErrors=@() }
                    $Status.ProbeExportErrors+=('Refused/unavailable expected probe file: '+$taskName)
                }
            }
        }
    }
    try {
        $taskSnapshot=Get-AictServiceState -Context $Context
        [IO.File]::WriteAllText((Join-Path $taskOutput 'final-service-state.json'),($taskSnapshot | ConvertTo-Json -Depth 10),(New-Object Text.UTF8Encoding($false)))
    } catch { $Status.StateSnapshotUnavailable=$true }
    [IO.File]::WriteAllText((Join-Path $taskOutput 'runner-result.json'),($Status | ConvertTo-Json -Depth 15),(New-Object Text.UTF8Encoding($false)))
    $taskHashes=@(Get-ChildItem -LiteralPath $taskOutput -File | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()+'  '+$_.Name })
    [IO.File]::WriteAllLines((Join-Path $taskOutput 'SHA256SUMS.txt'),[string[]]$taskHashes,(New-Object Text.UTF8Encoding($false)))
    Write-Host ('Evidence directory: '+$taskOutput)
}
