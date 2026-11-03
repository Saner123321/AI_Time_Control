#Requires -Version 5.1
# Read-only native probe shared by the guest collector and ABI regression test.
function Get-AictWfpSessionProbe {
    [CmdletBinding()]
    param()
    $taskWfp = [ordered]@{
        ServerNameEncoding = 'EXPLICIT_NULL_POINTER'
        Attempted = $false
        EngineOpenStatus = $null
        EngineCloseStatus = $null
        ErrorType = $null
        FiltersAddedOrRemoved = $false
        EnforcementTested = $false
    }
    $taskEngine = [IntPtr]::Zero
    try {
        if (-not ('AICTLab.ReadOnlyWfpV2' -as [type])) {
            Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace AICTLab {
    public static class ReadOnlyWfpV2 {
        // The Windows API requires NULL, not an empty server name. PowerShell 5.1
        // coerces $null to "" for string arguments; use IntPtr.Zero explicitly.
        [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)]
        public static extern uint FwpmEngineOpen0(IntPtr serverName, uint authnService,
            IntPtr authIdentity, IntPtr session, out IntPtr engineHandle);
        [DllImport("fwpuclnt.dll")]
        public static extern uint FwpmEngineClose0(IntPtr engineHandle);
    }
}
'@
        }
        $taskWfp.Attempted = $true
        $taskWfp.EngineOpenStatus = [AICTLab.ReadOnlyWfpV2]::FwpmEngineOpen0([IntPtr]::Zero, 10, [IntPtr]::Zero, [IntPtr]::Zero, [ref]$taskEngine)
    } catch { $taskWfp.ErrorType = $_.Exception.GetType().Name }
    finally {
        if ($taskEngine -ne [IntPtr]::Zero) {
            $taskWfp.EngineCloseStatus = [AICTLab.ReadOnlyWfpV2]::FwpmEngineClose0($taskEngine)
        }
    }
    [pscustomobject]$taskWfp
}
