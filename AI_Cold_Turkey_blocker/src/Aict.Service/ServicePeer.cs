using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Aict.Service;

internal static class ServicePeer {
    [StructLayout(LayoutKind.Sequential)] struct Status {public uint Type,State,Accepted,Win32Exit,SpecificExit,Checkpoint,WaitHint,Pid,Flags;}
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe,out uint pid);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true,ExactSpelling=true)] static extern IntPtr OpenSCManagerW(string? machine,string? database,uint rights);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true,ExactSpelling=true)] static extern IntPtr OpenServiceW(IntPtr manager,string name,uint rights);
    [DllImport("advapi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool QueryServiceStatusEx(IntPtr service,int level,out Status status,uint length,out uint needed);
    [DllImport("advapi32.dll")] [return:MarshalAs(UnmanagedType.Bool)] static extern bool CloseServiceHandle(IntPtr handle);
    internal static void Verify(NamedPipeClientStream pipe,string serviceName) {
        if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle,out uint pid)) throw new Win32Exception(Marshal.GetLastWin32Error());
        IntPtr manager=OpenSCManagerW(null,null,1);if(manager==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try {
            IntPtr service=OpenServiceW(manager,serviceName,4);if(service==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try {
                if(!QueryServiceStatusEx(service,0,out var status,(uint)Marshal.SizeOf<Status>(),out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if(status.State!=4 || status.Pid==0 || status.Pid!=pid) throw new InvalidDataException("Pipe server does not match the actual registered service process");
            } finally {CloseServiceHandle(service);}
        } finally {CloseServiceHandle(manager);}
    }
}
