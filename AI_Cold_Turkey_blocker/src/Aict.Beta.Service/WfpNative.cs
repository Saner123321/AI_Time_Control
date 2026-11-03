using System;
using System.Runtime.InteropServices;
namespace Aict.Beta.Service.Wfp {
    // Windows SDK FWP_DATA_TYPE values. 13 is a SID pointer; 14 is an SD byte blob.
    enum DataType:uint {ByteBlob=12,Sid=13,SecurityDescriptor=14}
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    struct Display { [MarshalAs(UnmanagedType.LPWStr)] public string? name; [MarshalAs(UnmanagedType.LPWStr)] public string? description; }
    [StructLayout(LayoutKind.Sequential)] struct Blob { public uint size; public IntPtr data; }
    [StructLayout(LayoutKind.Explicit, Size=16)] struct Value { [FieldOffset(0)] public uint type; [FieldOffset(8)] public IntPtr data; }
    [StructLayout(LayoutKind.Explicit, Size=16)] struct Context { [FieldOffset(0)] public ulong raw; [FieldOffset(0)] public Guid key; }
    [StructLayout(LayoutKind.Sequential)] struct Action { public uint type; public Guid key; }
    [StructLayout(LayoutKind.Sequential)] struct Condition { public Guid field; public uint match; public Value value; }
    [StructLayout(LayoutKind.Sequential)] struct Session { public Guid key; public Display display; public uint flags, timeout, processId; public IntPtr sid, username; public int kernelMode; }
    [StructLayout(LayoutKind.Sequential)] struct Provider { public Guid key; public Display display; public uint flags; public Blob data; public IntPtr serviceName; }
    [StructLayout(LayoutKind.Sequential)] struct Sublayer { public Guid key; public Display display; public uint flags; public IntPtr provider; public Blob data; public ushort weight; }
    [StructLayout(LayoutKind.Sequential)] struct Filter {
        public Guid key; public Display display; public uint flags; public IntPtr provider; public Blob providerData;
        public Guid layer, sublayer; public Value weight; public uint conditionCount; public IntPtr conditions;
        public Action action; public Context context; public IntPtr reserved; public ulong id; public Value effectiveWeight;
    }
    static class Native {
        [DllImport("fwpuclnt.dll")] internal static extern uint FwpmFilterGetByKey0(IntPtr engine,ref Guid key,out IntPtr filter);
        [DllImport("fwpuclnt.dll")] internal static extern uint FwpmFilterDeleteByKey0(IntPtr engine,ref Guid key);
        [DllImport("fwpuclnt.dll")] internal static extern uint FwpmSubLayerDeleteByKey0(IntPtr engine,ref Guid key);
        [DllImport("fwpuclnt.dll")] internal static extern uint FwpmProviderDeleteByKey0(IntPtr engine,ref Guid key);
        [DllImport("fwpuclnt.dll",CharSet=CharSet.Unicode)] internal static extern uint FwpmEngineOpen0(IntPtr server,uint auth,IntPtr identity,ref Session session,out IntPtr engine);
        [DllImport("fwpuclnt.dll")] internal static extern uint FwpmEngineClose0(IntPtr engine);
        [DllImport("fwpuclnt.dll")] internal static extern uint FwpmTransactionBegin0(IntPtr engine,uint flags);
        [DllImport("fwpuclnt.dll")] internal static extern uint FwpmTransactionCommit0(IntPtr engine);
        [DllImport("fwpuclnt.dll")] internal static extern uint FwpmTransactionAbort0(IntPtr engine);
        [DllImport("fwpuclnt.dll",CharSet=CharSet.Unicode)] internal static extern uint FwpmProviderAdd0(IntPtr engine,ref Provider provider,IntPtr security);
        [DllImport("fwpuclnt.dll",CharSet=CharSet.Unicode)] internal static extern uint FwpmSubLayerAdd0(IntPtr engine,ref Sublayer sublayer,IntPtr security);
        [DllImport("fwpuclnt.dll",CharSet=CharSet.Unicode)] internal static extern uint FwpmFilterAdd0(IntPtr engine,ref Filter filter,IntPtr security,out ulong id);
        [DllImport("fwpuclnt.dll",CharSet=CharSet.Unicode)] internal static extern uint FwpmGetAppIdFromFileName0(string path,out IntPtr blob);
        [DllImport("fwpuclnt.dll")] internal static extern uint FwpmFilterGetById0(IntPtr engine,ulong id,out IntPtr filter);
        [DllImport("fwpuclnt.dll")] internal static extern uint FwpmProviderGetByKey0(IntPtr engine,ref Guid key,out IntPtr provider);
        [DllImport("fwpuclnt.dll")] internal static extern uint FwpmSubLayerGetByKey0(IntPtr engine,ref Guid key,out IntPtr sublayer);
        [DllImport("fwpuclnt.dll")] internal static extern void FwpmFreeMemory0(ref IntPtr value);
    }

}
