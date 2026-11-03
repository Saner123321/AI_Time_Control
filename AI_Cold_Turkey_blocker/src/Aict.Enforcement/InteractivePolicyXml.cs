// C# 5-compatible, also compiled into the inbox Framework MI bridge.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;

namespace Aict.Enforcement {
    public sealed class PolicyTarget {
        public string Id="",Name="",FileName="";public long Size;
        public string SipHash {get{return "0x"+Id.Substring("authenticode:sha256:".Length);}}
        public void Validate(){
            if(Id==null || !Regex.IsMatch(Id,@"\Aauthenticode:sha256:[0-9A-F]{64}\z") || string.IsNullOrWhiteSpace(Name) || Name.Length>200 || Name.Any(char.IsControl) || string.IsNullOrWhiteSpace(FileName) || FileName.Length>200 || FileName.Any(c=>char.IsControl(c) || "\\/:*?\"<>|".Contains(c)) || !FileName.EndsWith(".exe",StringComparison.OrdinalIgnoreCase) || Size<=0 || Size>4294967296L)throw new ArgumentException("Invalid identity metadata");
        }
    }
    public static class InteractivePolicyXml {
        public static string Exe(string runId,string sid,IEnumerable<PolicyTarget> targets){
            if(!Regex.IsMatch(sid,@"\AS-1-5-21-(\d+-){2}\d+-\d+\z"))throw new ArgumentException("Local user SID required");
            var apps=targets.OrderBy(t=>t.Id,StringComparer.Ordinal).ToArray();if(apps.Length<1 || apps.Length>64 || apps.Select(t=>t.Id).Distinct().Count()!=apps.Length)throw new ArgumentException("Invalid target set");foreach(var app in apps)app.Validate();
            var doc=new XmlDocument {XmlResolver=null};var root=doc.CreateElement("RuleCollection");doc.AppendChild(root);root.SetAttribute("Type","Exe");root.SetAttribute("EnforcementMode","Enabled");
            var allow=doc.CreateElement("FilePathRule");root.AppendChild(allow);Attributes(allow,AppLockerXml.RuleId(runId,"interactive-Allow"),runId,"Allow","S-1-1-0");
            var conditions=doc.CreateElement("Conditions");allow.AppendChild(conditions);var path=doc.CreateElement("FilePathCondition");conditions.AppendChild(path);path.SetAttribute("Path","*");
            foreach(var app in apps){var deny=doc.CreateElement("FileHashRule");root.AppendChild(deny);Attributes(deny,AppLockerXml.RuleId(runId,"interactive-Deny/"+app.Id),runId,"Deny",sid);
                conditions=doc.CreateElement("Conditions");deny.AppendChild(conditions);var condition=doc.CreateElement("FileHashCondition");conditions.AppendChild(condition);var hash=doc.CreateElement("FileHash");condition.AppendChild(hash);
                hash.SetAttribute("Type","SHA256");hash.SetAttribute("Data",app.SipHash);hash.SetAttribute("SourceFileName",app.FileName);hash.SetAttribute("SourceFileLength",app.Size.ToString(CultureInfo.InvariantCulture));
            }
            return doc.OuterXml;
        }
        static void Attributes(XmlElement rule,string id,string run,string action,string sid){rule.SetAttribute("Id",id);rule.SetAttribute("Name","AICT Interactive "+run+" "+action);rule.SetAttribute("Description","Owned application control policy");rule.SetAttribute("UserOrGroupSid",sid);rule.SetAttribute("Action",action);}
        public static PolicyTarget[] ReadOwned(string xml,string runId,out string sid){
            var doc=AppLockerXml.Parse(xml);var nodes=doc.SelectNodes("/RuleCollection/FileHashRule");if(nodes==null || nodes.Count<1 || nodes.Count>64)throw new InvalidOperationException("Invalid owned target set");
            var first=nodes[0] as XmlElement;if(first==null)throw new InvalidOperationException("Missing hash rule");sid=first.GetAttribute("UserOrGroupSid");var apps=new List<PolicyTarget>();
            foreach(XmlElement node in nodes){var hash=node.SelectSingleNode("Conditions/FileHashCondition/FileHash") as XmlElement;long size;
                if(hash==null || !long.TryParse(hash.GetAttribute("SourceFileLength"),NumberStyles.None,CultureInfo.InvariantCulture,out size))throw new InvalidOperationException("Invalid hash rule");string data=hash.GetAttribute("Data");
                if(!Regex.IsMatch(data,@"\A0x[0-9A-F]{64}\z"))throw new InvalidOperationException("Invalid hash data");
                apps.Add(new PolicyTarget {Id="authenticode:sha256:"+data.Substring(2),Name="Owned application",FileName=hash.GetAttribute("SourceFileName"),Size=size});
            }
            if(!AppLockerXml.Equivalent(xml,Exe(runId,sid,apps)))throw new InvalidOperationException("Owned policy shape or identity changed; left untouched");return apps.ToArray();
        }
    }
}
