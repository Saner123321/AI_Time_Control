// Phase-0 Authenticode hash identity and 20-cycle latency harness. Not the product.
// Worker/cleanup: VMware LocalSystem. Probe: same guest's interactive limited token.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Xml;
using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;
using Microsoft.Win32;
using MiType=Microsoft.Management.Infrastructure.CimType;

namespace AictEnforceLab {
    static class Program {
        const string Namespace=@"root\cimv2\mdm\dmmap";
        const string ClassName="MDM_AppLocker_ApplicationLaunchRestrictions01_EXE03";
        const string StoreClass="MDM_AppLocker_ApplicationLaunchRestrictions01_StoreApps03";
        const string Prefix="./Vendor/MSFT/AppLocker/ApplicationLaunchRestrictions/AICT-Identity-";
        const string AllowId="1eb37953-5cf8-40e2-8fcf-2a6615bca537";
        const string DenyId="bb6b7a3a-7cf2-4b36-b7bb-c17c66a93016";
        const int CycleCount=20;
        static readonly JavaScriptSerializer Json=new JavaScriptSerializer {MaxJsonLength=1048576};
        static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
        static string Root,RunId;
        static Dictionary<string,object> Report;
        static Dictionary<string,object> Obj(params object[] fields) {
            var result=new Dictionary<string,object>();for(int i=0;i<fields.Length;i+=2)result.Add((string)fields[i],fields[i+1]);return result;
        }
        static void Require(bool value,string message) {if(!value)throw new InvalidOperationException(message);}
        static bool IsVmware() {
            using(var search=new ManagementObjectSearcher("SELECT Manufacturer,Model FROM Win32_ComputerSystem"))
                foreach(ManagementObject row in search.Get())if(Convert.ToString(row["Manufacturer"]).Contains("VMware") && Convert.ToString(row["Model"]).Contains("VMware"))return true;
            return false;
        }
        static bool IsAdmin() {return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);}
        static string Join(string name) {return Path.Combine(Root,name);}
        static void Guard(string mode) {
            // These checks precede all file creation and all MI/policy access.
            Require(IsVmware(),"VMware guest required; no files, tasks or policy access performed.");
            bool system=WindowsIdentity.GetCurrent().User.Value=="S-1-5-18";
            Require(mode=="--probe"?!system && !IsAdmin() && Process.GetCurrentProcess().SessionId>0:system,
                mode=="--probe"?"Probe requires an interactive, non-elevated token.":"Policy worker requires LocalSystem through the fixed guest runner.");
            Root=Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string directory=Path.GetFileName(Root);
            Require(directory.StartsWith("AICT-Identity-",StringComparison.Ordinal),"Unexpected lab location");
            RunId=directory.Substring("AICT-Identity-".Length);Guid id;
            Require(Guid.TryParseExact(RunId,"N",out id),"Invalid run ID");
            string expected=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),directory);
            Require(string.Equals(Path.GetFullPath(expected),Root,StringComparison.OrdinalIgnoreCase),"Protected ProgramData directory required");
            foreach(string path in cleanupDirectories(mode))
                Require(Directory.Exists(path) && (File.GetAttributes(path)&FileAttributes.ReparsePoint)==0,"Missing directory or reparse point refused");
            // Recovery must still work when a fixture/config has been damaged or removed.
            if(mode=="--cleanup")return;
            var config=Read(Join("config.json"));
            Require((string)config["RunId"]==RunId,"Config run identity mismatch");
            if(mode=="--probe")Require((string)config["ProbeSid"]==WindowsIdentity.GetCurrent().User.Value,"Unexpected probe identity");
            foreach(string relative in Fixtures()) {
                string path=Join(relative);
                Require(File.Exists(path) && (File.GetAttributes(path)&FileAttributes.ReparsePoint)==0,"Missing/reparse sample refused");
                string hash=Hash(path);
                bool control=relative==@"Targets\AllowedControl.exe";
                Require(hash==(control?FixtureIdentity.ControlSha256:FixtureIdentity.TargetSha256),"Frozen fixture hash mismatch");
                string sip=AuthenticodeHash.Compute(path);
                Require(control?sip!=FixtureIdentity.TargetSipHash:sip==FixtureIdentity.TargetSipHash,"SIP identity mismatch");
            }
            Require(AuthenticodeHash.Compute(Assembly.GetExecutingAssembly().Location)!=FixtureIdentity.TargetSipHash,"Worker must not share target identity");
        }
        static string[] cleanupDirectories(string mode) {return mode=="--cleanup"?new string[]{Root}:new string[]{Root,Join("Targets"),Join("probe")};}
        static string[] Fixtures() {return new string[]{@"Targets\Target.exe",@"Targets\Renamed.exe",@"Targets\AllowedControl.exe","MovedTarget.exe"};}
        static string Hash(string path) {using(var sha=SHA256.Create())using(var file=File.OpenRead(path))return BitConverter.ToString(sha.ComputeHash(file)).Replace("-","").ToLowerInvariant();}
        static Dictionary<string,object> Read(string path) {
            var info=new FileInfo(path);Require(info.Exists && info.Length<=1048576 && (info.Attributes&FileAttributes.ReparsePoint)==0,"Missing/oversized/reparse JSON");
            return Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(path,Utf8));
        }
        static void Write(string path,object value) {
            string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            File.WriteAllText(temp,Json.Serialize(value),Utf8);File.Move(temp,path); // never overwrite earlier evidence
        }
        static void Journal(string stage) {
            Report["LastStage"]=stage;
            File.AppendAllText(Join("worker-journal.jsonl"),Json.Serialize(Obj("at_utc",DateTimeOffset.UtcNow.ToString("o"),"stage",stage))+Environment.NewLine,Utf8);
        }
        static Dictionary<string,object> Error(Exception error) {
            var result=Obj("Type",error.GetType().Name,"HResult",error.HResult,"Message",error.Message);
            var cim=error as CimException;if(cim!=null) {result["NativeErrorCode"]=cim.NativeErrorCode.ToString();result["StatusCode"]=cim.StatusCode.ToString();result["MessageId"]=cim.MessageId;}
            return result;
        }
        static string Policy(string root,bool store=false) {
            var doc=new XmlDocument {XmlResolver=null};var collection=doc.CreateElement("RuleCollection");doc.AppendChild(collection);
            collection.SetAttribute("Type",store?"Appx":"Exe");collection.SetAttribute("EnforcementMode","Enabled");
            if(store) {
                var allow=doc.CreateElement("FilePublisherRule");collection.AppendChild(allow);
                allow.SetAttribute("Id",AllowId);allow.SetAttribute("Name","AICT temporary allow all signed packaged apps");
                allow.SetAttribute("Description","Companion safety rule while EXE enforcement is active");allow.SetAttribute("UserOrGroupSid","S-1-1-0");allow.SetAttribute("Action","Allow");
                var conditions=doc.CreateElement("Conditions");allow.AppendChild(conditions);var publisher=doc.CreateElement("FilePublisherCondition");conditions.AppendChild(publisher);
                foreach(string attribute in new string[]{"PublisherName","ProductName","BinaryName"})publisher.SetAttribute(attribute,"*");
                var version=doc.CreateElement("BinaryVersionRange");publisher.AppendChild(version);version.SetAttribute("LowSection","0.0.0.0");version.SetAttribute("HighSection","*");
                return doc.OuterXml;
            }
            foreach(string action in new string[]{"Allow","Deny"}) {
                var rule=doc.CreateElement(action=="Allow"?"FilePathRule":"FileHashRule");collection.AppendChild(rule);
                rule.SetAttribute("Id",action=="Allow"?AllowId:DenyId);rule.SetAttribute("Name","AICT frozen fixture identity "+action);
                rule.SetAttribute("Description","Temporary Authenticode hash phase-0 experiment");
                rule.SetAttribute("UserOrGroupSid","S-1-1-0");rule.SetAttribute("Action",action);
                var conditions=doc.CreateElement("Conditions");rule.AppendChild(conditions);
                if(action=="Allow") {var path=doc.CreateElement("FilePathCondition");conditions.AppendChild(path);path.SetAttribute("Path","*");}
                else {
                    var condition=doc.CreateElement("FileHashCondition");conditions.AppendChild(condition);
                    var hash=doc.CreateElement("FileHash");condition.AppendChild(hash);hash.SetAttribute("Type","SHA256");hash.SetAttribute("Data",FixtureIdentity.TargetSipHash);
                    hash.SetAttribute("SourceFileName","AictIdentityTarget.exe");hash.SetAttribute("SourceFileLength",FixtureIdentity.TargetSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            return doc.OuterXml;
        }
        static string Normalize(string value) {
            Require(value!=null && value.Length<=65536,"Missing or oversized policy");string text=value.Trim();
            if(!text.StartsWith("<",StringComparison.Ordinal))text=WebUtility.HtmlDecode(text);
            Require(text.StartsWith("<RuleCollection",StringComparison.Ordinal) || text.StartsWith("<?xml",StringComparison.Ordinal),"Only raw XML or one HTML encoding accepted");return text;
        }
        static bool PolicyMatches(string value,string root,bool store=false) {
            var doc=new XmlDocument {XmlResolver=null};
            using(var reader=XmlReader.Create(new StringReader(Normalize(value)),new XmlReaderSettings {DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=65536}))doc.Load(reader);
            // Exact generated shape, including IDs, allow-all and frozen Authenticode identity.
            // Attribute order is normalized by comparing attributes; whitespace is ignored.
            var expected=new XmlDocument {XmlResolver=null};expected.LoadXml(Policy(root,store));
            return SameNode(doc.DocumentElement,expected.DocumentElement);
        }
        static bool SameNode(XmlElement actual,XmlElement expected) {
            if(actual==null || actual.Name!=expected.Name || actual.Attributes.Count!=expected.Attributes.Count)return false;
            foreach(XmlAttribute attribute in expected.Attributes)if(actual.GetAttribute(attribute.Name)!=attribute.Value)return false;
            var a=new List<XmlElement>();var b=new List<XmlElement>();
            foreach(XmlNode node in actual.ChildNodes) {if(node is XmlElement)a.Add((XmlElement)node);else if(!string.IsNullOrWhiteSpace(node.Value))return false;}
            foreach(XmlNode node in expected.ChildNodes)if(node is XmlElement)b.Add((XmlElement)node);
            return a.Count==b.Count && a.Select((node,index)=>SameNode(node,b[index])).All(value=>value);
        }
        static string Property(CimInstance instance,string name) {return Convert.ToString(instance.CimInstanceProperties[name].Value);}
        static List<CimInstance> Query(CimSession session,bool owned,bool store=false) {
            string query="SELECT * FROM "+(store?StoreClass:ClassName)+(owned?" WHERE ParentID='"+Prefix+RunId+"' AND InstanceID='"+(store?"StoreApps":"EXE")+"'":"");
            using(var options=new CimOperationOptions {Timeout=TimeSpan.FromSeconds(8)})return session.QueryInstances(Namespace,"WQL",query,options).ToList();
        }
        static void DisposeRows(List<CimInstance> rows) {foreach(var row in rows)row.Dispose();}
        static bool RemoveCollection(CimSession session,bool store) {
            var rows=Query(session,true,store);
            try {
                foreach(var row in rows) {
                    Require(Property(row,"ParentID")==Prefix+RunId && Property(row,"InstanceID")==(store?"StoreApps":"EXE") && PolicyMatches(Property(row,"Policy"),Root,store),"Refusing deletion of unexpected policy contents/identity");
                    using(var options=new CimOperationOptions {Timeout=TimeSpan.FromSeconds(8)})session.DeleteInstance(Namespace,row,options);
                }
            } finally {DisposeRows(rows);}
            rows=Query(session,true,store);try{return rows.Count==0;}finally{DisposeRows(rows);}
        }
        static bool RemoveOwned(CimSession session) {
            // Never remove the companion Appx allow while our EXE enforcement is still present.
            return RemoveCollection(session,false) && RemoveCollection(session,true);
        }
        static void CreateCollection(CimSession session,bool store) {
            string xml=Policy(Root,store);Require(PolicyMatches(xml,Root,store) && PolicyMatches(WebUtility.HtmlEncode(xml),Root,store),"Fixed policy contract invalid");
            using(var instance=new CimInstance(store?StoreClass:ClassName,Namespace)) {
                instance.CimInstanceProperties.Add(CimProperty.Create("ParentID",Prefix+RunId,MiType.String,CimFlags.Key));
                instance.CimInstanceProperties.Add(CimProperty.Create("InstanceID",store?"StoreApps":"EXE",MiType.String,CimFlags.Key));
                instance.CimInstanceProperties.Add(CimProperty.Create("Policy",WebUtility.HtmlEncode(xml),MiType.String,CimFlags.Property));
                using(var options=new CimOperationOptions {Timeout=TimeSpan.FromSeconds(8)})using(var created=session.CreateInstance(Namespace,instance,options)) {}
            }
            var rows=Query(session,true,store);
            try {Require(rows.Count==1 && PolicyMatches(Property(rows[0],"Policy"),Root,store),"Enforce readback mismatch");}finally{DisposeRows(rows);}
        }
        static bool RegistryPolicyPresent() {using(var key=Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\SrpV2"))return key!=null;}
        static Dictionary<string,object> WaitFor(string relative,int seconds,bool honorAbort) {
            var watch=Stopwatch.StartNew();string path=Join(relative);
            while(watch.Elapsed.TotalSeconds<seconds) {
                if(File.Exists(path)) {var value=Read(path);Require((string)value["RunId"]==RunId,"Handshake run mismatch");return value;}
                if(honorAbort && File.Exists(Join("abort.json")))throw new InvalidOperationException("Experiment aborted; inspect worker result");
                Thread.Sleep(100);
            }
            throw new TimeoutException("Timed out waiting for "+relative);
        }
        static bool Good(Dictionary<string,object> value,string key) {return value.ContainsKey(key) && value[key] is bool && (bool)value[key];}
        static void Signal(string relative,params object[] fields) {
            var value=Obj("RunId",RunId,"AtUtc",DateTimeOffset.UtcNow.ToString("o"));
            for(int i=0;i<fields.Length;i+=2)value.Add((string)fields[i],fields[i+1]);Write(Join(relative),value);
        }
        static string CycleFile(int cycle,string phase) {return "cycle-"+cycle.ToString("00")+"-"+phase+".json";}
        static int Worker(bool cleanupOnly) {
            var cycles=new List<Dictionary<string,object>>();
            Report=Obj("SchemaVersion",1,"Kind","APPLOCKER_IDENTITY_LATENCY_SMOKE","RunId",RunId,"CollectedAtUtc",DateTimeOffset.UtcNow.ToString("o"),
                "Mode",cleanupOnly?"Cleanup":"Enforce","PolicyScope","FIXTURE_AUTHENTICODE_SHA256_ONLY","LocalSystem",true,"MutationAttempted",false,
                "OwnInstancesAbsent",false,"FunctionalPassed",false,"TimingTargetMet",false,"BatchPassed",false,"Phase0PassAwarded",false,
                "RequestedCycles",CycleCount,"CompletedCycles",0,"Cycles",cycles,"TargetSipHash",FixtureIdentity.TargetSipHash,
                "RegistryKeyPresentBefore",RegistryPolicyPresent(),"Error",null,"CleanupError",null,"Outcome","NOT_RUN");
            CimSession session=null;
            try {
                session=CimSession.Create(null);
                if(cleanupOnly) {
                    if(!File.Exists(Join("abort.json")))Signal("abort.json","Reason","Independent cleanup invoked");
                    Journal("Independent exact hash-policy cleanup");Report["OwnInstancesAbsent"]=RemoveOwned(session);Report["Outcome"]="CLEANUP_ONLY";
                } else {
                    Journal("Waiting for limited-token baseline");var baseline=WaitFor(@"probe\baseline.json",30,false);
                    Require(Good(baseline,"BaselinePassed") && Good(baseline,"LimitedToken"),"Interactive baseline failed");
                    using(var services=new ManagementObjectSearcher("SELECT State FROM Win32_Service WHERE Name='AppIDSvc'")) {
                        bool running=false;foreach(ManagementObject item in services.Get())running|=Convert.ToString(item["State"])=="Running";Require(running,"AppIDSvc must already be running");
                    }
                    foreach(bool store in new bool[]{false,true}) {
                        var rows=Query(session,false,store);try {Report[store?"BaselineStoreInstanceCount":"BaselineInstanceCount"]=rows.Count;Require(rows.Count==0 && !RegistryPolicyPresent(),"Existing policy; no merge or overwrite attempted");}finally{DisposeRows(rows);}
                    }
                    Require(!File.Exists(Join("abort.json")),"Cleanup already requested; refusing new policy");
                    Report["MutationAttempted"]=true;Journal("Creating companion allow-all signed Appx policy");CreateCollection(session,true);Report["CompanionAppxAllowVerified"]=true;
                    double maxBlock=0,maxAllow=0;bool functional=true;
                    for(int cycle=1;cycle<=CycleCount;cycle++) {
                        Require(!File.Exists(Join("abort.json")),"Cleanup requested");
                        var row=Obj("Cycle",cycle,"ClockFrequency",Stopwatch.Frequency,"ApplyStartTick",Stopwatch.GetTimestamp(),"CreateReadbackVerified",false,"ExeAbsent",false,"FunctionalPassed",false);
                        cycles.Add(row);Journal("Cycle "+cycle+": applying fixed Authenticode hash deny");
                        // Capture immediately before the provider call, not before journaling.
                        row["ApplyStartTick"]=Stopwatch.GetTimestamp();CreateCollection(session,false);row["CreateReadbackVerified"]=true;
                        Signal(CycleFile(cycle,"applied"),"Cycle",cycle,"ApplyStartTick",row["ApplyStartTick"],"ClockFrequency",Stopwatch.Frequency);
                        var blocked=WaitFor(Path.Combine("probe",CycleFile(cycle,"blocked")),30,false);
                        Require(Convert.ToInt32(blocked["Cycle"])==cycle,"Blocked cycle mismatch");
                        row["BlockPhasePassed"]=Good(blocked,"BlockedPhasePassed");
                        row["FirstObservedBlockSeconds"]=blocked["FirstObservedBlockSeconds"];
                        row["RemoveStartTick"]=Stopwatch.GetTimestamp();row["ExeAbsent"]=RemoveCollection(session,false);Require(Good(row,"ExeAbsent"),"EXE instance remains");
                        Signal(CycleFile(cycle,"removed"),"Cycle",cycle,"RemoveStartTick",row["RemoveStartTick"],"ClockFrequency",Stopwatch.Frequency,"ExePolicyAbsent",true);
                        var recovery=WaitFor(Path.Combine("probe",CycleFile(cycle,"recovered")),30,false);
                        Require(Convert.ToInt32(recovery["Cycle"])==cycle,"Recovery cycle mismatch");
                        row["RecoveryPhasePassed"]=Good(recovery,"RecoveryPassed");row["FirstObservedAllowSeconds"]=recovery["FirstObservedAllowSeconds"];
                        row["FunctionalPassed"]=Good(row,"BlockPhasePassed") && Good(row,"RecoveryPhasePassed");functional&=Good(row,"FunctionalPassed");
                        if(blocked["FirstObservedBlockSeconds"]!=null)maxBlock=Math.Max(maxBlock,Convert.ToDouble(blocked["FirstObservedBlockSeconds"]));else functional=false;
                        if(recovery["FirstObservedAllowSeconds"]!=null)maxAllow=Math.Max(maxAllow,Convert.ToDouble(recovery["FirstObservedAllowSeconds"]));else functional=false;
                        Report["CompletedCycles"]=cycle;
                        Require(Good(row,"FunctionalPassed"),"Identity/control/recovery observations did not match; remaining cycles skipped");
                    }
                    Report["FunctionalPassed"]=functional;Report["MaxObservedBlockSeconds"]=maxBlock;Report["MaxObservedAllowSeconds"]=maxAllow;
                    Report["TimingTargetMet"]=functional && maxBlock<=2.0 && maxAllow<=2.0;
                }
            } catch(Exception error) {Report["Error"]=Error(error);Report["Outcome"]="FAILED_OR_INCOMPLETE";}
            finally {
                if(session!=null && !cleanupOnly) {
                    try {Journal("Final cleanup: EXE first, then companion Appx");Report["OwnInstancesAbsent"]=RemoveOwned(session);}catch(Exception error){Report["CleanupError"]=Error(error);}
                }
                if(session!=null)session.Dispose();
                if(!cleanupOnly) {
                    if(Report["Error"]!=null || !Good(Report,"OwnInstancesAbsent")) {
                        if(!File.Exists(Join("abort.json")))Signal("abort.json","Reason","Worker incomplete; inspect reports and cleanup state");
                    } else {
                        Signal("done.json","PolicyAbsent",true);
                        try {
                            var probe=WaitFor(@"probe\probe-result.json",10,false);
                            Report["ProbeCompleted"]=Good(probe,"ProbeCompleted");
                        }catch(Exception error){Report["ProbeCompletionError"]=Error(error);}
                    }
                    Report["FunctionalPassed"]=Good(Report,"FunctionalPassed") && Good(Report,"OwnInstancesAbsent") && Good(Report,"ProbeCompleted") && Report["Error"]==null && Report["CleanupError"]==null;
                    Report["BatchPassed"]=Good(Report,"FunctionalPassed") && Good(Report,"TimingTargetMet");
                    if(Good(Report,"BatchPassed"))Report["Outcome"]="HASH_IDENTITY_20_CYCLES_PASS_NOT_FULL_PHASE0";
                    else if(Good(Report,"FunctionalPassed"))Report["Outcome"]="HASH_IDENTITY_PASS_TIMING_TARGET_NOT_MET";
                }
                Report["RegistryKeyPresentAfter"]=RegistryPolicyPresent();Journal("Final result saved");
                Write(Join(cleanupOnly?"cleanup-result-"+DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ")+".json":"worker-result.json"),Report);
            }
            Console.WriteLine(Json.Serialize(Report));return Good(Report,cleanupOnly?"OwnInstancesAbsent":"BatchPassed")?0:2;
        }
        static Dictionary<string,object> Launch(string relative) {
            long start=Stopwatch.GetTimestamp();
            var row=Obj("RelativePath",relative,"AttemptTick",start,"AtUtc",DateTimeOffset.UtcNow.ToString("o"),"Started",false,"Completed",false,"ExitCode",null,"MarkerMatched",false,"NativeErrorCode",null,"Error",null);
            try {
                using(var process=Process.Start(new ProcessStartInfo(Join(relative)) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true})) {
                    row["Started"]=true;
                    if(!process.WaitForExit(3000)) {process.Kill();process.WaitForExit(2000);throw new TimeoutException("Inert fixture timed out");}
                    row["Completed"]=true;row["ExitCode"]=process.ExitCode;
                    row["MarkerMatched"]=process.StandardOutput.ReadToEnd().Trim()==(relative==@"Targets\AllowedControl.exe"?"AICT_CONTROL_OK":"AICT_FIXTURE_OK") && process.StandardError.ReadToEnd().Length==0;
                }
            } catch(Win32Exception error) {row["NativeErrorCode"]=error.NativeErrorCode;row["Error"]=Error(error);}
            catch(Exception error) {row["Error"]=Error(error);}
            row["CompletedTick"]=Stopwatch.GetTimestamp();return row;
        }
        static bool Allowed(Dictionary<string,object> row) {return Good(row,"Started") && Good(row,"Completed") && Convert.ToInt32(row["ExitCode"])==0 && Good(row,"MarkerMatched") && row["Error"]==null;}
        static bool Blocked(Dictionary<string,object> row) {return !Good(row,"Started") && row["NativeErrorCode"]!=null && Convert.ToInt32(row["NativeErrorCode"])==1260;}
        static List<Dictionary<string,object>> Trials(bool blocked,out bool passed,int repetitions=1) {
            var rows=new List<Dictionary<string,object>>();passed=true;
            for(int i=0;i<repetitions;i++)foreach(string relative in Fixtures()) {
                var row=Launch(relative);row["Repetition"]=i+1;bool expectedBlock=blocked && relative!=@"Targets\AllowedControl.exe";
                row["Expected"]=expectedBlock?"POLICY_DENIED_1260":"ALLOWED_WITH_MARKER";
                bool match=expectedBlock?Blocked(row):Allowed(row);row["ExpectationMet"]=match;passed&=match;rows.Add(row);
            }
            return rows;
        }
        static List<Dictionary<string,object>> WaitForLaunch(bool expectBlocked,out Dictionary<string,object> first) {
            first=null;var attempts=new List<Dictionary<string,object>>();var watch=Stopwatch.StartNew();
            do {
                var row=Launch(@"Targets\Target.exe");attempts.Add(row);
                if(expectBlocked?Blocked(row):Allowed(row)){first=row;break;}
                if(expectBlocked?!Allowed(row):!Blocked(row))break;
                Thread.Sleep(50);
            }while(watch.Elapsed.TotalSeconds<10);
            return attempts;
        }
        static int Probe() {
            bool complete=false;var report=Obj("SchemaVersion",1,"Kind","APPLOCKER_IDENTITY_LIMITED_PROBE","RunId",RunId,"LimitedToken",!IsAdmin(),"InteractiveSession",true,"CompletedCycles",0,"Error",null);
            try {
                bool baselineGood;var baseline=Trials(false,out baselineGood,3);
                Signal(@"probe\baseline.json","LimitedToken",true,"BaselinePassed",baselineGood,"Trials",baseline);
                Require(baselineGood,"Baseline failed; policy must not be applied");
                bool all=true;
                for(int cycle=1;cycle<=CycleCount;cycle++) {
                    var applied=WaitFor(CycleFile(cycle,"applied"),40,true);Require(Convert.ToInt32(applied["Cycle"])==cycle,"Applied cycle mismatch");
                    Require(Convert.ToInt64(applied["ClockFrequency"])==Stopwatch.Frequency,"Clock frequency mismatch");
                    Dictionary<string,object> first;var attempts=WaitForLaunch(true,out first);
                    bool blockGood;var blockTrials=Trials(true,out blockGood);
                    double? blockSeconds=first==null?(double?)null:(Convert.ToInt64(first["CompletedTick"])-Convert.ToInt64(applied["ApplyStartTick"]))/ (double)Stopwatch.Frequency;
                    bool phaseGood=first!=null && blockGood;all&=phaseGood;
                    Signal(Path.Combine("probe",CycleFile(cycle,"blocked")),"Cycle",cycle,"LimitedToken",true,"BlockedPhasePassed",phaseGood,"FirstObservedBlockSeconds",blockSeconds,"PropagationAttempts",attempts,"Trials",blockTrials);
                    var removed=WaitFor(CycleFile(cycle,"removed"),40,true);Require(Convert.ToInt32(removed["Cycle"])==cycle && Good(removed,"ExePolicyAbsent"),"Removal cycle mismatch");
                    Require(Convert.ToInt64(removed["ClockFrequency"])==Stopwatch.Frequency,"Recovery clock mismatch");
                    var recoveryAttempts=WaitForLaunch(false,out first);
                    bool recoveryGood;var recoveryTrials=Trials(false,out recoveryGood);
                    double? allowSeconds=first==null?(double?)null:(Convert.ToInt64(first["CompletedTick"])-Convert.ToInt64(removed["RemoveStartTick"]))/ (double)Stopwatch.Frequency;
                    bool recovered=first!=null && recoveryGood;all&=recovered;
                    Signal(Path.Combine("probe",CycleFile(cycle,"recovered")),"Cycle",cycle,"LimitedToken",true,"RecoveryPassed",recovered,"FirstObservedAllowSeconds",allowSeconds,"PropagationAttempts",recoveryAttempts,"Trials",recoveryTrials);
                    report["CompletedCycles"]=cycle;
                }
                WaitFor("done.json",40,true);complete=all;
            }catch(Exception error){report["Error"]=Error(error);}
            finally {report["ProbeCompleted"]=complete;Write(Join(@"probe\probe-result.json"),report);}
            Console.WriteLine(Json.Serialize(report));return complete?0:2;
        }
        static int Validate() {
            int count=0;AuthenticodeHash.ValidateLayout();count++;
            string root=@"C:\ProgramData\AICT-Identity-fixture",xml=Policy(root);
            Require(PolicyMatches(xml,root) && PolicyMatches(WebUtility.HtmlEncode(xml),root),"Policy encoding contract");count++;
            Require(PolicyMatches(xml,root+"-different"),"Hash identity must not depend on directory");count++;
            Require(!PolicyMatches(xml.Replace("Enabled","AuditOnly"),root),"Audit accepted");count++;
            Require(!PolicyMatches(xml.Replace("Path=\"*\"","Path=\"C:\\only\""),root),"Allow-all missing");count++;
            Require(!PolicyMatches(xml.Replace("S-1-1-0","S-1-5-18"),root),"Wrong user scope");count++;
            Require(!PolicyMatches(xml.Replace("Type=\"SHA256\"","Type=\"SHA256Flat\""),root),"Flat hash accepted");count++;
            Require(!PolicyMatches(xml.Replace(FixtureIdentity.TargetSipHash,"0x"+new string('0',64)),root),"Wrong hash accepted");count++;
            Require(!PolicyMatches(xml.Replace(DenyId,Guid.NewGuid().ToString()),root),"Wrong rule ID accepted");count++;
            bool doubleRefused=false;try{PolicyMatches(WebUtility.HtmlEncode(WebUtility.HtmlEncode(xml)),root);}catch(InvalidOperationException){doubleRefused=true;}
            Require(doubleRefused,"Double encoding accepted");count++;
            bool dtdRefused=false;try{PolicyMatches("<?xml version=\"1.0\"?><!DOCTYPE RuleCollection [<!ENTITY x \"x\">]>"+xml,root);}catch(XmlException){dtdRefused=true;}
            Require(dtdRefused,"DTD accepted");count++;
            string appx=Policy(root,true);Require(PolicyMatches(appx,root,true) && !PolicyMatches(xml,root,true),"Appx contract");count++;
            Require(!PolicyMatches(appx.Replace("HighSection=\"*\"","HighSection=\"1.0.0.0\""),root,true),"Appx restriction accepted");count++;
            Require(Blocked(Obj("Started",false,"NativeErrorCode",1260)) && !Blocked(Obj("Started",false,"NativeErrorCode",5)),"Block error classification");count++;
            Require(!Allowed(Obj("Started",true,"Completed",true,"ExitCode",0,"MarkerMatched",false,"Error",null)),"Marker missing");count++;
            Require(CycleCount==20 && FixtureIdentity.TargetSipHash.Length==66 && FixtureIdentity.TargetSha256!=FixtureIdentity.ControlSha256,"Frozen sample contract");count++;
            Console.WriteLine(Json.Serialize(Obj("Kind","IDENTITY_TIMING_OFFLINE_CONTRACT_NOT_APPLOCKER_EXECUTION","Assertions",count,"Passed",true)));return 0;
        }
        static int Main(string[] args) {
            Console.OutputEncoding=Utf8;Console.SetError(new StreamWriter(Console.OpenStandardError(),Utf8){AutoFlush=true});
            try {
                if(args.Length==1 && args[0]=="--validate")return Validate();
                Require(args.Length==1 && new string[]{"--enforce","--cleanup","--probe"}.Contains(args[0]),"Use the fixed guest runner");
                Guard(args[0]);return args[0]=="--probe"?Probe():Worker(args[0]=="--cleanup");
            } catch(Exception error) {Console.Error.WriteLine(Json.Serialize(Error(error)));return 1;}
        }
    }
}
