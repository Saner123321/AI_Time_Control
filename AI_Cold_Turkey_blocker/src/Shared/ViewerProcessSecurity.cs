using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;
namespace Aict.ProcessGuard;
// Only the supplied product executable is eligible. Existing administrator and
// SYSTEM control is retained; ordinary fresh PROCESS_TERMINATE opens are denied.
internal static class ViewerProcessSecurity {
    [DllImport("kernel32.dll",SetLastError=true)] internal static extern IntPtr OpenProcess(uint rights,bool inherit,uint pid);
    [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,EntryPoint="QueryFullProcessImageNameW")] static extern bool QueryImage(IntPtr handle,uint flags,StringBuilder path,ref uint size);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetProcessTimes(IntPtr handle,out long created,out long exited,out long kernel,out long user);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool SetKernelObjectSecurity(IntPtr handle,uint info,byte[] descriptor);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool GetKernelObjectSecurity(IntPtr handle,uint info,byte[]? descriptor,uint size,out uint needed);
    static readonly Dictionary<uint,long> hardened=new();
    internal static bool Protect(uint pid,string expectedImage,string sid,out bool processProtected){
        processProtected=false;IntPtr handle=OpenProcess(0x61000,false,pid);if(handle==IntPtr.Zero)return false;
        try{
            uint length=32768;var path=new StringBuilder((int)length);if(!QueryImage(handle,0,path,ref length)||!Path.GetFullPath(path.ToString()).Equals(Path.GetFullPath(expectedImage),StringComparison.OrdinalIgnoreCase))return false;
            if(!GetProcessTimes(handle,out long created,out _,out _,out _))return true;
            lock(hardened){if(hardened.TryGetValue(pid,out long known)&&known==created){processProtected=true;return true;}
                var descriptor=new RawSecurityDescriptor("D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x00101000;;;"+sid+")");var bytes=new byte[descriptor.BinaryLength];descriptor.GetBinaryForm(bytes,0);
                if(!SetKernelObjectSecurity(handle,4,bytes))return true;
                GetKernelObjectSecurity(handle,4,null,0,out uint needed);if(needed==0||needed>65536)return true;var actual=new byte[needed];if(!GetKernelObjectSecurity(handle,4,actual,needed,out _))return true;
                var readback=new RawSecurityDescriptor(actual,0);if(readback.DiscretionaryAcl is not {Count:3} acl)return true;
                foreach(GenericAce entry in acl){if(entry is not CommonAce ace||ace.AceQualifier!=AceQualifier.AccessAllowed)return true;string who=ace.SecurityIdentifier.Value;if(who==sid){if((ace.AccessMask&1)!=0)return true;}else if(who is not ("S-1-5-18" or "S-1-5-32-544"))return true;}
                if(hardened.Count>256)hardened.Clear();hardened[pid]=created;processProtected=true;
            }return true;
        }finally{CloseHandle(handle);}
    }
}
