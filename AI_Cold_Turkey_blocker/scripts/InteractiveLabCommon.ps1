#Requires -Version 5.1
. (Join-Path $PSScriptRoot 'ServicePolicyLabCommon.ps1')
function New-AictInteractiveContext {
    param([string]$Package,[string]$RunId)
    if($RunId -notmatch '^[a-f0-9]{32}$') { throw 'Invalid interactive run ID.' }
    return [pscustomobject]@{Package=$Package;RunId=$RunId;Root=(Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) ('AICT-Interactive-'+$RunId));TaskName=('AICT-Interactive-Cleanup-'+$RunId);BridgeFileName='AictInteractiveBridge.exe';UiTaskName=('AICT-Interactive-UI-'+$RunId)}
}
function Read-AictInteractiveContext {
    param([string]$Package,$Manifest)
    $taskLocator=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $Package 'active-interactive.json') | ConvertFrom-Json
    $taskContext=New-AictInteractiveContext -Package $Package -RunId $taskLocator.runId
    if((Get-Item -LiteralPath $taskContext.Root).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse root refused.' }
    if((Get-FileHash -LiteralPath (Join-Path $taskContext.Root 'AictInteractiveBridge.exe') -Algorithm SHA256).Hash.ToLowerInvariant() -cne $Manifest.files.'lab/AictInteractiveBridge.exe') { throw 'Protected bridge hash changed; do not execute it.' }
    return $taskContext
}
function Export-AictInteractiveEvidence {
    param($Context,$Result)
    $taskOutput=Join-Path $Context.Package ('interactive-session-'+[DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'));$null=New-Item -ItemType Directory -Path $taskOutput
    foreach($taskName in @('install-result.json','inspect-result.json','cleanup-result.json','watchdog-stdout.txt','watchdog-stderr.txt')) {
        $taskSource=Join-Path $Context.Root $taskName;if(Test-Path -LiteralPath $taskSource) { Copy-Item -LiteralPath $taskSource -Destination (Join-Path $taskOutput $taskName) }
    }
    foreach($taskFile in @(Get-ChildItem -LiteralPath $Context.Root -File -Filter '*.json' | Where-Object { $_.Name -notin @('config.json','connection.json','install-result.json','inspect-result.json','cleanup-result.json') })) { Copy-Item -LiteralPath $taskFile.FullName -Destination (Join-Path $taskOutput $taskFile.Name) }
    foreach($taskName in @('workspace-status.json','workspace-fault.json','process-actions.jsonl')) {
        $taskSource=Join-Path $Context.Root ('state\'+$taskName)
        if(Test-Path -LiteralPath $taskSource) {
            $taskStream=New-Object IO.FileStream($taskSource,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete));$taskReader=New-Object IO.StreamReader($taskStream,(New-Object Text.UTF8Encoding($false)))
            try { [IO.File]::WriteAllText((Join-Path $taskOutput $taskName),$taskReader.ReadToEnd(),(New-Object Text.UTF8Encoding($false))) } finally { $taskReader.Dispose() }
        }
    }
    $taskSmoke=Join-Path $Context.Root 'probe\workspace-smoke.json'
    if(Test-Path -LiteralPath $taskSmoke) { Assert-AictPolicyProbeFile -Path $taskSmoke;Copy-Item -LiteralPath $taskSmoke -Destination (Join-Path $taskOutput 'workspace-smoke.json') }
    Write-AictPolicyJson -Path (Join-Path $taskOutput 'session-result.json') -Value $Result
    $taskHashes=@(Get-ChildItem -LiteralPath $taskOutput -File | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()+'  '+$_.Name })
    [IO.File]::WriteAllLines((Join-Path $taskOutput 'SHA256SUMS.txt'),[string[]]$taskHashes,(New-Object Text.UTF8Encoding($false)))
    Write-Host ('Evidence directory: '+$taskOutput)
}
function Remove-AictInteractiveUiTask {
    param($Context)
    $task=Get-ScheduledTask -TaskName $Context.UiTaskName -ErrorAction SilentlyContinue;if($null -eq $task) { return $true }
    $taskExpected='--vm-service "'+(Join-Path $Context.Root 'connection.json')+'"'
    $taskConfig=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $Context.Root 'config.json') | ConvertFrom-Json
    if(@($task.Actions).Count -ne 1 -or $task.Actions[0].Execute -ine (Join-Path $Context.Root 'App\Aict.Desktop.exe') -or $task.Actions[0].Arguments -cne $taskExpected -or $task.Principal.UserId -cne $taskConfig.probeSid) { throw 'UI task changed; left untouched.' }
    # Unregister without ending any UI or other process. The window reports disconnected after service cleanup.
    Unregister-ScheduledTask -TaskName $Context.UiTaskName -Confirm:$false -ErrorAction Stop
    return $null -eq (Get-ScheduledTask -TaskName $Context.UiTaskName -ErrorAction SilentlyContinue)
}
