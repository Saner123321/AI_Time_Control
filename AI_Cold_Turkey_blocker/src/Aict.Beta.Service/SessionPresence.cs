using System.Runtime.InteropServices;
using System.Security.Principal;
namespace Aict.Beta.Service;
internal static class SessionPresence {
    [StructLayout(LayoutKind.Sequential)] struct Session {public int Id;public IntPtr Name;public int State;}
    [DllImport("wtsapi32.dll",CharSet=CharSet.Unicode,SetLastError=true,ExactSpelling=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool WTSEnumerateSessionsW(IntPtr server,int reserved,int version,out IntPtr sessions,out int count);
    [DllImport("wtsapi32.dll",CharSet=CharSet.Unicode,SetLastError=true,ExactSpelling=true)] [return:MarshalAs(UnmanagedType.Bool)] static extern bool WTSQuerySessionInformationW(IntPtr server,int session,int info,out IntPtr value,out int size);
    [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr value);
    static string Read(int session,int kind){if(!WTSQuerySessionInformationW(IntPtr.Zero,session,kind,out var p,out _))throw new IOException("Session identity unavailable");try{return Marshal.PtrToStringUni(p)??"";}finally{WTSFreeMemory(p);}}
    internal static bool ManagedUserActive(){if(!WTSEnumerateSessionsW(IntPtr.Zero,0,1,out var p,out int count))return true;try{if(count<0 || count>256)return true;for(int i=0;i<count;i++){var s=Marshal.PtrToStructure<Session>(IntPtr.Add(p,i*Marshal.SizeOf<Session>()));if(s.State!=0)continue;string user=Read(s.Id,5);if(user.Length==0)continue;string domain=Read(s.Id,7);var sid=(SecurityIdentifier)new NTAccount(domain,user).Translate(typeof(SecurityIdentifier));if(sid.Value==Env.Config.ProbeSid)return true;}return false;}catch{return true;}finally{WTSFreeMemory(p);}}
}
