// Copied documented SCM ABI from the separately verified service lab. Fixed names supplied by this integration worker.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Diagnostics;
using System.Threading;
using Microsoft.Win32.SafeHandles;
namespace AictServicePolicyLab {
    sealed class ServiceHandle:SafeHandleZeroOrMinusOneIsInvalid {
        public ServiceHandle():base(true) {}
        protected override bool ReleaseHandle(){return Native.CloseServiceHandle(handle);}
    }
    static class Native {
        internal const uint All=0xF01FF,QueryConfig=1,ChangeConfig=2,QueryStatus=4,Start=16,Stop=32,Custom=256,Delete=0x10000,ReadControl=0x20000,WriteDac=0x40000;
        [StructLayout(LayoutKind.Sequential)] internal struct Status {public uint Type,State,Accepted,Win32Exit,SpecificExit,Checkpoint,WaitHint,Pid,Flags;}
        [StructLayout(LayoutKind.Sequential)] internal struct ControlStatus {public uint Type,State,Accepted,Win32Exit,SpecificExit,Checkpoint,WaitHint;}
        [StructLayout(LayoutKind.Sequential)] internal struct Config {public uint Type,StartType,ErrorControl;public IntPtr Image,Group;public uint Tag;public IntPtr Dependencies,Account,Display;}
        [StructLayout(LayoutKind.Sequential)] internal struct Action {public uint Type,Delay;}
        [StructLayout(LayoutKind.Sequential)] internal struct Failure {public uint Reset;public IntPtr RebootMessage,Command;public uint Count;public IntPtr Actions;}
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true,ExactSpelling=true)] internal static extern ServiceHandle OpenSCManagerW(string machine,string database,uint access);
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true,ExactSpelling=true)] internal static extern ServiceHandle OpenServiceW(ServiceHandle scm,string name,uint access);
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true,ExactSpelling=true)] internal static extern ServiceHandle CreateServiceW(ServiceHandle scm,string name,string display,uint access,uint type,uint start,uint error,string image,string group,IntPtr tag,string dependencies,string account,string password);
        [DllImport("advapi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseServiceHandle(IntPtr service);
        [DllImport("advapi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryServiceStatusEx(ServiceHandle service,int level,out Status status,uint length,out uint needed);
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true,ExactSpelling=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryServiceConfigW(ServiceHandle service,IntPtr buffer,uint size,out uint needed);
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true,ExactSpelling=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryServiceConfig2W(ServiceHandle service,uint level,IntPtr buffer,uint size,out uint needed);
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true,ExactSpelling=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool ChangeServiceConfig2W(ServiceHandle service,uint level,ref Failure failure);
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true,ExactSpelling=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool ChangeServiceConfigW(ServiceHandle service,uint type,uint start,uint error,string image,string group,IntPtr tag,string dependencies,string account,string password,string display);
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true,ExactSpelling=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool StartServiceW(ServiceHandle service,uint count,IntPtr args);
        [DllImport("advapi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool ControlService(ServiceHandle service,uint control,out ControlStatus status);
        [DllImport("advapi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteService(ServiceHandle service);
        [DllImport("advapi32.dll",ExactSpelling=true)] internal static extern uint SetSecurityInfo(ServiceHandle service,uint objectType,uint info,[In]byte[] owner,[In]byte[] group,[In]byte[] dacl,IntPtr sacl);
        [DllImport("advapi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryServiceObjectSecurity(ServiceHandle service,uint info,byte[] descriptor,uint size,out uint needed);
        [DllImport("kernel32.dll",SetLastError=true)] internal static extern IntPtr OpenProcess(uint access,[MarshalAs(UnmanagedType.Bool)]bool inherit,uint pid);
        [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(IntPtr handle);
        internal static void Check(bool good,string operation){if(!good)throw new Win32Exception(Marshal.GetLastWin32Error(),operation);}
        internal static ServiceHandle Manager(uint rights=1){var h=OpenSCManagerW(null,null,rights);Check(!h.IsInvalid,"OpenSCManager");return h;}
        internal static ServiceHandle Open(ServiceHandle scm,uint rights){var h=OpenServiceW(scm,Program.Name,rights);Check(!h.IsInvalid,"OpenService");return h;}
        internal static Status GetStatus(ServiceHandle service){Status status;uint needed;Check(QueryServiceStatusEx(service,0,out status,(uint)Marshal.SizeOf(typeof(Status)),out needed),"QueryServiceStatusEx");return status;}
        internal static Dictionary<string,object> Definition(ServiceHandle service){
            uint size;QueryServiceConfigW(service,IntPtr.Zero,0,out size);Program.Require(size>=64 && size<65536,"Invalid config size");
            IntPtr data=Marshal.AllocHGlobal((int)size);
            try {Check(QueryServiceConfigW(service,data,size,out size),"QueryServiceConfig");var c=(Config)Marshal.PtrToStructure(data,typeof(Config));
                return Program.Obj("Type",c.Type,"StartType",c.StartType,"Image",Marshal.PtrToStringUni(c.Image),"Account",Marshal.PtrToStringUni(c.Account));
            }finally{Marshal.FreeHGlobal(data);}
        }
        internal static void ValidateOwned(ServiceHandle service){
            var c=Definition(service);Program.Require(Convert.ToUInt32(c["Type"])==16 && string.Equals((string)c["Image"],Program.Image,StringComparison.OrdinalIgnoreCase) && (string)c["Account"]=="LocalSystem","Service ownership/image/account mismatch; left untouched");
        }
        internal static bool Absent(ServiceHandle scm){using(var h=OpenServiceW(scm,Program.Name,QueryStatus)){if(!h.IsInvalid)return false;int e=Marshal.GetLastWin32Error();Program.Require(e==1060,"Service absence not established; code "+e);return true;}}
        internal static void WaitState(ServiceHandle service,uint expected,int seconds){var watch=Stopwatch.StartNew();while(watch.Elapsed.TotalSeconds<seconds){if(GetStatus(service).State==expected)return;Thread.Sleep(100);}throw new TimeoutException("Service state "+expected+" not reached");}
        internal static void SetRecovery(ServiceHandle service){
            IntPtr actions=Marshal.AllocHGlobal(24);
            try {for(int i=0;i<3;i++)Marshal.StructureToPtr(new Action{Type=i<2?1u:0u,Delay=i==0?5000u:i==1?2000u:0u},IntPtr.Add(actions,i*8),false);
                var f=new Failure{Reset=60,Count=3,Actions=actions};Check(ChangeServiceConfig2W(service,2,ref f),"ChangeServiceConfig2 restart actions");
            }finally{Marshal.FreeHGlobal(actions);}
        }
        internal static Dictionary<string,object> Recovery(ServiceHandle service){
            uint size;QueryServiceConfig2W(service,2,IntPtr.Zero,0,out size);Program.Require(size>=40 && size<65536,"Invalid recovery config size");
            IntPtr data=Marshal.AllocHGlobal((int)size);
            try {Check(QueryServiceConfig2W(service,2,data,size,out size),"QueryServiceConfig2");var f=(Failure)Marshal.PtrToStructure(data,typeof(Failure));
                Program.Require(f.Count<=8,"Too many recovery actions");var actions=new List<Dictionary<string,object>>();
                long offset=f.Actions.ToInt64()-data.ToInt64();Program.Require(f.Count==0 || (offset>=0 && offset+8*f.Count<=size),"Recovery actions outside buffer");
                for(int i=0;i<f.Count;i++){var a=(Action)Marshal.PtrToStructure(IntPtr.Add(f.Actions,i*8),typeof(Action));actions.Add(Program.Obj("Type",a.Type,"DelayMs",a.Delay));}
                bool safe= f.Reset==60 && actions.Count==3 && actions.Select((a,i)=>Convert.ToUInt32(a["Type"])==(i<2?1u:0u) && Convert.ToUInt32(a["DelayMs"])==(i==0?5000u:i==1?2000u:0u)).All(v=>v) && string.IsNullOrEmpty(Marshal.PtrToStringUni(f.Command)) && string.IsNullOrEmpty(Marshal.PtrToStringUni(f.RebootMessage));
                return Program.Obj("MatchesFixedRecovery",safe,"ResetSeconds",f.Reset,"Actions",actions);
            }finally{Marshal.FreeHGlobal(data);}
        }
        internal static RawSecurityDescriptor Security(ServiceHandle service){
            uint size;QueryServiceObjectSecurity(service,7,null,0,out size);Program.Require(size>0 && size<65536,"Invalid service security size");
            byte[] data=new byte[size];Check(QueryServiceObjectSecurity(service,7,data,size,out size),"QueryServiceObjectSecurity");return new RawSecurityDescriptor(data,0);
        }
        internal static void SetSecurity(ServiceHandle service,string sid){
            var descriptor=Program.Descriptor(sid);
            byte[] owner=new byte[descriptor.Owner.BinaryLength],group=new byte[descriptor.Group.BinaryLength],dacl=new byte[descriptor.DiscretionaryAcl.BinaryLength];
            descriptor.Owner.GetBinaryForm(owner,0);descriptor.Group.GetBinaryForm(group,0);descriptor.DiscretionaryAcl.GetBinaryForm(dacl,0);
            // SE_SERVICE=2; set owner, group, DACL and request protected DACL explicitly.
            uint code=SetSecurityInfo(service,2,0x80000007,owner,group,dacl,IntPtr.Zero);
            if(code!=0)throw new Win32Exception((int)code,"SetSecurityInfo exact owned lab service");
        }
    }
}
