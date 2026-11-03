#Requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'InteractiveLabCommon.ps1')
Assert-AictPolicyGuest
$taskPackage=Split-Path -Parent $PSScriptRoot;$taskManifest=Read-AictPolicyManifest -Package $taskPackage -ExpectedKind 'AICT_INTERACTIVE_LAB_V1'
$taskContext=Read-AictInteractiveContext -Package $taskPackage -Manifest $taskManifest
$taskResult=[ordered]@{kind='AICT_INTERACTIVE_SESSION';stage='Collect';runId=$taskContext.RunId;sourceManifestSha256=(Get-FileHash -LiteralPath (Join-Path $taskPackage 'MANIFEST.json') -Algorithm SHA256).Hash.ToLowerInvariant();serviceState=$null;serviceSecurityMatches=$null;manualResistanceChecksNotAutomaticallyAwarded=$true;exportRefusedFiles=@();error=$null}
try {
    $taskInspection=Invoke-AictPolicyNative -Context $taskContext -Mode '--inspect'
    Write-AictPolicyJson -Path (Join-Path $taskContext.Root ('inspect-'+[DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')+'.json')) -Value $taskInspection
    $taskResult.serviceState=$taskInspection.state;$taskResult.serviceSecurityMatches=$taskInspection.serviceSecurityMatches
} catch { $taskResult.error=$_.Exception.Message }
finally { Export-AictInteractiveEvidence -Context $taskContext -Result $taskResult }
if($null -ne $taskResult.error) { Write-Warning 'Current service inspection failed. Existing state and the error were exported; no rules were reset.' }
