// Deliberately C# 5-compatible: shared with the inbox .NET Framework MI bridge.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Aict.Enforcement {
    public static class AppLockerXml {
        public static string RuleId(string runId,string role) {
            Guid run;if(!Guid.TryParseExact(runId,"N",out run))throw new ArgumentException("Invalid ownership ID");
            using(var sha=SHA256.Create())return new Guid(sha.ComputeHash(Encoding.UTF8.GetBytes(runId+"/"+role)).Take(16).ToArray()).ToString();
        }
        public static string Exe(string runId,string sid,string sipHash,long size) {
            if(!Regex.IsMatch(sid,@"\AS-1-5-21-(\d+-){2}\d+-\d+\z") || !Regex.IsMatch(sipHash,@"\A0x[0-9A-F]{64}\z") || size<=0)throw new ArgumentException("Expected fixed local-user SID and PE SIP SHA256");
            var doc=new XmlDocument {XmlResolver=null};var root=doc.CreateElement("RuleCollection");doc.AppendChild(root);root.SetAttribute("Type","Exe");root.SetAttribute("EnforcementMode","Enabled");
            foreach(string action in new[]{"Allow","Deny"}) {
                var rule=doc.CreateElement(action=="Allow"?"FilePathRule":"FileHashRule");root.AppendChild(rule);
                rule.SetAttribute("Id",RuleId(runId,"exe-"+action));rule.SetAttribute("Name","AICT ServicePolicy "+runId+" "+action);
                rule.SetAttribute("Description","Owned fixture-only service integration policy");rule.SetAttribute("UserOrGroupSid",action=="Allow"?"S-1-1-0":sid);rule.SetAttribute("Action",action);
                var conditions=doc.CreateElement("Conditions");rule.AppendChild(conditions);
                if(action=="Allow") {var path=doc.CreateElement("FilePathCondition");conditions.AppendChild(path);path.SetAttribute("Path","*");}
                else {var condition=doc.CreateElement("FileHashCondition");conditions.AppendChild(condition);var hash=doc.CreateElement("FileHash");condition.AppendChild(hash);
                    hash.SetAttribute("Type","SHA256");hash.SetAttribute("Data",sipHash);hash.SetAttribute("SourceFileName","AictPolicyTarget.exe");hash.SetAttribute("SourceFileLength",size.ToString(System.Globalization.CultureInfo.InvariantCulture));}
            }
            return doc.OuterXml;
        }
        public static string Appx(string runId) {
            var doc=new XmlDocument {XmlResolver=null};var root=doc.CreateElement("RuleCollection");doc.AppendChild(root);root.SetAttribute("Type","Appx");root.SetAttribute("EnforcementMode","Enabled");
            var rule=doc.CreateElement("FilePublisherRule");root.AppendChild(rule);rule.SetAttribute("Id",RuleId(runId,"appx-Allow"));rule.SetAttribute("Name","AICT ServicePolicy "+runId+" companion");
            rule.SetAttribute("Description","Allow signed packaged apps while owned EXE policy exists");rule.SetAttribute("UserOrGroupSid","S-1-1-0");rule.SetAttribute("Action","Allow");
            var conditions=doc.CreateElement("Conditions");rule.AppendChild(conditions);var publisher=doc.CreateElement("FilePublisherCondition");conditions.AppendChild(publisher);
            foreach(string name in new[]{"PublisherName","ProductName","BinaryName"})publisher.SetAttribute(name,"*");
            var version=doc.CreateElement("BinaryVersionRange");publisher.AppendChild(version);version.SetAttribute("LowSection","0.0.0.0");version.SetAttribute("HighSection","*");return doc.OuterXml;
        }
        public static XmlDocument Parse(string xml) {
            if(xml==null || xml.Length>65536)throw new InvalidDataException("Missing/oversized policy XML");
            if(!xml.TrimStart().StartsWith("<",StringComparison.Ordinal))xml=WebUtility.HtmlDecode(xml);
            var doc=new XmlDocument {XmlResolver=null};using(var reader=XmlReader.Create(new StringReader(xml),new XmlReaderSettings {XmlResolver=null,DtdProcessing=DtdProcessing.Prohibit,MaxCharactersInDocument=65536}))doc.Load(reader);return doc;
        }
        public static bool Equivalent(string actual,string expected) {var a=Parse(actual).DocumentElement;var b=Parse(expected).DocumentElement;return a!=null && b!=null && Same(a,b);}
        static bool Same(XmlElement a,XmlElement b) {
            if(a==null || b==null || a.Name!=b.Name || a.Attributes.Count!=b.Attributes.Count)return false;
            foreach(XmlAttribute attribute in b.Attributes)if(a.GetAttribute(attribute.Name)!=attribute.Value)return false;
            var aa=new List<XmlElement>();var bb=new List<XmlElement>();
            foreach(XmlNode node in a.ChildNodes) {if(node is XmlElement)aa.Add((XmlElement)node);else if(!string.IsNullOrWhiteSpace(node.Value))return false;}
            foreach(XmlNode node in b.ChildNodes) {if(node is XmlElement)bb.Add((XmlElement)node);else if(!string.IsNullOrWhiteSpace(node.Value))return false;}
            return aa.Count==bb.Count && aa.Select((n,i)=>Same(n,bb[i])).All(v=>v);
        }
    }
}
