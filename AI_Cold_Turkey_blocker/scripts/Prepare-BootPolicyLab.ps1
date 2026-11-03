#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][ValidateNotNullOrEmpty()][string]$SnapshotReference)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'BootPolicyLabCommon.ps1')
Assert-AictPolicyGuest
$taskPackage=Split-Path -Parent $PSScriptRoot
$taskManifest=Read-AictPolicyManifest -Package $taskPackage -ExpectedKind 'AICT_BOOT_POLICY_LAB_V1'
if(Test-Path -LiteralPath (Join-Path $taskPackage 'active-boot-policy.json')) { throw 'This extraction has a run already. Finish/recover it instead of repeating preparation.' }
if((Get-Service -Name AppIDSvc).Status -ne 'Running') { throw 'AppIDSvc is not running. No experiment installed; report the preflight result.' }
$taskContext=New-AictPolicyContext -Package $taskPackage -RunId ([guid]::NewGuid().ToString('N'))
Initialize-AictBootStage -Context $taskContext -Manifest $taskManifest
$taskVersion=Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$taskResult=[ordered]@{kind='AICT_BOOT_POLICY_LAB_RUNNER';schemaVersion=1;stage='Prepare';runId=$taskContext.RunId;collectedAtUtc=[DateTimeOffset]::UtcNow.ToString('o');editionId=$taskVersion.EditionID;build=([string]$taskVersion.CurrentBuildNumber+'.'+$taskVersion.UBR);vmwareGuestVerified=$true;snapshotReference=$SnapshotReference;repeatSnapshotRestoreRequired=$false;sourceManifestSha256=(Get-FileHash -LiteralPath (Join-Path $taskPackage 'MANIFEST.json') -Algorithm SHA256).Hash.ToLowerInvariant();serviceRuntimeUnchanged=$taskManifest.service_runtime_unchanged;fixedFixtureOnly=$true;baselinePassed=$false;preBootBlocked=$false;automaticStartArmed=$false;logonTaskRegistered=$false;preparePassed=$false;serviceLeftForReboot=$false;serviceAbsent=$false;ownPoliciesAbsent=$false;cleanupTaskRemoved=$false;windowTaskRemoved=$false;phase0PassAwarded=$false;exportRefusedFiles=@();error=$null;cleanupError=$null}
$taskLeave=$false
Write-Host 'Guest-only normal Windows reboot test. The already-tested service runtime is unchanged. No new snapshot restore is required.'
try {
    Register-AictBootCleanup -Context $taskContext
    $taskBoot=Invoke-AictPolicyNative -Context $taskContext -Mode '--boot-metadata' -Tag 'boot-before'
    $null=Invoke-AictPolicyProbe -Context $taskContext -Phase 'baseline';$taskResult.baselinePassed=$true
    Write-AictPolicyJson -Path (Join-Path $taskContext.Root 'baseline-passed.json') -Value @{runId=$taskContext.RunId;passed=$true}
    $null=Invoke-AictPolicyNative -Context $taskContext -Mode '--install' -Tag 'install-result'
    $null=Invoke-AictPolicyProbe -Context $taskContext -Phase 'blocked'
    $taskBefore=Read-AictPolicyStatus -Context $taskContext
    if($taskBefore.runId -cne $taskContext.RunId -or -not $taskBefore.clockTrusted -or -not $taskBefore.desiredBlocked -or $taskBefore.policyReadbackBlocked -ne $true -or $null -ne $taskBefore.fault -or $null -eq $taskBefore.pendingId) { throw 'Pre-reboot scheduler/policy state did not match.' }
    Write-AictPolicyJson -Path (Join-Path $taskContext.Root 'before-boot-state.json') -Value $taskBefore
    $taskArmed=Invoke-AictPolicyNative -Context $taskContext -Mode '--arm-boot' -Tag 'arm-result'
    $taskInspection=Invoke-AictPolicyNative -Context $taskContext -Mode '--inspect' -Tag 'inspect-before-boot'
    if(-not $taskArmed.automaticStartArmed -or -not $taskArmed.appIdDependencyArmed -or $taskInspection.startType -ne 2 -or $taskInspection.state -ne 4 -or @($taskInspection.dependencies).Count -ne 1 -or $taskInspection.dependencies[0] -ine 'AppIDSvc' -or -not $taskInspection.imageAndAccountMatch -or -not $taskInspection.serviceSecurityMatches) { throw 'Owned automatic-start/dependency/security readback did not match.' }
    $taskResult.preBootBlocked=$true;$taskResult.automaticStartArmed=$true
    Register-AictBootWindow -Context $taskContext;$taskResult.logonTaskRegistered=$true
    if(Test-Path -LiteralPath (Join-Path $taskContext.Root 'probe\boot-window.json')) { throw 'A logon probe ran before the requested reboot; preserve its result.' }
    $taskResult.preparePassed=$true;$taskResult.serviceLeftForReboot=$true
    Write-AictPolicyJson -Path (Join-Path $taskContext.Root 'boot-prepare-summary.json') -Value $taskResult
    $taskLeave=$true
} catch { $taskResult.error=$_.Exception.Message;Write-Host ('Preparation diagnostic: '+$taskResult.error) }
finally {
    if(-not $taskLeave) {
        try { $taskCleanup=Invoke-AictPolicyNative -Context $taskContext -Mode '--cleanup' -Tag ('cleanup-prepare-'+[guid]::NewGuid().ToString('N'));$taskResult.serviceAbsent=$taskCleanup.serviceAbsent;$taskResult.ownPoliciesAbsent=$taskCleanup.ownPoliciesAbsent
            if($taskResult.serviceAbsent -and $taskResult.ownPoliciesAbsent) { $taskResult.windowTaskRemoved=Remove-AictBootWindow -Context $taskContext;$taskResult.cleanupTaskRemoved=Remove-AictPolicyWatchdog -Context $taskContext }
        } catch { $taskResult.cleanupError=$_.Exception.Message }
    }
    Export-AictPolicyEvidence -Context $taskContext -Result $taskResult -Prefix 'boot-policy-prepare'
}
if(-not $taskResult.preparePassed) { throw 'Preparation did not pass. Preserve its evidence; do not reboot or repeat preparation blindly.' }
Write-Host 'Prepare passed. Use Windows Restart INSIDE THIS GUEST soon (within 5 minutes), then sign into the same guest account.'
Write-Host 'After login, run from the desktop: .\AICT-boot-policy-lab-v1\scripts\Finish-BootPolicyLab.ps1'
Write-Host 'Finish within 10 minutes after boot. Independent cleanup: preparation +30 minutes or boot +10 minutes. Keep the protected directory.'
