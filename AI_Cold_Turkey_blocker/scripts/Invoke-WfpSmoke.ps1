#Requires -Version 5.1
<#
.SYNOPSIS
Run a transient, target-only WFP experiment inside a snapshotted VMware guest.
#>
[CmdletBinding()]
param([Parameter(Mandatory=$true)][ValidateNotNullOrEmpty()][string]$SnapshotReference)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($SnapshotReference.Length -gt 120 -or $SnapshotReference -match '["\r\n]' -or [string]::IsNullOrWhiteSpace($SnapshotReference)) {
    throw 'Provide the actual snapshot name without quotation marks or newlines.'
}
$taskComputer = Get-CimInstance Win32_ComputerSystem
if ($taskComputer.Manufacturer -notmatch 'VMware' -or $taskComputer.Model -notmatch 'VMware') {
    throw 'VMware guest required. No filters added. Do not run this experiment on the host.'
}
$taskPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $taskPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Open 64-bit Windows PowerShell as administrator INSIDE the guest, then run again.'
}
$taskPackageRoot = Split-Path -Parent $PSScriptRoot
$taskManifest = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $taskPackageRoot 'MANIFEST.json') | ConvertFrom-Json
$taskExecutable = Join-Path $taskPackageRoot 'lab\AictWfpSmoke.exe'
$taskActualHash = (Get-FileHash -LiteralPath $taskExecutable -Algorithm SHA256).Hash.ToLowerInvariant()
if ($taskManifest.files.'lab/AictWfpSmoke.exe' -ne $taskActualHash) { throw 'Executable hash mismatch; use a freshly extracted package.' }
Write-Host 'Temporary guest experiment: only its own generated test executable will be blocked.'
Write-Host 'Other applications are not targets. No persistent or boot-time filters are created.'
$taskBefore = @(Get-ChildItem -LiteralPath (Join-Path $taskPackageRoot 'lab') -Directory -Filter 'wfp-smoke-*' | Select-Object -ExpandProperty FullName)
& $taskExecutable --smoke --snapshot-reference $SnapshotReference
$taskResultCode = $LASTEXITCODE
$taskNewDirectories = @(Get-ChildItem -LiteralPath (Join-Path $taskPackageRoot 'lab') -Directory -Filter 'wfp-smoke-*' | Where-Object { $_.FullName -notin $taskBefore })
if ($taskNewDirectories.Count -eq 1) {
    $taskResultDirectory = $taskNewDirectories[0].FullName
    $taskResultPath = Join-Path $taskResultDirectory 'result.json'
    if (Test-Path -LiteralPath $taskResultPath) {
        $taskResult = Get-Content -Raw -Encoding UTF8 -LiteralPath $taskResultPath | ConvertFrom-Json
        Write-Host '--- Experiment diagnosis ---'
        foreach ($taskField in @('error', 'cleanup_error')) {
            if ($taskResult.PSObject.Properties[$taskField]) { Write-Host ($taskField + ': ' + $taskResult.$taskField) }
        }
        if ($taskResult.PSObject.Properties['cleanup']) { Write-Host ('Own temporary objects absent: ' + $taskResult.cleanup.objects_absent) }
        if ($taskResult.PSObject.Properties['active_filter_verification']) { Write-Host ('Active filter scope verified: ' + $taskResult.active_filter_verification.verified) }
        foreach ($taskTrial in @($taskResult.connections | Where-Object { $_.phase -eq 'blocked-target' })) {
            Write-Host ($taskTrial.protocol + ' trial ' + $taskTrial.repeat + ': socket=' + $taskTrial.socket_error + ', stage=' + $taskTrial.error_stage + ', receiver-bytes=' + $taskTrial.server_received_bytes + ', receiver-error=' + $taskTrial.server_error)
        }
        $taskSums = @('result.json', 'AictProbeTarget.exe', 'AictProbeControl.exe') | ForEach-Object {
            $taskItemHash = Get-FileHash -LiteralPath (Join-Path $taskResultDirectory $_) -Algorithm SHA256
            $taskItemHash.Hash.ToLowerInvariant() + '  ' + $_
        }
        [IO.File]::WriteAllLines((Join-Path $taskResultDirectory 'SHA256SUMS.txt'), [string[]]$taskSums, (New-Object Text.UTF8Encoding($false)))
    }
}
Write-Host 'Copy the new lab\wfp-smoke-TIMESTAMP directory back to the project reports directory.'
if ($taskResultCode -ne 0) { throw ('Transient experiment did not pass. Preserve all evidence. Exit code: ' + $taskResultCode) }
