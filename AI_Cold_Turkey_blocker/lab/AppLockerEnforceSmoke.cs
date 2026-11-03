// Isolated phase-0 harness, not the product. Fixed directory-scoped policy only.
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
        const string Prefix="./Vendor/MSFT/AppLocker/ApplicationLaunchRestrictions/AICT-Enforce-";
        const string AllowId="1eb37953-5cf8-40e2-8fcf-2a6615bca537";
        const string DenyId="bb6b7a3a-7cf2-4b36-b7bb-c17c66a93016";
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
            Require(directory.StartsWith("AICT-Enforce-",StringComparison.Ordinal),"Unexpected lab location");
            RunId=directory.Substring("AICT-Enforce-".Length);Guid id;
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
            string reference=null;
            foreach(string relative in Fixtures()) {
                string path=Join(relative);
                Require(File.Exists(path) && (File.GetAttributes(path)&FileAttributes.ReparsePoint)==0,"Missing/reparse sample refused");
                string hash=Hash(path);
                Require(hash==(string)config["FixtureSha256"],"Fixture hash mismatch");
                if(reference==null)reference=hash;else Require(hash==reference,"Fixture copies differ");
            }
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
                var rule=doc.CreateElement("FilePathRule");collection.AppendChild(rule);
                rule.SetAttribute("Id",action=="Allow"?AllowId:DenyId);rule.SetAttribute("Name","AICT isolated fixture "+action);
                rule.SetAttribute("Description","Temporary directory-scoped phase-0 experiment");
                rule.SetAttribute("UserOrGroupSid","S-1-1-0");rule.SetAttribute("Action",action);
                var conditions=doc.CreateElement("Conditions");rule.AppendChild(conditions);
                var path=doc.CreateElement("FilePathCondition");conditions.AppendChild(path);
                path.SetAttribute("Path",action=="Allow"?"*":Path.Combine(root,@"Targets\*"));
                if(action=="Deny") {
                    var exceptions=doc.CreateElement("Exceptions");rule.AppendChild(exceptions);
                    var exception=doc.CreateElement("FilePathCondition");exceptions.AppendChild(exception);
                    exception.SetAttribute("Path",Path.Combine(root,@"Targets\AllowedControl.exe"));
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
            // Exact generated shape, including IDs, allow-all and scoped deny exception.
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
        static int Worker(bool cleanupOnly) {
            Report=Obj("SchemaVersion",1,"Kind","APPLOCKER_ENFORCE_DIRECTORY_SMOKE","RunId",RunId,"CollectedAtUtc",DateTimeOffset.UtcNow.ToString("o"),
                "Mode",cleanupOnly?"Cleanup":"Enforce","PolicyScope","FIXTURE_DIRECTORY_PATH_ONLY","LocalSystem",true,"MutationAttempted",false,"CreateReturned",false,"ReadbackVerified",false,
                "OwnInstancesAbsent",false,"ProbeContractPassed",false,"SmokePassed",false,"LatencyGateAwarded",false,"IdentityGateAwarded",false,"Phase0PassAwarded",false,
                "RegistryKeyPresentBefore",RegistryPolicyPresent(),"Error",null,"CleanupError",null,"Outcome","NOT_RUN");
            CimSession session=null;Dictionary<string,object> blocked=null;bool baselinePassed=false;
            try {
                session=CimSession.Create(null);
                if(cleanupOnly) {
                    if(!File.Exists(Join("abort.json")))Signal("abort.json","Reason","Independent cleanup invoked");
                    Journal("Independent cleanup: exact group and exact generated policy only");
                    Report["OwnInstancesAbsent"]=RemoveOwned(session);Report["Outcome"]="CLEANUP_ONLY";
                } else {
                    Journal("Waiting for interactive limited-token baseline");var baseline=WaitFor(@"probe\baseline.json",25,false);
                    Require(Good(baseline,"BaselinePassed") && Good(baseline,"LimitedToken") && Good(baseline,"HolderReady"),"Interactive baseline/holder failed");baselinePassed=true;
                    Journal("Checking AppIDSvc without changing service settings");
                    using(var services=new ManagementObjectSearcher("SELECT State FROM Win32_Service WHERE Name='AppIDSvc'")) {
                        bool running=false;foreach(ManagementObject item in services.Get())running|=Convert.ToString(item["State"])=="Running";Require(running,"AppIDSvc must already be running");
                    }
                    foreach(bool store in new bool[]{false,true}) {
                        var rows=Query(session,false,store);try {Report[store?"BaselineStoreInstanceCount":"BaselineInstanceCount"]=rows.Count;Require(rows.Count==0 && !RegistryPolicyPresent(),"Existing policy detected; no merge or overwrite attempted");}finally{DisposeRows(rows);}
                    }
                    Require(!File.Exists(Join("abort.json")),"Cleanup already requested; refusing new policy");
                    Report["MutationAttempted"]=true;Journal("Creating and verifying companion allow-all signed packaged-app rule before EXE enforcement");
                    CreateCollection(session,true);Report["CompanionAppxAllowVerified"]=true;
                    Journal("Applying allow-all plus fixture-directory deny with a control exception");
                    Report["ApplyStartTick"]=Stopwatch.GetTimestamp();Report["ClockFrequency"]=Stopwatch.Frequency;
                    CreateCollection(session,false);Report["CreateReturned"]=true;Report["ReadbackVerified"]=true;
                    Signal("applied.json","ApplyStartTick",Report["ApplyStartTick"],"ClockFrequency",Stopwatch.Frequency);
                    Journal("Waiting for limited-token block/control observations");blocked=WaitFor(@"probe\blocked.json",25,false);
                    Report["ProbeContractPassed"]=Good(blocked,"BlockedPhasePassed");Report["FirstObservedBlockSeconds"]=blocked.ContainsKey("FirstObservedBlockSeconds")?blocked["FirstObservedBlockSeconds"]:null;
                }
            } catch(Exception error) {Report["Error"]=Error(error);Report["Outcome"]="FAILED_OR_INCOMPLETE";}
            finally {
                if(session!=null && !cleanupOnly) {
                    try {Journal("Removing only this exact generated policy");Report["OwnInstancesAbsent"]=RemoveOwned(session);}catch(Exception error){Report["CleanupError"]=Error(error);}
                }
                if(session!=null)session.Dispose();
                if(!cleanupOnly && Good(Report,"OwnInstancesAbsent")) {
                    if(!File.Exists(Join("removed.json")))Signal("removed.json","PolicyAbsent",true,"RemoveObservedTick",Stopwatch.GetTimestamp());
                    if(baselinePassed) {
                        try {
                            Journal("Waiting for actual post-cleanup execution recovery");var recovery=WaitFor(@"probe\recovered.json",25,false);
                            Report["RecoveredPhasePassed"]=Good(recovery,"RecoveryPassed");
                            Report["SmokePassed"]=Good(Report,"ReadbackVerified") && Good(Report,"ProbeContractPassed") && Good(recovery,"RecoveryPassed") && Report["Error"]==null && Report["CleanupError"]==null;
                        } catch(Exception error) {Report["RecoveryError"]=Error(error);}
                    }
                } else if(!File.Exists(Join("abort.json")))Signal("abort.json","Reason","Policy cleanup not confirmed; watchdog retained");
                if(Good(Report,"SmokePassed"))Report["Outcome"]="ENFORCE_DIRECTORY_SMOKE_PASS_NOT_FULL_PHASE0";
                Report["RegistryKeyPresentAfter"]=RegistryPolicyPresent();Journal("Result saved");
                Write(Join(cleanupOnly?"cleanup-result-"+DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ")+".json":"worker-result.json"),Report);
            }
            Console.WriteLine(Json.Serialize(Report));return Good(Report,cleanupOnly?"OwnInstancesAbsent":"SmokePassed")?0:2;
        }
        static Dictionary<string,object> Launch(string relative) {
            long start=Stopwatch.GetTimestamp();
            var row=Obj("RelativePath",relative,"AttemptTick",start,"AtUtc",DateTimeOffset.UtcNow.ToString("o"),"Started",false,"Completed",false,"ExitCode",null,"MarkerMatched",false,"NativeErrorCode",null,"Error",null);
            try {
                using(var process=Process.Start(new ProcessStartInfo(Join(relative)) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true})) {
                    row["Started"]=true;
                    if(!process.WaitForExit(3000)) {process.Kill();process.WaitForExit(2000);throw new TimeoutException("Inert fixture timed out");}
                    row["Completed"]=true;row["ExitCode"]=process.ExitCode;
                    row["MarkerMatched"]=process.StandardOutput.ReadToEnd().Trim()=="AICT_FIXTURE_OK" && process.StandardError.ReadToEnd().Length==0;
                }
            } catch(Win32Exception error) {row["NativeErrorCode"]=error.NativeErrorCode;row["Error"]=Error(error);}
            catch(Exception error) {row["Error"]=Error(error);}
            row["CompletedTick"]=Stopwatch.GetTimestamp();return row;
        }
        static bool Allowed(Dictionary<string,object> row) {return Good(row,"Started") && Good(row,"Completed") && Convert.ToInt32(row["ExitCode"])==0 && Good(row,"MarkerMatched") && row["Error"]==null;}
        static bool Blocked(Dictionary<string,object> row) {return !Good(row,"Started") && row["NativeErrorCode"]!=null && Convert.ToInt32(row["NativeErrorCode"])==1260;}
        static List<Dictionary<string,object>> Trials(bool blocked,out bool passed) {
            var rows=new List<Dictionary<string,object>>();passed=true;
            for(int i=0;i<3;i++)foreach(string relative in Fixtures()) {
                var row=Launch(relative);row["Repetition"]=i+1;bool expectedBlock=blocked && relative!=@"Targets\AllowedControl.exe" && relative!="MovedTarget.exe";
                row["Expected"]=expectedBlock?"POLICY_DENIED_1260":"ALLOWED_WITH_MARKER";
                bool match=expectedBlock?Blocked(row):Allowed(row);row["ExpectationMet"]=match;passed&=match;rows.Add(row);
            }
            return rows;
        }
        static int Probe() {
            Process holder=null;bool complete=false;var report=Obj("SchemaVersion",1,"Kind","APPLOCKER_LIMITED_TOKEN_PROBE","RunId",RunId,"LimitedToken",!IsAdmin(),"InteractiveSession",true,"Error",null);
            try {
                bool baselineGood;var baseline=Trials(false,out baselineGood);
                holder=Process.Start(new ProcessStartInfo(Join(@"Targets\Target.exe"),"--hold") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true});
                var ready=holder.StandardOutput.ReadLineAsync();bool holderReady=ready.Wait(3000) && ready.Result=="AICT_HOLD_READY" && !holder.HasExited;
                Signal(@"probe\baseline.json","LimitedToken",true,"BaselinePassed",baselineGood,"HolderReady",holderReady,"Trials",baseline);
                Require(baselineGood && holderReady,"Baseline failed; policy must not be applied");
                var applied=WaitFor("applied.json",35,true);long applyTick=Convert.ToInt64(applied["ApplyStartTick"]);
                Require(Convert.ToInt64(applied["ClockFrequency"])==Stopwatch.Frequency,"Clock frequency mismatch");
                var attempts=new List<Dictionary<string,object>>();Dictionary<string,object> first=null;var deadline=Stopwatch.StartNew();
                do {
                    var row=Launch(@"Targets\Target.exe");attempts.Add(row);if(Blocked(row)){first=row;break;}
                    // Only a successfully executed fixture can be treated as propagation delay.
                    if(!Allowed(row))break;Thread.Sleep(100);
                }while(deadline.Elapsed.TotalSeconds<10);
                bool blockGood;var blockTrials=Trials(true,out blockGood);bool existingAlive=!holder.HasExited;
                double? seconds=first==null?(double?)null:(Convert.ToInt64(first["CompletedTick"])-applyTick)/(double)Stopwatch.Frequency;
                bool phaseGood=first!=null && blockGood && existingAlive;
                Signal(@"probe\blocked.json","LimitedToken",true,"BlockedPhasePassed",phaseGood,"FirstObservedBlockSeconds",seconds,"PropagationAttempts",attempts,"Trials",blockTrials,
                    "ExistingProcessStillRunning",existingAlive,"CopyOutsideDirectoryExpectedAllowed",true,"IdentityBypassResistanceProven",false,"LatencyGateAwarded",false);
                holder.Kill();holder.WaitForExit(2000);holder.Dispose();holder=null;
                WaitFor("removed.json",35,true);deadline.Restart();bool recovered=false;var recoveryAttempts=new List<Dictionary<string,object>>();
                do {
                    var row=Launch(@"Targets\Target.exe");recoveryAttempts.Add(row);if(Allowed(row)){recovered=true;break;}
                    if(!Blocked(row))break;Thread.Sleep(100);
                }while(deadline.Elapsed.TotalSeconds<10);
                bool recoveryGood;var recoveryTrials=Trials(false,out recoveryGood);
                Signal(@"probe\recovered.json","LimitedToken",true,"RecoveryPassed",recovered && recoveryGood,"PropagationAttempts",recoveryAttempts,"Trials",recoveryTrials);
                complete=phaseGood && recovered && recoveryGood;
            } catch(Exception error) {report["Error"]=Error(error);}
            finally {
                if(holder!=null) {
                    try{if(!holder.HasExited){holder.Kill();holder.WaitForExit(2000);}}
                    catch(Exception error){report["HolderCleanupError"]=Error(error);complete=false;}
                    finally{holder.Dispose();}
                }
                report["ProbeCompleted"]=complete;Write(Join(@"probe\probe-result.json"),report);
            }
            Console.WriteLine(Json.Serialize(report));return complete?0:2;
        }
        static int Validate() {
            int count=0;string root=@"C:\ProgramData\AICT-Enforce-fixture & "+"\u6d4b\u8bd5";string xml=Policy(root);
            Require(PolicyMatches(xml,root),"raw policy");count++;
            Require(PolicyMatches(WebUtility.HtmlEncode(xml),root),"encoded policy");count++;
            Require(!PolicyMatches(xml.Replace("Enabled","AuditOnly"),root),"audit accepted");count++;
            Require(!PolicyMatches(xml.Replace("Path=\"*\"","Path=\"C:\\only\""),root),"missing allow-all accepted");count++;
            Require(!PolicyMatches(xml,root+"-other"),"other root accepted");count++;
            Require(!PolicyMatches(xml.Replace("S-1-1-0","S-1-5-18"),root),"different scope accepted");count++;
            Require(!PolicyMatches(xml.Replace("AllowedControl.exe","Target.exe"),root),"wrong exception accepted");count++;
            Require(!PolicyMatches(xml.Replace(DenyId,Guid.NewGuid().ToString()),root),"changed rule identity accepted");count++;
            Require(!PolicyMatches(xml.Replace("<Exceptions>","<Exceptions extra=\"yes\">"),root),"extra attribute accepted");count++;
            bool doubleRefused=false;try{PolicyMatches(WebUtility.HtmlEncode(WebUtility.HtmlEncode(xml)),root);}catch(InvalidOperationException){doubleRefused=true;}
            Require(doubleRefused,"double encoding accepted");count++;
            bool dtdRefused=false;try{PolicyMatches("<?xml version=\"1.0\"?><!DOCTYPE RuleCollection [<!ENTITY x \"x\">]>"+xml,root);}catch(XmlException){dtdRefused=true;}
            Require(dtdRefused,"DTD accepted");count++;
            string appx=Policy(root,true);Require(PolicyMatches(appx,root,true) && PolicyMatches(WebUtility.HtmlEncode(appx),root,true),"Appx companion invalid");count++;
            Require(!PolicyMatches(appx.Replace("HighSection=\"*\"","HighSection=\"1.0.0.0\""),root,true),"Appx restriction accepted");count++;
            Require(!PolicyMatches(appx,root,false) && !PolicyMatches(xml,root,true),"collection type mixup");count++;
            var denied=Obj("Started",false,"NativeErrorCode",1260);Require(Blocked(denied) && !Allowed(denied),"1260 classification");count++;
            var generic=Obj("Started",false,"NativeErrorCode",5);Require(!Blocked(generic),"access denied misclassified");count++;
            var badLaunch=Obj("Started",true,"Completed",true,"ExitCode",0,"MarkerMatched",false,"Error",null);Require(!Allowed(badLaunch),"unrelated exit0 accepted");count++;
            Console.WriteLine(Json.Serialize(Obj("Kind","ENFORCE_OFFLINE_CONTRACT_NOT_WINDOWS_EXECUTION","Assertions",count,"Passed",true)));return 0;
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
