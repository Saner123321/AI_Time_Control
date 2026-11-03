using System.Diagnostics.Eventing.Reader;
using System.Xml;
namespace Aict.Beta.Service;
internal sealed class BlockEvents:IDisposable {
    readonly EventLogWatcher? watcher;
    public BlockEvents(){try{watcher=new(new EventLogQuery("Microsoft-Windows-AppLocker/EXE and DLL",PathType.LogName,"*[System[EventID=8004]]"));watcher.EventRecordWritten+=OnEvent;watcher.Enabled=true;}catch(Exception e){Env.Log("BlockEventObserverUnavailable",new{error=e.Message});}}
    static void OnEvent(object? sender,EventRecordWrittenEventArgs e){using var record=e.EventRecord;if(record is null)return;try{string xml=record.ToXml();if(xml.Length>131072)return;var doc=new XmlDocument{XmlResolver=null};using(var reader=XmlReader.Create(new StringReader(xml),new XmlReaderSettings{XmlResolver=null,DtdProcessing=DtdProcessing.Prohibit}))doc.Load(reader);
            var values=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);foreach(XmlElement node in doc.SelectNodes("//*[local-name()='EventData']/*[local-name()='Data'] | //*[local-name()='UserData']//*[not(*)]")!){string name=node.HasAttribute("Name")?node.GetAttribute("Name"):node.LocalName;values[name]=node.InnerText;}
            if(!values.Values.Any(v=>v.Contains(Env.Config.ProbeSid,StringComparison.Ordinal)) && record.UserId?.Value!=Env.Config.ProbeSid)return;
            string nameValue=values.GetValueOrDefault("RuleName")??"";if(!nameValue.Contains("AICT",StringComparison.OrdinalIgnoreCase))return;
            string? path=values.GetValueOrDefault("FilePath");Env.Log("ApplicationLaunchDenied",new{recordId=record.RecordId,application=path is null?null:Path.GetFileName(path),ruleName=nameValue});
        }catch(Exception error){try{Env.Log("BlockEventReadFault",new{error=error.Message});}catch{}}}
    public void Dispose()=>watcher?.Dispose();
}
