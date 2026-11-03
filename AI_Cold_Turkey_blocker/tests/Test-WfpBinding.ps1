#Requires -Version 5.1
# Read-only ABI regression test. Opens and closes engine handles, never adds filters.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class AICTReadOnlyProbeComparison {
    [DllImport("fwpuclnt.dll", EntryPoint="FwpmEngineOpen0", CharSet=CharSet.Unicode)]
    public static extern uint OpenString(string name, uint auth, IntPtr identity, IntPtr session, out IntPtr handle);
    [DllImport("fwpuclnt.dll")]
    public static extern uint FwpmEngineClose0(IntPtr handle);
}
'@
$taskOldHandle = [IntPtr]::Zero
$taskOldClose = $null
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts\Read-WfpSession.ps1')
try {
    $taskOldStatus = [AICTReadOnlyProbeComparison]::OpenString($null, 10, [IntPtr]::Zero, [IntPtr]::Zero, [ref]$taskOldHandle)
    $taskProductionProbe = Get-AictWfpSessionProbe
} finally {
    if ($taskOldHandle -ne [IntPtr]::Zero) { $taskOldClose = [AICTReadOnlyProbeComparison]::FwpmEngineClose0($taskOldHandle) }
}
[ordered]@{
    Context = 'READ_ONLY_ABI_REGRESSION_NOT_ENFORCEMENT'
    CollectedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    PSVersion = $PSVersionTable.PSVersion.ToString()
    StringNullOpenStatus = $taskOldStatus
    PointerNullOpenStatus = $taskProductionProbe.EngineOpenStatus
    StringCloseStatus = $taskOldClose
    PointerCloseStatus = $taskProductionProbe.EngineCloseStatus
    PolicyChangesMade = $false
} | ConvertTo-Json
if ($taskOldStatus -ne 50 -or $taskProductionProbe.EngineOpenStatus -ne 0 -or $taskProductionProbe.EngineCloseStatus -ne 0) {
    throw 'Unexpected binding regression result; inspect statuses, do not infer enforcement support.'
}
