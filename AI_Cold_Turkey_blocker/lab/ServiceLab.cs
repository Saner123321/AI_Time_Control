// Phase-0 service-only experiment. No AppLocker, WFP, browser or user-app changes.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;
using Microsoft.Win32;
using TimeoutException=System.TimeoutException;

namespace AictServiceLab {
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
            try {for(int i=0;i<3;i++)Marshal.StructureToPtr(new Action{Type=i<2?1u:0u,Delay=i==0?1000u:i==1?2000u:0u},IntPtr.Add(actions,i*8),false);
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
                bool safe= f.Reset==60 && actions.Count==3 && actions.Select((a,i)=>Convert.ToUInt32(a["Type"])==(i<2?1u:0u) && Convert.ToUInt32(a["DelayMs"])==(i==0?1000u:i==1?2000u:0u)).All(v=>v) && string.IsNullOrEmpty(Marshal.PtrToStringUni(f.Command)) && string.IsNullOrEmpty(Marshal.PtrToStringUni(f.RebootMessage));
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
    sealed class HeartbeatService:ServiceBase {
        readonly object gate=new object();Timer timer;Dictionary<string,object> state;bool stopping;
        internal HeartbeatService(){ServiceName=Program.Name;CanStop=true;CanShutdown=true;AutoLog=false;}
        protected override void OnStart(string[] args){
            try {
                RequestAdditionalTime(30000);
                long count=0,total=0;
                if(File.Exists(Program.StatePath)){var previous=Program.Read(Program.StatePath);Program.Require((string)previous["RunId"]==Program.RunId,"State ownership mismatch");count=Convert.ToInt64(previous["PersistentStartCount"]);total=Convert.ToInt64(previous["PersistentHeartbeatCount"]);}
                state=Program.Obj("SchemaVersion",1,"Kind","AICT_SERVICE_LAB_HEARTBEAT","RunId",Program.RunId,"InstanceId",Guid.NewGuid().ToString("N"),"ProcessId",Process.GetCurrentProcess().Id,
                    "BootTimeUtc",Program.BootTime(),"ProcessStartedUtc",DateTimeOffset.UtcNow.ToString("o"),"LocalSystem",WindowsIdentity.GetCurrent().User.Value=="S-1-5-18",
                    "PersistentStartCount",count+1,"PersistentHeartbeatCount",total,"HeartbeatSequence",0L,"PersistenceFailures",0L,"ServicePhase","Running","LastHeartbeatUtc",DateTimeOffset.UtcNow.ToString("o"));
                Program.Require((bool)state["LocalSystem"],"Service must run under LocalSystem");Program.Save(Program.StatePath,state,true);
                timer=new Timer(Tick,null,1000,1000);
            }catch(Exception error){Program.Save(Program.Join("service-start-error-"+DateTime.UtcNow.Ticks+".json"),Program.Error(error),false);throw;}
        }
        void Tick(object unused){lock(gate){if(stopping)return;state["HeartbeatSequence"]=Convert.ToInt64(state["HeartbeatSequence"])+1;state["PersistentHeartbeatCount"]=Convert.ToInt64(state["PersistentHeartbeatCount"])+1;state["LastHeartbeatUtc"]=DateTimeOffset.UtcNow.ToString("o");
            try{Program.Save(Program.StatePath,state,true);}catch(IOException){state["PersistenceFailures"]=Convert.ToInt64(state["PersistenceFailures"])+1;}
        }}
        protected override void OnStop(){lock(gate){stopping=true;if(timer!=null)timer.Dispose();if(state!=null){state["ServicePhase"]="Stopped";state["LastHeartbeatUtc"]=DateTimeOffset.UtcNow.ToString("o");Program.Save(Program.StatePath,state,true);}}}
        protected override void OnShutdown(){OnStop();}
        protected override void OnCustomCommand(int command){
            Program.Require(command==128,"Only the fixed one-time lab crash command exists");
            Program.Save(Program.Join("intentional-crash.json"),Program.Obj("RunId",Program.RunId,"ProcessId",Process.GetCurrentProcess().Id,"AtUtc",DateTimeOffset.UtcNow.ToString("o")),false);
            ThreadPool.QueueUserWorkItem(delegate {Thread.Sleep(150);Process.GetCurrentProcess().Kill();});
        }
    }
    static class Program {
        internal static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=1048576};
        internal static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
        internal static string Root,RunId,ProbeSid;
        internal static string Name {get{return "AICT-Lab-"+RunId;}}
        internal static string Image {get{return "\""+Assembly.GetExecutingAssembly().Location+"\" --service";}}
        internal static string StatePath {get{return Join(@"state\service-state.json");}}
        internal static string Join(string name){return Path.Combine(Root,name);}
        internal static Dictionary<string,object> Obj(params object[] values){var o=new Dictionary<string,object>();for(int i=0;i<values.Length;i+=2)o.Add((string)values[i],values[i+1]);return o;}
        internal static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        internal static Dictionary<string,object> Error(Exception error){var o=Obj("Type",error.GetType().Name,"HResult",error.HResult,"Message",error.Message);var w=error as Win32Exception;if(w!=null)o["NativeErrorCode"]=w.NativeErrorCode;return o;}
        internal static Dictionary<string,object> Read(string path){var file=new FileInfo(path);Require(file.Exists && file.Length<1048576 && (file.Attributes&FileAttributes.ReparsePoint)==0,"Expected regular bounded JSON");
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))using(var reader=new StreamReader(stream,Utf8))return Json.Deserialize<Dictionary<string,object>>(reader.ReadToEnd());
        }
        internal static void Save(string path,object value,bool replace){string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{File.WriteAllText(temporary,Json.Serialize(value),Utf8);if(replace && File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);}
            finally{if(File.Exists(temporary))File.Delete(temporary);} // exact temporary file just created by this call only
        }
        internal static string BootTime(){using(var search=new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem"))foreach(ManagementObject item in search.Get())return ManagementDateTimeConverter.ToDateTime(Convert.ToString(item["LastBootUpTime"])).ToUniversalTime().ToString("o");throw new InvalidOperationException("Boot time unavailable");}
        static bool Vmware(){
            // Avoid waiting for WMI before connecting to the SCM during auto-start.
            using(var bios=Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS"))
                return bios!=null && Convert.ToString(bios.GetValue("SystemManufacturer")).Contains("VMware") && Convert.ToString(bios.GetValue("SystemProductName")).Contains("VMware");
        }
        static bool Admin(){return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);}
        static void Guard(string mode){
            // No SCM calls, files or service registration before these physical-host checks.
            Require(Vmware(),"VMware guest required; no service access, files or registration performed.");
            bool system=WindowsIdentity.GetCurrent().User.Value=="S-1-5-18";
            Require(mode=="--service"?system:mode=="--probe"?!system && !Admin() && Process.GetCurrentProcess().SessionId>0:system || Admin(),"Unexpected process privilege/session");
            Root=Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);string directory=Path.GetFileName(Root);
            Require(directory.StartsWith("AICT-Service-",StringComparison.Ordinal),"Unexpected service laboratory directory");RunId=directory.Substring("AICT-Service-".Length);Guid id;Require(Guid.TryParseExact(RunId,"N",out id),"Invalid lab ID");
            Require(string.Equals(Root,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),directory),StringComparison.OrdinalIgnoreCase) && (File.GetAttributes(Root)&FileAttributes.ReparsePoint)==0,"Protected ProgramData location required");
            if(mode=="--cleanup")return; // Recovery still works if state/config was damaged.
            foreach(string path in new string[]{Join("state"),Join("probe")})Require(Directory.Exists(path) && (File.GetAttributes(path)&FileAttributes.ReparsePoint)==0,"Expected ordinary lab subdirectory");
            var config=Read(Join("config.json"));Require((string)config["RunId"]==RunId,"Config run mismatch");ProbeSid=(string)config["ProbeSid"];
            new SecurityIdentifier(ProbeSid);Require(ProbeSid.StartsWith("S-1-5-21-",StringComparison.Ordinal),"Expected local interactive account SID");
            if(mode=="--probe")Require(WindowsIdentity.GetCurrent().User.Value==ProbeSid,"Unexpected probe account");
        }
        internal static RawSecurityDescriptor Descriptor(string sid){return new RawSecurityDescriptor("O:BAG:BAD:P(A;;0xF01FF;;;SY)(A;;0xF01FF;;;BA)(A;;0x20085;;;"+sid+")");}
        internal static bool SecurityMatches(RawSecurityDescriptor d,string sid){
            // Services are flat SCM objects. Compare effective owner and exact ACEs;
            // no extra/inherited allow entry is accepted, regardless of control-bit normalization.
            if(d.Owner.Value!="S-1-5-32-544" || d.Group.Value!="S-1-5-32-544" || d.DiscretionaryAcl==null || d.DiscretionaryAcl.Count!=3)return false;
            var expected=new Dictionary<string,int>{{"S-1-5-18",0xF01FF},{"S-1-5-32-544",0xF01FF},{sid,0x20085}};
            foreach(GenericAce ace in d.DiscretionaryAcl){var common=ace as CommonAce;if(common==null || common.AceQualifier!=AceQualifier.AccessAllowed || common.AceFlags!=AceFlags.None || !expected.ContainsKey(common.SecurityIdentifier.Value) || expected[common.SecurityIdentifier.Value]!=common.AccessMask)return false;expected.Remove(common.SecurityIdentifier.Value);}
            return expected.Count==0;
        }
        static bool FileAclSafe(string path){
            FileSystemSecurity acl=(File.GetAttributes(path)&FileAttributes.Directory)!=0?(FileSystemSecurity)Directory.GetAccessControl(path):File.GetAccessControl(path);
            string owner=acl.GetOwner(typeof(SecurityIdentifier)).Value;if(owner!="S-1-5-18" && owner!="S-1-5-32-544")return false;
            bool userRead=false;
            foreach(FileSystemAccessRule rule in acl.GetAccessRules(true,true,typeof(SecurityIdentifier))){
                if(rule.AccessControlType!=AccessControlType.Allow)continue;string sid=rule.IdentityReference.Value;
                if(sid=="S-1-5-18" || sid=="S-1-5-32-544")continue;
                if(sid!=ProbeSid || (rule.FileSystemRights&(FileSystemRights.Write|FileSystemRights.Delete|FileSystemRights.ChangePermissions|FileSystemRights.TakeOwnership|FileSystemRights.DeleteSubdirectoriesAndFiles))!=0)return false;
                userRead=true;
            }
            return userRead;
        }
        static object Install(){
            using(var manager=Native.Manager(3)){
                Require(Native.Absent(manager),"Exact lab service already exists; refusing overwrite");
                using(var service=Native.CreateServiceW(manager,Name,"AICT phase0 temporary service "+RunId,Native.All,16,2,1,Image,null,IntPtr.Zero,null,"LocalSystem",null)){
                    Native.Check(!service.IsInvalid,"CreateService");Native.ValidateOwned(service);Native.SetSecurity(service,ProbeSid);
                    Native.SetRecovery(service);Native.ValidateOwned(service);Require(SecurityMatches(Native.Security(service),ProbeSid),"Service security readback mismatch");Require((bool)Native.Recovery(service)["MatchesFixedRecovery"],"Recovery readback mismatch");
                    Native.Check(Native.StartServiceW(service,0,IntPtr.Zero),"StartService");Native.WaitState(service,4,25);
                }
            }
            return Obj("Kind","AICT_SERVICE_LAB_INSTALL","RunId",RunId,"Installed",true,"SecurityVerified",true,"RecoveryVerified",true,"NoBlockingPolicyApplied",true);
        }
        static object Inspect(){
            using(var manager=Native.Manager())using(var service=Native.Open(manager,Native.QueryConfig|Native.QueryStatus|Native.ReadControl)){
                Native.ValidateOwned(service);var c=Native.Definition(service);var s=Native.GetStatus(service);
                return Obj("Kind","AICT_SERVICE_LAB_INSPECTION","RunId",RunId,"State",s.State,"ProcessId",s.Pid,"AutoStart",Convert.ToUInt32(c["StartType"])==2,"ImageAndAccountMatch",true,
                    "SecurityMatches",SecurityMatches(Native.Security(service),ProbeSid),"Recovery",Native.Recovery(service),"Heartbeat",Read(StatePath),"ObservedAtUtc",DateTimeOffset.UtcNow.ToString("o"));
            }
        }
        static object Crash(){
            using(var manager=Native.Manager())using(var service=Native.Open(manager,Native.Custom|Native.QueryConfig)){
                Native.ValidateOwned(service);Native.ControlStatus status;Native.Check(Native.ControlService(service,128,out status),"Fixed one-time lab crash control");
                return Obj("Kind","AICT_SERVICE_LAB_CRASH_REQUEST","RunId",RunId,"OneTimeCrashRequested",true);
            }
        }
        static object Cleanup(){
            bool existed;
            using(var manager=Native.Manager()){
                existed=!Native.Absent(manager);
                if(existed){using(var service=Native.Open(manager,Native.All)){
                    Native.ValidateOwned(service);
                    // Disable before stop/delete so a previously queued SCM restart cannot resurrect it.
                    Native.Check(Native.ChangeServiceConfigW(service,0xFFFFFFFF,4,0xFFFFFFFF,null,null,IntPtr.Zero,null,null,null,null),"Disable exact owned lab service");
                    uint current=Native.GetStatus(service).State;
                    if(current!=1){if(current!=3){Native.ControlStatus status;bool stopped=Native.ControlService(service,1,out status);if(!stopped && Marshal.GetLastWin32Error()!=1062)Native.Check(false,"Stop exact lab service");}Native.WaitState(service,1,15);}
                    Native.Check(Native.DeleteService(service),"Delete exact owned lab service");
                }}
                var watch=Stopwatch.StartNew();bool absent=false;while(watch.Elapsed.TotalSeconds<10){using(var test=Native.OpenServiceW(manager,Name,Native.QueryStatus)){if(test.IsInvalid){int e=Marshal.GetLastWin32Error();if(e==1060){absent=true;break;}Require(e==1072,"Service absence query failed: "+e);}}Thread.Sleep(100);}
                Require(absent,"Service deletion not confirmed");
            }
            return Obj("Kind","AICT_SERVICE_LAB_CLEANUP","RunId",RunId,"ServiceExisted",existed,"ServiceAbsent",true,"NoBlockingPolicyApplied",true);
        }
        static object Probe(string phase){
            Require(new string[]{"before-crash","after-crash","after-boot"}.Contains(phase),"Unknown probe phase");
            var tests=new List<Dictionary<string,object>>();bool pass=true;uint pid;
            using(var manager=Native.Manager()){
                using(var service=Native.Open(manager,Native.QueryStatus)){var status=Native.GetStatus(service);Require(status.State==4 && status.Pid>0,"Real running service required for probe");pid=status.Pid;}
                foreach(var entry in new Dictionary<string,uint>{{"Stop",Native.Stop},{"Reconfigure",Native.ChangeConfig},{"Delete",Native.Delete},{"ChangeDacl",Native.WriteDac},{"CustomControl",Native.Custom},{"Start",Native.Start}}){
                    using(var h=Native.OpenServiceW(manager,Name,entry.Value)){int e=h.IsInvalid?Marshal.GetLastWin32Error():0;bool denied=h.IsInvalid && e==5;pass&=denied;tests.Add(Obj("Operation",entry.Key,"Denied",denied,"NativeErrorCode",e));}
                }
            }
            IntPtr process=Native.OpenProcess(1,false,pid);int pe=process==IntPtr.Zero?Marshal.GetLastWin32Error():0;if(process!=IntPtr.Zero)Native.CloseHandle(process);
            bool processDenied=process==IntPtr.Zero && pe==5;pass&=processDenied;tests.Add(Obj("Operation","TerminateSystemServiceProcess","Denied",processDenied,"NativeErrorCode",pe));
            // A running image can fail with sharing violation before an access check. Test an
            // unmapped canary with the same ACL, and inspect the real binary/layout ACL too.
            bool layoutSafe=new string[]{Root,Join("state"),Join("AictServiceLab.exe"),Join("config.json"),Join("protection-canary.bin")}.All(FileAclSafe);pass&=layoutSafe;
            foreach(string relative in new string[]{"protection-canary.bin","config.json",@"state\service-state.json"}){
                bool denied=false;string failure=null;
                try{using(var stream=new FileStream(Join(relative),FileMode.Open,FileAccess.Write,FileShare.ReadWrite)){} }
                catch(UnauthorizedAccessException){denied=true;}
                catch(Exception error){failure=error.GetType().Name;}
                string owner=File.GetAccessControl(Join(relative)).GetOwner(typeof(SecurityIdentifier)).Value;bool ownerSafe=owner=="S-1-5-18" || owner=="S-1-5-32-544";
                bool aclSafe=FileAclSafe(Join(relative));pass&=denied && ownerSafe && aclSafe && failure==null;tests.Add(Obj("Operation","Write "+relative,"Denied",denied,"OwnerSystemOrAdministrators",ownerSafe,"AclSafe",aclSafe,"UnexpectedFailure",failure));
            }
            var report=Obj("Kind","AICT_SERVICE_LAB_LIMITED_PROBE","RunId",RunId,"Phase",phase,"LimitedToken",!Admin(),"InteractiveSession",true,"ProcessIdObserved",pid,"ProtectedLayoutAclSafe",layoutSafe,"Checks",tests,"PermissionsPassed",pass,"NoStopDeleteKillOrFileBytesWritten",true);
            Save(Join("probe\\probe-"+phase+".json"),report,false);return report;
        }
        static object Validate(){
            Require(IntPtr.Size==8 && Marshal.SizeOf(typeof(Native.Status))==36 && Marshal.SizeOf(typeof(Native.ControlStatus))==28 && Marshal.SizeOf(typeof(Native.Config))==64 && Marshal.SizeOf(typeof(Native.Failure))==40 && Marshal.SizeOf(typeof(Native.Action))==8,"SCM x64 ABI mismatch");
            string sid="S-1-5-21-1-2-3-1001";var d=Descriptor(sid);Require(SecurityMatches(d,sid),"Fixed service descriptor invalid");
            var widened=new RawSecurityDescriptor("O:BAG:BAD:P(A;;0xF01FF;;;SY)(A;;0xF01FF;;;BA)(A;;0xF01FF;;;"+sid+")");Require(!SecurityMatches(widened,sid),"Writable user service descriptor accepted");
            var owner=new RawSecurityDescriptor("O:"+sid+"G:BAD:P(A;;0xF01FF;;;SY)(A;;0xF01FF;;;BA)(A;;0x20085;;;"+sid+")");Require(!SecurityMatches(owner,sid),"User-owned service descriptor accepted");
            Require(!SecurityMatches(d,"S-1-5-21-1-2-3-1002"),"Different account accepted");
            return Obj("Kind","SERVICE_LAB_OFFLINE_CONTRACT_NOT_SERVICE_INSTALLATION","Assertions",5,"Passed",true);
        }
        static int Main(string[] args){
            bool serviceMode=args.Length==1 && args[0]=="--service";
            try{
                if(!serviceMode){Console.OutputEncoding=Utf8;Console.SetError(new StreamWriter(Console.OpenStandardError(),Utf8){AutoFlush=true});}
                if(args.Length==1 && args[0]=="--validate"){Console.WriteLine(Json.Serialize(Validate()));return 0;}
                Require(args.Length>=1 && new string[]{"--service","--install","--inspect","--crash","--cleanup","--probe"}.Contains(args[0]) && (args[0]=="--probe"?args.Length==2:args.Length==1),"Use the fixed VMware guest lab runner");
                Guard(args[0]);if(serviceMode){ServiceBase.Run(new HeartbeatService());return 0;}
                object result=args[0]=="--install"?Install():args[0]=="--inspect"?Inspect():args[0]=="--crash"?Crash():args[0]=="--cleanup"?Cleanup():Probe(args[1]);
                Console.WriteLine(Json.Serialize(result));return 0;
            }catch(Exception error){try{Console.Error.WriteLine(Json.Serialize(Error(error)));}catch{}return 1;}
        }
    }
}
