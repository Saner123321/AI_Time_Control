// VM-only integration bridge. The inbox Framework MI implementation was already tested on Home.
// No arbitrary XML/path/command input: only this build's generated inert PE can be denied.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;
using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;
using Aict.Enforcement;
using MiType=Microsoft.Management.Infrastructure.CimType;

namespace AictServicePolicyLab {
    static class Program {
        const string Namespace=@"root\cimv2\mdm\dmmap";
        const string ExeClass="MDM_AppLocker_ApplicationLaunchRestrictions01_EXE03",StoreClass="MDM_AppLocker_ApplicationLaunchRestrictions01_StoreApps03";
        internal static string Root,RunId,ProbeSid;
        internal static string Name {get{return "AICT-Policy-"+RunId;}}
        internal static string Image {get{return "\""+Join(@"Service\Aict.Service.exe")+"\" --vm-lab";}}
        static string Parent {get{return "./Vendor/MSFT/AppLocker/ApplicationLaunchRestrictions/AICT-ServicePolicy-"+RunId;}}
        static readonly JavaScriptSerializer Json=new JavaScriptSerializer {MaxJsonLength=1048576};
        static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
        internal static string Join(string path){return Path.Combine(Root,path);}
        internal static void Require(bool good,string message){if(!good)throw new InvalidOperationException(message);}
        internal static Dictionary<string,object> Obj(params object[] pairs){var o=new Dictionary<string,object>();for(int i=0;i<pairs.Length;i+=2)o.Add((string)pairs[i],pairs[i+1]);return o;}
        static bool Admin(){return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);}
        static bool System(){return WindowsIdentity.GetCurrent().User.Value=="S-1-5-18";}
        static bool Vmware(){using(var bios=Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS"))return bios!=null && Convert.ToString(bios.GetValue("SystemManufacturer")).Contains("VMware") && Convert.ToString(bios.GetValue("SystemProductName")).Contains("VMware");}
        static Dictionary<string,object> Read(string path){var f=new FileInfo(path);Require(f.Exists && f.Length<1048576 && (f.Attributes&FileAttributes.ReparsePoint)==0,"Expected ordinary bounded config");return Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(path,Utf8));}
        static void Guard(string mode){
            Require(Vmware(),"VMware guest required; no SCM, policy or file changes performed.");
            Require(mode=="--probe"?!System() && !Admin() && Process.GetCurrentProcess().SessionId>0:mode=="--cleanup" || mode.StartsWith("--policy-",StringComparison.Ordinal)?System():System() || Admin(),"Wrong process token for fixed operation");
            Root=Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);string folder=Path.GetFileName(Root);
            Require(folder.StartsWith("AICT-ServicePolicy-",StringComparison.Ordinal),"Unexpected installation root");RunId=folder.Substring("AICT-ServicePolicy-".Length);Guid id;Require(Guid.TryParseExact(RunId,"N",out id),"Invalid ownership ID");
            Require(string.Equals(Root,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),folder),StringComparison.OrdinalIgnoreCase),"Protected ProgramData root required");
            Require((File.GetAttributes(Root)&FileAttributes.ReparsePoint)==0,"Reparse root refused");
            // Cleanup can infer the fixed deny SID from a fully shape-checked owned policy if config was damaged.
            if(mode=="--cleanup")return;
            var config=Read(Join("config.json"));Require((string)config["runId"]==RunId,"Run mismatch");ProbeSid=(string)config["probeSid"];
            Require(ProbeSid.StartsWith("S-1-5-21-",StringComparison.Ordinal),"Local-user SID required");new SecurityIdentifier(ProbeSid);
            if(mode=="--probe")Require(WindowsIdentity.GetCurrent().User.Value==ProbeSid,"Unexpected probe SID");
            foreach(string directory in new[]{Root,Join("Service"),Join("state"),Join("Targets"),Join("probe")})Require((File.GetAttributes(directory)&FileAttributes.ReparsePoint)==0,"Reparse path refused");
            foreach(string relative in new[]{@"Targets\Target.exe",@"Targets\Renamed.exe","MovedTarget.exe",@"Targets\AllowedControl.exe"}){
                string path=Join(relative);Require((File.GetAttributes(path)&FileAttributes.ReparsePoint)==0,"Reparse fixture refused");
                Require(Hash(path)==(relative.EndsWith("AllowedControl.exe",StringComparison.Ordinal)?FixtureIdentity.ControlSha256:FixtureIdentity.TargetSha256),"Frozen fixture mismatch");
            }
        }
        static string Hash(string path){using(var sha=SHA256.Create())using(var f=File.OpenRead(path))return BitConverter.ToString(sha.ComputeHash(f)).Replace("-","").ToLowerInvariant();}
        internal static RawSecurityDescriptor Descriptor(string sid){return new RawSecurityDescriptor("O:BAG:BAD:P(A;;0xF01FF;;;SY)(A;;0xF01FF;;;BA)(A;;0x20085;;;"+sid+")");}
        static bool SecurityMatches(RawSecurityDescriptor d,string sid){
            if(d.Owner.Value!="S-1-5-32-544" || d.Group.Value!="S-1-5-32-544" || d.DiscretionaryAcl==null || d.DiscretionaryAcl.Count!=3)return false;
            var rights=new Dictionary<string,int>{{"S-1-5-18",0xF01FF},{"S-1-5-32-544",0xF01FF},{sid,0x20085}};
            foreach(GenericAce a in d.DiscretionaryAcl){var ace=a as CommonAce;if(ace==null || ace.AceQualifier!=AceQualifier.AccessAllowed || ace.AceFlags!=AceFlags.None || !rights.ContainsKey(ace.SecurityIdentifier.Value) || rights[ace.SecurityIdentifier.Value]!=ace.AccessMask)return false;rights.Remove(ace.SecurityIdentifier.Value);}return rights.Count==0;
        }
        static string Property(CimInstance row,string name){return Convert.ToString(row.CimInstanceProperties[name].Value);}
        static List<CimInstance> Rows(CimSession session,bool store){using(var options=new CimOperationOptions {Timeout=TimeSpan.FromSeconds(8)})return session.QueryInstances(Namespace,"WQL","SELECT * FROM "+(store?StoreClass:ExeClass),options).ToList();}
        static string Expected(bool store){return store?AppLockerXml.Appx(RunId):AppLockerXml.Exe(RunId,ProbeSid,FixtureIdentity.TargetSipHash,FixtureIdentity.TargetSize);}
        static void VerifyRow(CimInstance row,bool store,bool recovery){
            Require(Property(row,"ParentID")==Parent && Property(row,"InstanceID")== (store?"StoreApps":"EXE"),"Foreign AppLocker policy detected; no merge or overwrite performed");
            if(!store && recovery && ProbeSid==null){var xml=AppLockerXml.Parse(Property(row,"Policy"));var deny=xml.SelectSingleNode("/RuleCollection/FileHashRule") as System.Xml.XmlElement;Require(deny!=null,"Missing owned deny rule");ProbeSid=deny.GetAttribute("UserOrGroupSid");}
            Require(AppLockerXml.Equivalent(Property(row,"Policy"),Expected(store)),"Owned policy shape changed; left untouched");
        }
        static bool RegistrySafe(){
            using(var root=Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\SrpV2")){
                if(root==null)return true;
                var allowed=new HashSet<string>(new[]{AppLockerXml.RuleId(RunId,"exe-Allow"),AppLockerXml.RuleId(RunId,"exe-Deny"),AppLockerXml.RuleId(RunId,"appx-Allow")},StringComparer.OrdinalIgnoreCase);
                foreach(string collection in root.GetSubKeyNames())using(var key=root.OpenSubKey(collection)){
                    if(!new[]{"Exe","Appx"}.Contains(collection,StringComparer.OrdinalIgnoreCase))return false;
                    foreach(string rule in key.GetSubKeyNames())if(!allowed.Contains(rule))return false;
                    // An externally configured ruleless enforcement collection is also a policy conflict.
                    if(key.GetSubKeyNames().Length==0 && key.GetValueNames().Length>0)return false;
                }
                return root.GetValueNames().Length==0;
            }
        }
        static Dictionary<string,object> InspectPolicy(CimSession session,bool recovery){
            Require(RegistrySafe(),"Local AppLocker policy conflict; left untouched");bool exe=false,appx=false;
            foreach(bool store in new[]{false,true}){
                var rows=Rows(session,store);try{Require(rows.Count<=1,"Foreign/duplicate policy detected");foreach(var row in rows){VerifyRow(row,store,recovery);if(store)appx=true;else exe=true;}}finally{foreach(var row in rows)row.Dispose();}
            }
            Require(recovery || !exe || appx,"Owned EXE present without companion Appx safety policy");
            return Obj("runId",RunId,"exePresent",exe,"companionPresent",appx,"readbackBlocked",exe,"fixedFixtureOnly",true);
        }
        static void Create(CimSession session,bool store){using(var instance=new CimInstance(store?StoreClass:ExeClass,Namespace)){
            instance.CimInstanceProperties.Add(CimProperty.Create("ParentID",Parent,MiType.String,CimFlags.Key));instance.CimInstanceProperties.Add(CimProperty.Create("InstanceID",store?"StoreApps":"EXE",MiType.String,CimFlags.Key));
            instance.CimInstanceProperties.Add(CimProperty.Create("Policy",WebUtility.HtmlEncode(Expected(store)),MiType.String,CimFlags.Property));
            using(var options=new CimOperationOptions {Timeout=TimeSpan.FromSeconds(8)})using(var created=session.CreateInstance(Namespace,instance,options)){}
        }}
        static void Remove(CimSession session,bool store,bool recovery){var rows=Rows(session,store);try{foreach(var row in rows){VerifyRow(row,store,recovery);using(var options=new CimOperationOptions {Timeout=TimeSpan.FromSeconds(8)})session.DeleteInstance(Namespace,row,options);}}finally{foreach(var row in rows)row.Dispose();}}
        static object Policy(string mode){
            using(var session=CimSession.Create(null)){
                var before=InspectPolicy(session,false);
                if(mode=="--policy-block"){
                    if(!(bool)before["companionPresent"])Create(session,true);
                    if(!(bool)before["exePresent"])Create(session,false);
                }else if(mode=="--policy-allow" && (bool)before["exePresent"])Remove(session,false,false);
                return InspectPolicy(session,false);
            }
        }
        static object Install(){
            Require(File.Exists(Join("baseline-passed.json")),"Limited-token baseline must pass before service install");
            Require(AuthenticodeHash.Compute(Join(@"Targets\Target.exe"))==FixtureIdentity.TargetSipHash,"PE SIP identity mismatch");
            using(var manager=Native.Manager(3)){
                Require(Native.Absent(manager),"Service exists; refused overwrite");
                using(var service=Native.CreateServiceW(manager,Name,"AICT fixture policy integration "+RunId,Native.All,16,3,1,Image,null,IntPtr.Zero,null,"LocalSystem",null)){
                    Native.Check(!service.IsInvalid,"CreateService");Native.ValidateOwned(service);Native.SetSecurity(service,ProbeSid);Native.SetRecovery(service);
                    Require(SecurityMatches(Native.Security(service),ProbeSid) && (bool)Native.Recovery(service)["MatchesFixedRecovery"],"Service security/recovery readback mismatch");
                    Native.Check(Native.StartServiceW(service,0,IntPtr.Zero),"StartService");Native.WaitState(service,4,25);
                }
            }
            return Obj("runId",RunId,"installed",true,"manualStartLabOnly",true);
        }
        static object Inspect(){using(var manager=Native.Manager())using(var service=Native.Open(manager,Native.QueryConfig|Native.QueryStatus|Native.ReadControl)){
            Native.ValidateOwned(service);var status=Native.GetStatus(service);var definition=Native.Definition(service);return Obj("runId",RunId,"state",status.State,"processId",status.Pid,"startType",definition["StartType"],"dependencies",Dependencies(service),"imageAndAccountMatch",true,"serviceSecurityMatches",SecurityMatches(Native.Security(service),ProbeSid),"recoveryMatches",(bool)Native.Recovery(service)["MatchesFixedRecovery"]);
        }}
        static string[] Dependencies(ServiceHandle service){
            uint size;Native.QueryServiceConfigW(service,IntPtr.Zero,0,out size);Require(size>=64 && size<65536,"Invalid dependency config size");IntPtr buffer=Marshal.AllocHGlobal((int)size);
            try{
                Native.Check(Native.QueryServiceConfigW(service,buffer,size,out size),"Read owned service dependencies");var config=(Native.Config)Marshal.PtrToStructure(buffer,typeof(Native.Config));
                if(config.Dependencies==IntPtr.Zero)return new string[0];long offset=config.Dependencies.ToInt64()-buffer.ToInt64();Require(offset>=0 && offset+2<=size,"Dependency pointer outside config");
                byte[] bytes=new byte[(int)(size-offset)];Marshal.Copy(config.Dependencies,bytes,0,bytes.Length);string text=Encoding.Unicode.GetString(bytes);
                if(text[0]=='\0')return new string[0];int end=text.IndexOf("\0\0",StringComparison.Ordinal);Require(end>=0,"Unterminated dependency MULTI_SZ");return text.Substring(0,end).Split('\0');
            }finally{Marshal.FreeHGlobal(buffer);}
        }
        [DllImport("kernel32.dll")] static extern ulong GetTickCount64();
        static string BootTime(){using(var query=new global::System.Management.ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem"))foreach(global::System.Management.ManagementObject item in query.Get())return global::System.Management.ManagementDateTimeConverter.ToDateTime(Convert.ToString(item["LastBootUpTime"])).ToUniversalTime().ToString("o");throw new InvalidOperationException("Windows boot metadata unavailable");}
        static object BootMetadata(){using(var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\AppIDSvc"))return Obj("runId",RunId,"bootTimeUtc",BootTime(),"uptimeMilliseconds",GetTickCount64(),"observedAtUtc",DateTimeOffset.UtcNow.ToString("o"),"appIdStartType",key==null?-1:Convert.ToInt32(key.GetValue("Start",-1)));}
        static object ArmBoot(){
            Require(!File.Exists(Join("boot-armed.json")),"Boot experiment already armed");
            var state=Read(Join(@"state\service-status.json"));Require((string)state["runId"]==RunId && (bool)state["desiredBlocked"] && state["policyReadbackBlocked"] is bool && (bool)state["policyReadbackBlocked"] && state["fault"]==null,"A healthy blocked policy is required before arming boot");
            using(var manager=Native.Manager())using(var service=Native.Open(manager,Native.ChangeConfig|Native.QueryConfig|Native.QueryStatus|Native.ReadControl)){
                Native.ValidateOwned(service);Require(Native.GetStatus(service).State==4,"Owned running service required");
                var previous=Dependencies(service);Require(previous.Length==0 || previous.SequenceEqual(new[]{"AppIDSvc"},StringComparer.OrdinalIgnoreCase),"Unexpected existing dependency; refused overwrite");
                using(var appId=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\AppIDSvc"))Require(appId!=null && Convert.ToInt32(appId.GetValue("Start",4))!=4,"AppIDSvc must not be disabled");
                // Only our service is changed. SCM starts its manual AppIDSvc dependency as needed.
                Native.Check(Native.ChangeServiceConfigW(service,0xFFFFFFFF,2,0xFFFFFFFF,null,null,IntPtr.Zero,"AppIDSvc\0\0",null,null,null),"Set own automatic start and AppIDSvc dependency");
                var definition=Native.Definition(service);Require(Convert.ToUInt32(definition["StartType"])==2 && SecurityMatches(Native.Security(service),ProbeSid) && Dependencies(service).SequenceEqual(new[]{"AppIDSvc"},StringComparer.OrdinalIgnoreCase),"Automatic-start/dependency/security readback mismatch");
            }
            var result=Obj("runId",RunId,"automaticStartArmed",true,"serviceLeftRunning",true,"onlyOwnedServiceChanged",true,"appIdDependencyArmed",true);
            using(var file=new FileStream(Join("boot-armed.json"),FileMode.CreateNew,FileAccess.Write,FileShare.Read))using(var text=new StreamWriter(file,Utf8))text.Write(Json.Serialize(result));return result;
        }
        static object Crash(){using(var manager=Native.Manager())using(var service=Native.Open(manager,Native.Custom|Native.QueryConfig)){
            Native.ValidateOwned(service);Require(!File.Exists(Join("crash-once.marker")),"Controlled crash already used for this run");Native.ControlStatus status;Native.Check(Native.ControlService(service,128,out status),"One-time self-crash control");return Obj("runId",RunId,"crashRequested",true);
        }}
        static object Cleanup(){
            using(var manager=Native.Manager()){
                if(!Native.Absent(manager))using(var service=Native.Open(manager,Native.All)){
                    Native.ValidateOwned(service);Native.Check(Native.ChangeServiceConfigW(service,0xFFFFFFFF,4,0xFFFFFFFF,null,null,IntPtr.Zero,null,null,null,null),"Disable owned service before stopping");
                    if(Native.GetStatus(service).State!=1){Native.ControlStatus status;bool good=Native.ControlService(service,1,out status);if(!good && Marshal.GetLastWin32Error()!=1062)Native.Check(false,"Stop owned service");Native.WaitState(service,1,25);}
                    Native.Check(Native.DeleteService(service),"Delete owned service");
                }
                var watch=Stopwatch.StartNew();bool absent=false;
                while(watch.Elapsed.TotalSeconds<10){using(var check=Native.OpenServiceW(manager,Name,Native.QueryStatus)){if(check.IsInvalid){int code=Marshal.GetLastWin32Error();if(code==1060){absent=true;break;}Require(code==1072,"Service absence query failed");}}Thread.Sleep(100);}
                Require(absent,"Service removal unconfirmed");
            }
            using(var session=CimSession.Create(null)){
                // Check every owned/foreign shape before deleting anything, EXE first, companion last.
                InspectPolicy(session,true);Remove(session,false,true);Remove(session,true,true);var after=InspectPolicy(session,true);
                Require(!(bool)after["exePresent"] && !(bool)after["companionPresent"],"Policy cleanup unconfirmed");
            }
            return Obj("runId",RunId,"serviceAbsent",true,"ownPoliciesAbsent",true,"invocationId",Guid.NewGuid().ToString("N"));
        }
        static object Probe(string phase){
            Require(new[]{"baseline","blocked","during-crash","after-crash","restored","boot-window","after-boot"}.Contains(phase),"Unknown probe phase");bool shouldBlock=phase!="baseline" && phase!="restored",pass=true;
            long probeStartTick=Stopwatch.GetTimestamp();ulong probeUptime=GetTickCount64();string probeStartedAt=DateTimeOffset.UtcNow.ToString("o");
            var transition=new List<Dictionary<string,object>>();
            if(phase=="blocked" || phase=="restored"){
                var wait=Stopwatch.StartNew();bool seen=false;
                while(wait.Elapsed.TotalSeconds<40){
                    var attempt=Obj("startTick",Stopwatch.GetTimestamp(),"nativeErrorCode",0,"started",false,"markerMatched",false);
                    try{using(var child=Process.Start(new ProcessStartInfo(Join(@"Targets\Target.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true})){
                        attempt["started"]=true;Require(child.WaitForExit(3000),"Transition fixture timeout");attempt["markerMatched"]=child.ExitCode==0 && child.StandardOutput.ReadToEnd().Trim()=="AICT_FIXTURE_OK" && child.StandardError.ReadToEnd().Length==0;
                    }}catch(Win32Exception e){attempt["nativeErrorCode"]=e.NativeErrorCode;}
                    attempt["endTick"]=Stopwatch.GetTimestamp();transition.Add(attempt);
                    bool denied=!(bool)attempt["started"] && (int)attempt["nativeErrorCode"]==1260,allowed=(bool)attempt["started"] && (bool)attempt["markerMatched"];
                    if(phase=="blocked"?denied:allowed){seen=true;break;}
                    Require(denied || allowed,"Unexpected transition launch failure");Thread.Sleep(50);
                }
                Require(seen,"Scheduled block/recovery transition not observed within 40 seconds");
            }
            var trials=new List<Dictionary<string,object>>();
            for(int repetition=0;repetition<3;repetition++)foreach(string relative in new[]{@"Targets\Target.exe",@"Targets\Renamed.exe","MovedTarget.exe",@"Targets\AllowedControl.exe"}){
                bool control=relative.EndsWith("AllowedControl.exe",StringComparison.Ordinal);var row=Obj("relativePath",relative,"repetition",repetition+1,"started",false,"nativeErrorCode",0,"markerMatched",false,"expectedBlocked",shouldBlock && !control,"expectationMet",false,"startTick",Stopwatch.GetTimestamp());
                if(phase!="baseline" && phase!="restored")using(var manager=Native.Manager())using(var service=Native.Open(manager,Native.QueryStatus)){var observed=Native.GetStatus(service);row["serviceState"]=observed.State;row["servicePid"]=observed.Pid;}
                try{using(var child=Process.Start(new ProcessStartInfo(Join(relative)){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true})){
                    row["started"]=true;Require(child.WaitForExit(3000),"Fixture timed out");row["markerMatched"]=child.ExitCode==0 && child.StandardOutput.ReadToEnd().Trim()==(control?"AICT_CONTROL_OK":"AICT_FIXTURE_OK") && child.StandardError.ReadToEnd().Length==0;
                }}catch(Win32Exception error){row["nativeErrorCode"]=error.NativeErrorCode;}
                bool correct=shouldBlock && !control?!(bool)row["started"] && (int)row["nativeErrorCode"]==1260:(bool)row["started"] && (bool)row["markerMatched"];
                row["expectationMet"]=correct;row["endTick"]=Stopwatch.GetTimestamp();pass&=correct;trials.Add(row);
            }
            var report=Obj("runId",RunId,"phase",phase,"limitedToken",!Admin(),"passed",pass,"clockFrequency",Stopwatch.Frequency,"transitionAttempts",transition,"trials",trials);
            if(phase=="boot-window" || phase=="after-boot") {report["windowsBootTimeUtc"]=BootTime();report["probeStartTick"]=probeStartTick;report["probeStartedAtUtc"]=probeStartedAt;report["probeStartUptimeMilliseconds"]=probeUptime;report["coverage"]="POST_LOGON_TASK_OBSERVATIONS_ONLY";}
            if(phase=="blocked" || phase=="after-crash" || phase=="after-boot") {var permission=Permissions();report["permissions"]=permission;pass&=(bool)permission["passed"];report["passed"]=pass;}
            using(var file=new FileStream(Join("probe\\"+phase+".json"),FileMode.CreateNew,FileAccess.Write,FileShare.Read))using(var text=new StreamWriter(file,Utf8))text.Write(Json.Serialize(report));
            Require(pass,"Fixture launch or service permission expectations failed");return report;
        }
        static Dictionary<string,object> Permissions(){
            bool passed=true;var checks=new List<Dictionary<string,object>>();uint pid;
            using(var manager=Native.Manager()){
                using(var service=Native.Open(manager,Native.QueryStatus)){var state=Native.GetStatus(service);Require(state.State==4 && state.Pid>0,"Running SYSTEM service required");pid=state.Pid;}
                foreach(var operation in new Dictionary<string,uint>{{"Stop",Native.Stop},{"Reconfigure",Native.ChangeConfig},{"Delete",Native.Delete},{"ChangeDacl",Native.WriteDac},{"CustomControl",Native.Custom},{"Start",Native.Start}}){
                    using(var service=Native.OpenServiceW(manager,Name,operation.Value)){int code=service.IsInvalid?Marshal.GetLastWin32Error():0;bool denied=service.IsInvalid && code==5;passed&=denied;checks.Add(Obj("operation",operation.Key,"denied",denied,"nativeErrorCode",code));}
                }
            }
            IntPtr handle=Native.OpenProcess(1,false,pid);int error=handle==IntPtr.Zero?Marshal.GetLastWin32Error():0;if(handle!=IntPtr.Zero)Native.CloseHandle(handle);
            bool terminateDenied=handle==IntPtr.Zero && error==5;passed&=terminateDenied;checks.Add(Obj("operation","TerminateService","denied",terminateDenied,"nativeErrorCode",error));
            foreach(string relative in new[]{"config.json","protection-canary.bin"}){
                bool denied=false;string failure=null;try{using(var file=new FileStream(Join(relative),FileMode.Open,FileAccess.Write,FileShare.ReadWrite)){} }catch(UnauthorizedAccessException){denied=true;}catch(Exception e){failure=e.GetType().Name;}
                passed&=denied && failure==null;checks.Add(Obj("operation","Write "+relative,"denied",denied,"unexpectedFailure",failure));
            }
            return Obj("passed",passed,"checks",checks,"noStopKillDeleteOrFileBytesWritten",true);
        }
        static int Main(string[] args){
            Console.OutputEncoding=Utf8;
            try{
                if(args.Length==1 && args[0]=="--validate"){
                    string id="12345678123412341234123456789abc",xml=AppLockerXml.Exe(id,"S-1-5-21-1-2-3-1001",FixtureIdentity.TargetSipHash,FixtureIdentity.TargetSize);
                    Require(AppLockerXml.Equivalent(xml,WebUtility.HtmlEncode(xml)) && !AppLockerXml.Equivalent(xml,xml.Replace("Action=\"Deny\"","Action=\"Allow\"")),"XML contracts failed");Console.WriteLine("Offline fixed policy contracts passed; no VM/SCM/policy access");return 0;
                }
                Require(args.Length>=1 && new[]{"--install","--inspect","--crash","--cleanup","--policy-read","--policy-block","--policy-allow","--probe","--arm-boot","--boot-metadata"}.Contains(args[0]),"Unknown mode");
                Require(args.Length==(args[0]=="--probe"?2:1),"Unexpected arguments");Guard(args[0]);object result;
                if(args[0]=="--probe")result=Probe(args[1]);
                else if(args[0]=="--install")result=Install();else if(args[0]=="--inspect")result=Inspect();else if(args[0]=="--crash")result=Crash();
                else if(args[0]=="--arm-boot")result=ArmBoot();else if(args[0]=="--boot-metadata")result=BootMetadata();
                else {
                    // Fixed policy workers serialize with independent recovery; no caller-selected lock path.
                    FileStream locked=null;var wait=Stopwatch.StartNew();while(locked==null){try{locked=new FileStream(Join("policy-operation.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}catch(IOException){if(wait.Elapsed.TotalSeconds>25)throw;Thread.Sleep(100);}}
                    using(locked)result=args[0]=="--cleanup"?Cleanup():Policy(args[0]);
                }
                Console.WriteLine(Json.Serialize(result));return 0;
            }catch(Exception error){
                var detail=Obj("type",error.GetType().Name,"message",error.Message,"hResult",error.HResult);var cim=error as CimException;
                if(cim!=null){detail["nativeErrorCode"]=cim.NativeErrorCode.ToString();detail["statusCode"]=cim.StatusCode.ToString();detail["messageId"]=cim.MessageId;}
                Console.Error.WriteLine(Json.Serialize(detail));return 1;
            }
        }
    }
}
