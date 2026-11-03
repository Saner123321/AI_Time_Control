// Phase-0 laboratory harness, .NET Framework / x64. Not the product backend.
// Only --smoke writes WFP objects, and only after VMware + administrator guards.
// All objects are dynamic and scoped to a generated copy of this executable.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Web.Script.Serialization;

namespace AictLab {
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    struct Display { [MarshalAs(UnmanagedType.LPWStr)] public string name; [MarshalAs(UnmanagedType.LPWStr)] public string description; }
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
    static class Program {
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        static readonly string[] Modes = {"tcp4","udp4","tcp6","udp6"};
        const int Repeats = 3;
        static readonly Guid AppId = new Guid("d78e1e87-8644-4ea5-9437-d809ecefc971");
        static readonly Guid V4 = new Guid("c38d57d1-05a7-4c33-904f-7fbceee60e82");
        static readonly Guid V6 = new Guid("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");
        static Dictionary<string,object> Obj(params object[] fields) {
            var result=new Dictionary<string,object>(); for(int i=0;i<fields.Length;i+=2) result.Add((string)fields[i],fields[i+1]); return result;
        }
        static void Check(uint code,string api) { if(code!=0) throw new InvalidOperationException(api+" returned 0x"+code.ToString("X8")); }
        static Dictionary<string,object> Layout() {
            var sizes=new Dictionary<string,object>();
            Type[] types={typeof(Display),typeof(Blob),typeof(Value),typeof(Context),typeof(Action),typeof(Condition),typeof(Session),typeof(Provider),typeof(Sublayer),typeof(Filter)};
            int[] expected={16,16,16,16,20,40,72,64,72,200};
            if(IntPtr.Size!=8) throw new InvalidOperationException("x64 required");
            for(int i=0;i<types.Length;i++) { int size=Marshal.SizeOf(types[i]); sizes.Add(types[i].Name,size); if(size!=expected[i]) throw new InvalidOperationException("ABI size mismatch: "+types[i].Name); }
            if(Marshal.OffsetOf(typeof(Filter),"action").ToInt32()!=128 || Marshal.OffsetOf(typeof(Filter),"context").ToInt32()!=152 || Marshal.OffsetOf(typeof(Filter),"id").ToInt32()!=176)
                throw new InvalidOperationException("Filter ABI offset mismatch");
            return sizes;
        }
        static bool IsVmware() {
            using(var search=new ManagementObjectSearcher("SELECT Manufacturer,Model FROM Win32_ComputerSystem"))
                foreach(ManagementObject item in search.Get())
                    if(Convert.ToString(item["Manufacturer"]).Contains("VMware") && Convert.ToString(item["Model"]).Contains("VMware")) return true;
            return false;
        }
        static void ValidateMode(string mode) { if(!Modes.Contains(mode)) throw new ArgumentException("Unknown test protocol"); }
        static IPAddress Address(string mode) { return mode.EndsWith("6")?IPAddress.IPv6Loopback:IPAddress.Loopback; }
        static Socket NewSocket(string mode) { bool tcp=mode.StartsWith("tcp"); return new Socket(Address(mode).AddressFamily,tcp?SocketType.Stream:SocketType.Dgram,tcp?ProtocolType.Tcp:ProtocolType.Udp); }
        static Dictionary<string,object> Client(string mode,int port) {
            ValidateMode(mode);
            string stage="connect";
            try {
                using(var socket=NewSocket(mode)) {
                    socket.ReceiveTimeout=1000; socket.SendTimeout=1000;
                    IAsyncResult pending=socket.BeginConnect(new IPEndPoint(Address(mode),port),null,null);
                    using(var wait=pending.AsyncWaitHandle) { if(!wait.WaitOne(1500)) return Obj("connected",false,"socket_error",10060,"error_stage",stage,"reason","connect timeout"); socket.EndConnect(pending); }
                    stage="send"; socket.Send(new byte[]{0x5A}); byte[] echo=new byte[1]; stage="receive"; int count=socket.Receive(echo);
                    return Obj("connected",count==1 && echo[0]==0x5A,"socket_error",0,"error_stage",null);
                }
            } catch(SocketException e) { return Obj("connected",false,"socket_error",e.NativeErrorCode,"error_stage",stage); }
        }
        static Dictionary<string,object> Exchange(string exe,string mode,string phase) {
            using(var listener=NewSocket(mode)) {
                listener.ReceiveTimeout=3000; listener.SendTimeout=1000;
                listener.Bind(new IPEndPoint(Address(mode),0)); if(mode.StartsWith("tcp")) listener.Listen(4);
                int port=((IPEndPoint)listener.LocalEndPoint).Port;
                int received=0,replied=0,stopping=0; string serverError=null;
                var thread=new Thread(delegate() {
                    try {
                        byte[] buffer=new byte[1];
                        if(mode.StartsWith("tcp")) {
                            using(Socket accepted=listener.Accept()) { accepted.ReceiveTimeout=1500; int size=accepted.Receive(buffer); if(size>0) Interlocked.Add(ref received,size); if(size==1) { accepted.Send(buffer);Interlocked.Increment(ref replied); } }
                        } else { EndPoint peer=new IPEndPoint(Address(mode),0); int size=listener.ReceiveFrom(buffer,ref peer); if(size>0) Interlocked.Add(ref received,size); if(size==1) { listener.SendTo(buffer,peer);Interlocked.Increment(ref replied); } }
                    } catch(SocketException e) { if(Interlocked.CompareExchange(ref stopping,0,0)==0) serverError="SocketError:"+e.NativeErrorCode; }
                    catch(ObjectDisposedException) { if(Interlocked.CompareExchange(ref stopping,0,0)==0) serverError="UnexpectedObjectDisposed"; }
                });
                thread.IsBackground=true; thread.Start();
                Dictionary<string,object> result=null; bool joined=false;
                try {
                    var start=new ProcessStartInfo(exe,"--client "+mode+" "+port) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
                    using(var child=Process.Start(start)) {
                        if(!child.WaitForExit(5000)) { child.Kill(); child.WaitForExit(); throw new TimeoutException("Own test client exceeded time limit"); }
                        if(child.ExitCode!=0) throw new InvalidOperationException("Own test client failed: "+child.StandardError.ReadToEnd());
                        result=Json.Deserialize<Dictionary<string,object>>(child.StandardOutput.ReadToEnd()); result.Add("protocol",mode); result.Add("phase",phase);
                    }
                } finally { Interlocked.Exchange(ref stopping,1);listener.Close();joined=thread.Join(1800); }
                result.Add("server_received_bytes",received);result.Add("server_reply_count",replied);result.Add("server_error",serverError);result.Add("server_thread_joined",joined);
                return result;
            }
        }
        static string MakeClients(string directory,out string control) {
            Directory.CreateDirectory(directory);
            string source=Assembly.GetExecutingAssembly().Location;
            string target=Path.Combine(directory,"AictProbeTarget.exe"); control=Path.Combine(directory,"AictProbeControl.exe");
            File.Copy(source,target,false); File.Copy(source,control,false); return target;
        }
        static bool Connected(Dictionary<string,object> row) { return row.ContainsKey("connected") && row["connected"] is bool && (bool)row["connected"]; }
        static bool TrialPassed(Dictionary<string,object> row) {
            // Missing evidence must fail closed. A UDP timeout by itself is insufficient.
            string[] required={"connected","protocol","phase","socket_error","error_stage","server_received_bytes","server_reply_count","server_error","server_thread_joined"};
            if(required.Any(k=>!row.ContainsKey(k)) || !(row["server_thread_joined"] is bool) || !(bool)row["server_thread_joined"] || row["server_error"]!=null) return false;
            int received=Convert.ToInt32(row["server_received_bytes"]),replied=Convert.ToInt32(row["server_reply_count"]);
            if((string)row["phase"]!="blocked-target") return Connected(row) && received==1 && replied==1 && Convert.ToInt32(row["socket_error"])==0;
            if(Connected(row) || received!=0 || replied!=0) return false;
            int error=Convert.ToInt32(row["socket_error"]); string stage=row["error_stage"] as string;
            if(error==10013 && (stage=="connect" || stage=="send")) return true;
            return ((string)row["protocol"]).StartsWith("udp") && error==10060 && stage=="receive";
        }
        static bool AllTrialsPassed(List<Dictionary<string,object>> rows) {
            if(rows.Count!=Modes.Length*4*Repeats || !rows.All(TrialPassed)) return false;
            foreach(string phase in new string[]{"baseline-target","blocked-target","unblocked-control","after-cleanup-target"})
                foreach(string mode in Modes) if(rows.Count(r=>(string)r["phase"]==phase && (string)r["protocol"]==mode)!=Repeats) return false;
            return true;
        }
        static void RunPhase(List<Dictionary<string,object>> rows,string executable,string phase,bool requireSuccess) {
            foreach(string mode in Modes) for(int repeat=0;repeat<Repeats;repeat++) {
                var row=Exchange(executable,mode,phase);row.Add("repeat",repeat+1);rows.Add(row);
                if(requireSuccess && !TrialPassed(row)) throw new InvalidOperationException("Echo baseline failed: "+mode);
            }
        }
        static bool SameAppBlob(IntPtr left,IntPtr right) {
            if(left==IntPtr.Zero || right==IntPtr.Zero) return false;
            var a=(Blob)Marshal.PtrToStructure(left,typeof(Blob));var b=(Blob)Marshal.PtrToStructure(right,typeof(Blob));
            if(a.size==0 || a.size!=b.size || a.size>65536 || a.data==IntPtr.Zero || b.data==IntPtr.Zero) return false;
            byte[] aa=new byte[a.size],bb=new byte[b.size];Marshal.Copy(a.data,aa,0,aa.Length);Marshal.Copy(b.data,bb,0,bb.Length);return aa.SequenceEqual(bb);
        }
        static Dictionary<string,object> VerifyActive(IntPtr engine,string target,Guid provider,Guid sublayer,List<ulong> ids) {
            IntPtr app=IntPtr.Zero; var details=new List<Dictionary<string,object>>();
            try {
                Check(Native.FwpmGetAppIdFromFileName0(target,out app),"active verification app identity");
                foreach(ulong id in ids) {
                    IntPtr pointer=IntPtr.Zero;
                    try {
                        Check(Native.FwpmFilterGetById0(engine,id,out pointer),"active filter query");var filter=(Filter)Marshal.PtrToStructure(pointer,typeof(Filter));
                        bool match=false;
                        if(filter.conditionCount==1 && filter.conditions!=IntPtr.Zero) {
                            var condition=(Condition)Marshal.PtrToStructure(filter.conditions,typeof(Condition));
                            match=condition.field==AppId && condition.match==0 && condition.value.type==12 && SameAppBlob(condition.value.data,app);
                        }
                        bool own=filter.provider!=IntPtr.Zero && (Guid)Marshal.PtrToStructure(filter.provider,typeof(Guid))==provider && filter.sublayer==sublayer;
                        details.Add(Obj("filter_id",id,"layer",filter.layer.ToString(),"target_identity_exact",match,"owned_provider_sublayer",own,
                            "block_action",filter.action.type==0x1001,"persistent_or_boot",(filter.flags&3)!=0));
                    } finally { if(pointer!=IntPtr.Zero)Native.FwpmFreeMemory0(ref pointer); }
                }
            } finally { if(app!=IntPtr.Zero)Native.FwpmFreeMemory0(ref app); }
            bool verified=details.Count==2 && details.Select(r=>(string)r["layer"]).Distinct().Count()==2 && details.All(r=>
                ((string)r["layer"]==V4.ToString() || (string)r["layer"]==V6.ToString()) && (bool)r["target_identity_exact"] && (bool)r["owned_provider_sublayer"] && (bool)r["block_action"] && !(bool)r["persistent_or_boot"]);
            return Obj("verified",verified,"filters",details);
        }
        static void AddFilters(IntPtr engine,string target,Guid providerKey,Guid sublayerKey,List<ulong> ids) {
            IntPtr providerPtr=Marshal.AllocHGlobal(16),conditionPtr=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Condition))),appBlob=IntPtr.Zero;
            bool transaction=false;
            try {
                Marshal.StructureToPtr(providerKey,providerPtr,false);
                Check(Native.FwpmGetAppIdFromFileName0(target,out appBlob),"FwpmGetAppIdFromFileName0");
                Check(Native.FwpmTransactionBegin0(engine,0),"FwpmTransactionBegin0"); transaction=true;
                var provider=new Provider {key=providerKey,display=new Display {name="AICT phase0 dynamic experiment"}};
                Check(Native.FwpmProviderAdd0(engine,ref provider,IntPtr.Zero),"FwpmProviderAdd0");
                var sublayer=new Sublayer {key=sublayerKey,provider=providerPtr,weight=0x7000,display=new Display {name="AICT phase0 target-only sublayer"}};
                Check(Native.FwpmSubLayerAdd0(engine,ref sublayer,IntPtr.Zero),"FwpmSubLayerAdd0");
                var condition=new Condition {field=AppId,match=0,value=new Value {type=12,data=appBlob}};
                Marshal.StructureToPtr(condition,conditionPtr,false);
                foreach(Guid layer in new Guid[]{V4,V6}) {
                    var filter=new Filter {key=Guid.NewGuid(),display=new Display {name="AICT generated probe executable only"},provider=providerPtr,
                        layer=layer,sublayer=sublayerKey,conditionCount=1,conditions=conditionPtr,action=new Action {type=0x1001}};
                    ulong id; Check(Native.FwpmFilterAdd0(engine,ref filter,IntPtr.Zero,out id),"FwpmFilterAdd0"); ids.Add(id);
                }
                Check(Native.FwpmTransactionCommit0(engine),"FwpmTransactionCommit0"); transaction=false;
            } finally {
                if(transaction) Native.FwpmTransactionAbort0(engine);
                if(appBlob!=IntPtr.Zero) Native.FwpmFreeMemory0(ref appBlob);
                Marshal.FreeHGlobal(conditionPtr); Marshal.FreeHGlobal(providerPtr);
            }
        }
        static Dictionary<string,object> VerifyCleanup(Guid provider,Guid sublayer,List<ulong> ids) {
            var session=new Session(); IntPtr engine=IntPtr.Zero;
            var results=new List<uint>(); uint providerCode=0,sublayerCode=0,closeCode=0;
            try {
                Check(Native.FwpmEngineOpen0(IntPtr.Zero,10,IntPtr.Zero,ref session,out engine),"cleanup engine open");
                foreach(ulong id in ids) { IntPtr p=IntPtr.Zero; uint status=Native.FwpmFilterGetById0(engine,id,out p); results.Add(status); if(p!=IntPtr.Zero) Native.FwpmFreeMemory0(ref p); }
                IntPtr pointer=IntPtr.Zero; providerCode=Native.FwpmProviderGetByKey0(engine,ref provider,out pointer); if(pointer!=IntPtr.Zero) Native.FwpmFreeMemory0(ref pointer);
                sublayerCode=Native.FwpmSubLayerGetByKey0(engine,ref sublayer,out pointer); if(pointer!=IntPtr.Zero) Native.FwpmFreeMemory0(ref pointer);
            } finally { if(engine!=IntPtr.Zero) closeCode=Native.FwpmEngineClose0(engine); }
            return Obj("filter_query_codes",results,"provider_query_code",providerCode,"sublayer_query_code",sublayerCode,"query_engine_close_code",closeCode,
                "objects_absent",ids.Count==2 && results.All(c=>c==0x80320003U) && providerCode==0x80320005U && sublayerCode==0x80320007U && closeCode==0);
        }
        static int Smoke(string snapshot) {
            if(!IsVmware()) throw new InvalidOperationException("VMware guest required. No WFP objects created.");
            if(!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) throw new InvalidOperationException("Run inside the guest as administrator.");
            if(string.IsNullOrWhiteSpace(snapshot)) throw new ArgumentException("Snapshot reference required; this is operator-supplied, not automated restore verification.");
            var report=Obj("schema_version",2,"kind","TRANSIENT_WFP_SMOKE_NOT_FULL_PHASE0","collected_at_utc",DateTimeOffset.UtcNow.ToString("o"),"repeats_per_protocol_phase",Repeats,
                "snapshot_reference_operator_supplied",snapshot,"snapshot_restore_verified",false,"persistent_or_boot_filters_tested",false,"existing_connections_tested",false,"abi",Layout());
            string directory=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"wfp-smoke-"+DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ"));
            string control; string target=MakeClients(directory,out control);
            var rows=new List<Dictionary<string,object>>(); report.Add("connections",rows);
            IntPtr engine=IntPtr.Zero; var ids=new List<ulong>(); Guid provider=Guid.NewGuid(),sublayer=Guid.NewGuid(); bool installed=false;
            bool all=false,activeVerified=false; uint closeCode=0;
            try {
                RunPhase(rows,target,"baseline-target",true);
                var session=new Session {key=Guid.NewGuid(),flags=1,display=new Display {name="AICT dynamic lab; automatic session cleanup"}};
                Check(Native.FwpmEngineOpen0(IntPtr.Zero,10,IntPtr.Zero,ref session,out engine),"dynamic engine open");
                AddFilters(engine,target,provider,sublayer,ids); installed=true;
                var active=VerifyActive(engine,target,provider,sublayer,ids);report["active_filter_verification"]=active;activeVerified=(bool)active["verified"];
                if(!activeVerified)throw new InvalidOperationException("Installed filter scope verification failed");
                RunPhase(rows,target,"blocked-target",false);
                RunPhase(rows,control,"unblocked-control",false);
            } catch(Exception e) { report["error"]=e.GetType().Name+": "+e.Message; }
            finally { if(engine!=IntPtr.Zero) closeCode=Native.FwpmEngineClose0(engine); }
            report["dynamic_engine_close_code"]=closeCode;
            try {
                if(installed) {
                    var cleanup=VerifyCleanup(provider,sublayer,ids); report["cleanup"]=cleanup;
                    RunPhase(rows,target,"after-cleanup-target",false);
                    all=activeVerified && closeCode==0 && (bool)cleanup["objects_absent"] && AllTrialsPassed(rows);
                }
            } catch(Exception e) { report["cleanup_error"]=e.GetType().Name+": "+e.Message; }
            report["transient_smoke_passed"]=all;
            report["phase0_pass_awarded"]=false;
            File.WriteAllText(Path.Combine(directory,"result.json"),Json.Serialize(report),new System.Text.UTF8Encoding(false));
            Console.WriteLine("Evidence directory: "+directory);Console.WriteLine("Transient smoke passed: "+all+". Full phase0 gate remains pending.");
            return all?0:1;
        }
        static int LocalSelfTest() {
            Layout(); string directory=Path.Combine(Path.GetTempPath(),"aict-local-socket-test-"+Guid.NewGuid().ToString("N"));
            string control; string target=MakeClients(directory,out control);
            var results=new List<Dictionary<string,object>>();
            foreach(string mode in Modes) { results.Add(Exchange(target,mode,"host-local-socket-test"));results.Add(Exchange(control,mode,"host-local-control-test")); }
            Console.WriteLine(Json.Serialize(Obj("kind","LOCAL_SOCKET_SELFTEST_NO_WFP_WRITES","results",results,"passed",results.All(TrialPassed))));
            return results.All(TrialPassed)?0:1;
        }
        static int VerdictSelfTest() {
            var baseline=Obj("connected",false,"protocol","udp4","phase","blocked-target","socket_error",10060,"error_stage","receive", "server_received_bytes",0,"server_reply_count",0,"server_error",null,"server_thread_joined",true);
            if(!TrialPassed(baseline))throw new InvalidOperationException("Controlled UDP negative-delivery evidence rejected");
            int assertions=1;
            foreach(string key in new string[]{"server_received_bytes","server_reply_count","server_error","server_thread_joined","error_stage"}) {
                var row=new Dictionary<string,object>(baseline);row.Remove(key);if(TrialPassed(row))throw new InvalidOperationException("Missing evidence accepted: "+key);assertions++;
            }
            object[][] changes={new object[]{"server_received_bytes",1},new object[]{"server_reply_count",1},new object[]{"server_error","listener failed"},new object[]{"server_thread_joined",false},new object[]{"error_stage","connect"},new object[]{"protocol","tcp4"},new object[]{"connected",true}};
            foreach(var change in changes) {var row=new Dictionary<string,object>(baseline);row[(string)change[0]]=change[1];if(TrialPassed(row))throw new InvalidOperationException("Invalid evidence accepted: "+change[0]);assertions++;}
            if(AllTrialsPassed(new List<Dictionary<string,object>>{baseline}))throw new InvalidOperationException("Incomplete experiment accepted");assertions++;
            Console.WriteLine(Json.Serialize(Obj("kind","SYNTHETIC_VERDICT_SELFTEST_NOT_ENFORCEMENT","assertions",assertions,"passed",true)));return 0;
        }
        static int Main(string[] args) {
            try {
                if(args.Length==3 && args[0]=="--client") return WriteClient(args[1],int.Parse(args[2]));
                if(args.Length==1 && args[0]=="--layout-only") {Console.WriteLine(Json.Serialize(Layout()));return 0;}
                if(args.Length==1 && args[0]=="--selftest-local") return LocalSelfTest();
                if(args.Length==1 && args[0]=="--selftest-verdict") return VerdictSelfTest();
                if(args.Length==3 && args[0]=="--smoke" && args[1]=="--snapshot-reference") return Smoke(args[2]);
                Console.WriteLine("Use the supplied guest-only runner. No filtering performed.");return 2;
            } catch(Exception e) {Console.Error.WriteLine(e.GetType().Name+": "+e.Message);return 1;}
        }
        static int WriteClient(string mode,int port) { Console.WriteLine(Json.Serialize(Client(mode,port)));return 0; }
    }
}
