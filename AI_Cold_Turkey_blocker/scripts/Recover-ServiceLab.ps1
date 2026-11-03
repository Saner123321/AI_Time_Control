#Requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'ServiceLabCommon.ps1')
Assert-AictServiceGuest
$taskPackage=Split-Path -Parent $PSScriptRoot
$taskManifest=Read-AictServiceManifest -Package $taskPackage
$taskLocator=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskPackage 'ACTIVE-SERVICE-LAB.json') | ConvertFrom-Json
$taskContext=New-AictServiceContext -Package $taskPackage -RunId $taskLocator.RunId -Manifest $taskManifest
if ($taskLocator.ProtectedLabDirectory -ine $taskContext.Root -or ((Get-Item -LiteralPath $taskContext.Root).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Unexpected recovery root.' }
if ((Get-FileHash -LiteralPath (Join-Path $taskContext.Root 'AictServiceLab.exe') -Algorithm SHA256).Hash.ToLowerInvariant() -cne $taskManifest.files.'lab/AictServiceLab.exe') { throw 'Recovery executable changed; left unexecuted.' }
$taskStatus=[ordered]@{SchemaVersion=1;Kind='AICT_SERVICE_LAB_RECOVERY_RUNNER';RunId=$taskContext.RunId;ServiceAbsent=$false;CleanupTaskRemoved=$false;Error=$null;NoBlockingPolicyApplied=$true}
try {
    $taskCleanup=Invoke-AictServiceNative -Context $taskContext -Mode '--cleanup' -Tag ('cleanup-manual-'+[DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
    $taskStatus.ServiceAbsent=$taskCleanup.ServiceAbsent
    if ($taskCleanup.ServiceAbsent) { $taskStatus.CleanupTaskRemoved=Remove-AictServiceWatchdog -Context $taskContext }
} catch { $taskStatus.Error=$_.Exception.Message }
finally { Export-AictServiceEvidence -Context $taskContext -Status $taskStatus -Stage 'recovery' }
if (-not $taskStatus.ServiceAbsent -or -not $taskStatus.CleanupTaskRemoved) { throw 'Exact lab recovery remains unconfirmed; preserve evidence and the independent cleanup task.' }
Write-Host 'Only this owned test service and its cleanup task were removed. No test pass was awarded.'
