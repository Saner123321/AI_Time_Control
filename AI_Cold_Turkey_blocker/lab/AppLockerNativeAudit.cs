// Phase-0 laboratory worker. Uses public Windows MI APIs, never PowerShell.
// Policy is fixed AuditOnly. This is not the product service or a blocking test.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Reflection;
using System.Security.Principal;
using System.Web.Script.Serialization;
using System.Xml;
using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;
using Microsoft.Win32;
using MiType = Microsoft.Management.Infrastructure.CimType;

namespace AictCspLab {
    static class Program {
        const string Namespace = @"root\cimv2\mdm\dmmap";
        const string ClassName = "MDM_AppLocker_ApplicationLaunchRestrictions01_EXE03";
        const string Prefix = "./Vendor/MSFT/AppLocker/ApplicationLaunchRestrictions/AICT-Phase0-";
        const string LegacyId = "375860026bf2434088478e4ac27f57fe";
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        static string Root, RunId;
        static Dictionary<string,object> Report;
        static Dictionary<string,object> Obj(params object[] fields) {
            var result=new Dictionary<string,object>();for(int i=0;i<fields.Length;i+=2)result.Add((string)fields[i],fields[i+1]);return result;
        }
        static bool IsVmware() {
            using(var search=new ManagementObjectSearcher("SELECT Manufacturer,Model FROM Win32_ComputerSystem"))
                foreach(ManagementObject row in search.Get())if(Convert.ToString(row["Manufacturer"]).Contains("VMware") && Convert.ToString(row["Model"]).Contains("VMware"))return true;
            return false;
        }
        static void Journal(string stage) {
            Report["LastStage"]=stage;
            File.AppendAllText(Path.Combine(Root,"native-journal.jsonl"),Json.Serialize(Obj("at_utc",DateTimeOffset.UtcNow.ToString("o"),"stage",stage))+Environment.NewLine,new System.Text.UTF8Encoding(false));
        }
        static Dictionary<string,object> Error(Exception error) {
            var result=Obj("Type",error.GetType().Name,"HResult",error.HResult,"Message",error.Message);
            var cim=error as CimException;
            if(cim!=null) {
                result["NativeErrorCode"]=cim.NativeErrorCode.ToString();result["StatusCode"]=cim.StatusCode.ToString();result["MessageId"]=cim.MessageId;
                var codes=new Dictionary<string,object>();
                if(cim.ErrorData!=null)foreach(var property in cim.ErrorData.CimInstanceProperties) {
                    if(new string[]{"CIMStatusCode","CIMStatusCodeDescription","ErrorType","OtherErrorType","MessageID","MessageId","Message","Error_Code","ErrorCode","StatusCode","HResult"}.Contains(property.Name)) {
                        string text=Convert.ToString(property.Value);codes[property.Name]=text.Length>2048?text.Substring(0,2048):text;
                    }
                }
                result["ExtendedCodeData"]=codes;
            }
            return result;
        }
        static string Property(CimInstance instance,string key) {
            var property=instance.CimInstanceProperties[key];if(property==null)throw new InvalidOperationException("Missing MI property: "+key);return Convert.ToString(property.Value);
        }
        static List<CimInstance> Query(CimSession session,string id) {
            string query="SELECT * FROM "+ClassName;
            if(id!=null)query+=" WHERE ParentID='"+Prefix+id+"' AND InstanceID='EXE'";
            using(var options=new CimOperationOptions {Timeout=TimeSpan.FromSeconds(8)})return session.QueryInstances(Namespace,"WQL",query,options).ToList();
        }
        static void DisposeRows(List<CimInstance> rows) {foreach(var row in rows)row.Dispose();}
        static bool Absent(CimSession session,string id) {var rows=Query(session,id);try{return rows.Count==0;}finally{DisposeRows(rows);}}
        static string Policy(string target) {
            var doc=new XmlDocument {XmlResolver=null};var collection=doc.CreateElement("RuleCollection");doc.AppendChild(collection);
            collection.SetAttribute("Type","Exe");collection.SetAttribute("EnforcementMode","AuditOnly");
            foreach(string action in new string[]{"Allow","Deny"}) {
                var rule=doc.CreateElement("FilePathRule");collection.AppendChild(rule);rule.SetAttribute("Id",Guid.NewGuid().ToString());
                rule.SetAttribute("Name","AICT native audit "+action);rule.SetAttribute("Description","Temporary audit-only lab, not enforced");rule.SetAttribute("UserOrGroupSid","S-1-1-0");rule.SetAttribute("Action",action);
                var conditions=doc.CreateElement("Conditions");rule.AppendChild(conditions);var path=doc.CreateElement("FilePathCondition");conditions.AppendChild(path);path.SetAttribute("Path",action=="Allow"?"*":target);
            }
            return doc.OuterXml;
        }
        static string EncodePolicyTransport(string xml) { return WebUtility.HtmlEncode(xml); }
        static string DecodePolicyTransport(string value,out string encoding) {
            if(value==null || value.Length>65536)throw new InvalidDataException("Missing or oversized audit policy value");
            string text=value.Trim();
            if(text.StartsWith("<RuleCollection",StringComparison.Ordinal) || text.StartsWith("<?xml",StringComparison.Ordinal)) {encoding="RAW_XML";return text;}
            string decoded=WebUtility.HtmlDecode(text);
            if(decoded.StartsWith("<RuleCollection",StringComparison.Ordinal) || decoded.StartsWith("<?xml",StringComparison.Ordinal)) {encoding="HTML_ENTITY_ENCODED_XML";return decoded;}
            throw new InvalidDataException("Policy must be XML or exactly one HTML encoding layer; recursive decode refused");
        }
        static bool AuditPolicyMatches(string xml,string target) {
            string encoding;string normalized=DecodePolicyTransport(xml,out encoding);
            var doc=new XmlDocument {XmlResolver=null};
            using(var reader=XmlReader.Create(new StringReader(normalized),new XmlReaderSettings {DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=65536}))doc.Load(reader);
            var root=doc.DocumentElement;
            if(root==null || root.Name!="RuleCollection" || root.GetAttribute("Type")!="Exe" || root.GetAttribute("EnforcementMode")!="AuditOnly")return false;
            if(doc.SelectNodes("/RuleCollection/*").Count!=2)return false;
            var allow=doc.SelectSingleNode("/RuleCollection/FilePathRule[@Action='Allow']") as XmlElement;
            var deny=doc.SelectSingleNode("/RuleCollection/FilePathRule[@Action='Deny']") as XmlElement;
            if(allow==null || deny==null || allow.GetAttribute("UserOrGroupSid")!="S-1-1-0" || deny.GetAttribute("UserOrGroupSid")!="S-1-1-0")return false;
            var ap=allow.SelectSingleNode("Conditions/FilePathCondition") as XmlElement;var dp=deny.SelectSingleNode("Conditions/FilePathCondition") as XmlElement;
            return ap!=null && dp!=null && ap.GetAttribute("Path")=="*" && dp.GetAttribute("Path")==target && allow.SelectNodes("Conditions/*").Count==1 && deny.SelectNodes("Conditions/*").Count==1;
        }
        static bool RemoveOwned(CimSession session,string id,string expectedTarget,bool verifyAuditBody) {
            var rows=Query(session,id);
            try {
                foreach(var row in rows) {
                    if(Property(row,"ParentID")!=Prefix+id || Property(row,"InstanceID")!="EXE")throw new InvalidOperationException("Refusing delete: MI ownership mismatch");
                    if(verifyAuditBody && !AuditPolicyMatches(Property(row,"Policy"),expectedTarget))throw new InvalidOperationException("Refusing delete: legacy policy does not match the known audit experiment");
                    using(var options=new CimOperationOptions {Timeout=TimeSpan.FromSeconds(8)})session.DeleteInstance(Namespace,row,options);
                }
            } finally {DisposeRows(rows);}
            return Absent(session,id);
        }
        static bool RegistryPolicyPresent() {using(var key=Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\SrpV2"))return key!=null;}
        static void ReconcileLegacy(CimSession session) {
            Journal("Reconciling the exact legacy lab group");
            string legacyTarget=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"AICT-CspLab-"+LegacyId,"NeverExecuted-AictProbe.exe");
            Report["LegacyInstancesAbsent"]=RemoveOwned(session,LegacyId,legacyTarget,true);
            if(!(bool)Report["LegacyInstancesAbsent"])throw new InvalidOperationException("Legacy group absence not confirmed");
        }
        static void Save(string mode) {
            string name=mode=="--audit"?"audit-result.json":"cleanup-result.json";
            string destination=Path.Combine(Root,name);
            if(File.Exists(destination))destination=Path.Combine(Root,Path.GetFileNameWithoutExtension(name)+"-"+DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ")+".json");
            string temporary=destination+"."+Guid.NewGuid().ToString("N")+".tmp";
            File.WriteAllText(temporary,Json.Serialize(Report),new System.Text.UTF8Encoding(false));File.Move(temporary,destination);
        }
        static int Run(string mode) {
            if(!IsVmware())throw new InvalidOperationException("VMware guest required; no tasks, policy access or files created.");
            if(WindowsIdentity.GetCurrent().User.Value!="S-1-5-18")throw new InvalidOperationException("Fixed lab worker requires LocalSystem via the supplied guest runner.");
            Root=Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string name=Path.GetFileName(Root);if(!name.StartsWith("AICT-CspNative-",StringComparison.Ordinal))throw new InvalidOperationException("Unexpected lab directory");
            RunId=name.Substring("AICT-CspNative-".Length);Guid parsed;if(!Guid.TryParseExact(RunId,"N",out parsed))throw new InvalidOperationException("Invalid run ID");
            string expected=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),name);
            if(!string.Equals(Path.GetFullPath(Root),Path.GetFullPath(expected),StringComparison.OrdinalIgnoreCase) || (File.GetAttributes(Root)&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("Unexpected lab location");
            Report=Obj("SchemaVersion",3,"Kind","APPLOCKER_NATIVE_MI_AUDIT_ONLY_NOT_BLOCKING","CollectedAtUtc",DateTimeOffset.UtcNow.ToString("o"),"Mode",mode,"RunId",RunId,
                "LocalSystem",true,"LegacyRunId",LegacyId,"LegacyInstancesAbsent",false,"LegacyError",null,"BaselineInstanceCount",null,"MutationAttempted",false,
                "AuditCreateReturned",false,"AuditReadbackVerified",false,"OwnInstancesAbsent",false,"EnforcementEnabled",false,"BlockingProven",false,"Phase0PassAwarded",false,
                "Outcome","NOT_RUN","Error",null,"CleanupError",null,"RegistryKeyPresentBefore",RegistryPolicyPresent(),"PolicyTransportEncoding","HTML_ENTITY_ENCODED_XML","ReadbackTransportEncoding",null);
            CimSession session=null;
            try {
                Journal("Starting native Windows MI client");session=CimSession.Create(null);
                if(mode=="--cleanup") {
                    Report["MutationAttempted"]=true;Journal("Cleanup of this exact native lab group only");Report["OwnInstancesAbsent"]=RemoveOwned(session,RunId,Path.Combine(Root,"NeverExecuted-AictProbe.exe"),false);Report["Outcome"]="CLEANUP_ONLY";
                    try {ReconcileLegacy(session);}catch(Exception error){Report["LegacyError"]=Error(error);}
                } else {
                    ReconcileLegacy(session);
                    Journal("Checking application identity service without changing it");
                    using(var search=new ManagementObjectSearcher("SELECT State FROM Win32_Service WHERE Name='AppIDSvc'")) {
                        bool running=false;foreach(ManagementObject service in search.Get())running|=Convert.ToString(service["State"])=="Running";
                        if(!running)throw new InvalidOperationException("AppIDSvc not running; service configuration left unchanged");
                    }
                    Journal("Reading existing EXE policies without exporting their contents");var baseline=Query(session,null);
                    try {Report["BaselineInstanceCount"]=baseline.Count;if(baseline.Count!=0 || RegistryPolicyPresent())throw new InvalidOperationException("Existing policy state detected; refusing merge or overwrite");}finally{DisposeRows(baseline);}
                    string target=Path.Combine(Root,"NeverExecuted-AictProbe.exe");string xml=Policy(target);
                    if(!AuditPolicyMatches(xml,target))throw new InvalidOperationException("Fixed AuditOnly policy validation failed");
                    string transportValue=EncodePolicyTransport(xml);
                    if(!AuditPolicyMatches(transportValue,target))throw new InvalidOperationException("Encoded policy roundtrip validation failed");
                    using(var instance=new CimInstance(ClassName,Namespace)) {
                        instance.CimInstanceProperties.Add(CimProperty.Create("ParentID",Prefix+RunId,MiType.String,CimFlags.Key));
                        instance.CimInstanceProperties.Add(CimProperty.Create("InstanceID","EXE",MiType.String,CimFlags.Key));
                        instance.CimInstanceProperties.Add(CimProperty.Create("Policy",transportValue,MiType.String,CimFlags.Property));
                        Report["MutationAttempted"]=true;Journal("Creating a new AuditOnly group with HTML-encoded XML through the public MI API");
                        using(var options=new CimOperationOptions {Timeout=TimeSpan.FromSeconds(8)})using(var created=session.CreateInstance(Namespace,instance,options)) {Report["AuditCreateReturned"]=true;}
                    }
                    Journal("Reading back this exact group");var rows=Query(session,RunId);
                    try {
                        if(rows.Count!=1 || !AuditPolicyMatches(Property(rows[0],"Policy"),target))throw new InvalidOperationException("AuditOnly readback mismatch");
                        string readbackEncoding;DecodePolicyTransport(Property(rows[0],"Policy"),out readbackEncoding);Report["ReadbackTransportEncoding"]=readbackEncoding;Report["AuditReadbackVerified"]=true;
                    }finally{DisposeRows(rows);}
                    Report["Outcome"]="AUDIT_READBACK_COMPLETE";
                }
            } catch(Exception error) {Report["FailureStage"]=Report.ContainsKey("LastStage")?Report["LastStage"]:null;Report["Error"]=Error(error);Report["Outcome"]="ENTRY_OR_ROUNDTRIP_BLOCKED";}
            finally {
                if(session!=null && mode=="--audit" && (bool)Report["MutationAttempted"]) {
                    try {Journal("Removing only the new lab policy instance");Report["OwnInstancesAbsent"]=RemoveOwned(session,RunId,Path.Combine(Root,"NeverExecuted-AictProbe.exe"),false);}catch(Exception error){Report["CleanupError"]=Error(error);}
                }
                if(session!=null)session.Dispose();
                if(mode=="--audit" && (bool)Report["AuditReadbackVerified"] && (bool)Report["OwnInstancesAbsent"])Report["Outcome"]="AUDIT_ROUNDTRIP_PASS_NOT_BLOCKING";
                Report["RegistryKeyPresentAfter"]=RegistryPolicyPresent();Journal("Final result recorded");Save(mode);
            }
            Console.WriteLine(Json.Serialize(Report));
            return (string)Report["Outcome"]=="AUDIT_ROUNDTRIP_PASS_NOT_BLOCKING" || (mode=="--cleanup" && (bool)Report["OwnInstancesAbsent"])?0:2;
        }
        static int Validate() {
            string target=@"C:\ProgramData\fixture & scoped\"+"\u6d4b\u8bd5"+@"\NeverExecuted-AictProbe.exe";string xml=Policy(target);
            if(!AuditPolicyMatches(xml,target) || AuditPolicyMatches(xml.Replace("AuditOnly","Enabled"),target) || AuditPolicyMatches(xml,target+".other") || AuditPolicyMatches(xml.Replace("Path=\"*\"","Path=\"C:\\wrong\""),target))throw new InvalidOperationException("Audit policy regression");
            string encoded=EncodePolicyTransport(xml),encoding;
            if(DecodePolicyTransport(encoded,out encoding)!=xml || encoding!="HTML_ENTITY_ENCODED_XML" || !AuditPolicyMatches(encoded,target))throw new InvalidOperationException("Single-encoding roundtrip regression");
            var frame=new XmlDocument {XmlResolver=null};frame.LoadXml("<SyncML><Data>"+encoded+"</Data></SyncML>");var data=frame.SelectSingleNode("/SyncML/Data");
            if(data.InnerText!=xml || data.ChildNodes.Count!=1 || !(data.FirstChild is XmlText))throw new InvalidOperationException("Encoded XML did not remain a string value in the XML transport frame");
            var rawFrame=new XmlDocument {XmlResolver=null};rawFrame.LoadXml("<SyncML><Data>"+xml+"</Data></SyncML>");
            if(rawFrame.SelectSingleNode("/SyncML/Data").InnerText==xml)throw new InvalidOperationException("Raw XML unexpectedly behaved like an encoded string payload");
            bool doubleRejected=false;try{AuditPolicyMatches(EncodePolicyTransport(encoded),target);}catch(InvalidDataException){doubleRejected=true;}
            if(!doubleRejected)throw new InvalidOperationException("Double encoding was silently accepted");
            bool dtdRejected=false;try{AuditPolicyMatches("<?xml version=\"1.0\"?><!DOCTYPE RuleCollection [<!ENTITY x \"x\">]>"+xml,target);}catch(XmlException){dtdRejected=true;}
            if(!dtdRejected)throw new InvalidOperationException("DTD was accepted");
            Console.WriteLine("{\"kind\":\"NATIVE_AUDIT_OFFLINE_VALIDATION_NOT_DEPLOYMENT\",\"assertions\":9,\"passed\":true,\"transport_model_not_live_csp_proof\":true}");return 0;
        }
        static int Main(string[] args) {
            Console.OutputEncoding=new System.Text.UTF8Encoding(false);
            Console.SetError(new StreamWriter(Console.OpenStandardError(),new System.Text.UTF8Encoding(false)){AutoFlush=true});
            try {if(args.Length==1 && args[0]=="--validate")return Validate();if(args.Length==1 && (args[0]=="--audit" || args[0]=="--cleanup"))return Run(args[0]);Console.Error.WriteLine("Use the fixed guest laboratory runner.");return 1;}
            catch(Exception error){Console.Error.WriteLine(Json.Serialize(Error(error)));return 1;}
        }
    }
}
