using System.Collections.Immutable;
using Microsoft.Win32;
using Aict.Core;
using Aict.Beta;
namespace Aict.Beta.Service;
internal sealed record OwnedRegistry(string Key,string Name,RegistryValueKind Kind,string Value);
internal sealed class Browsers:IDisposable {
    public const string Host="com.aict.beta";
    public static string ExtensionId=>File.ReadAllText(Path.Combine(Env.Root,"Extension","extension-id.txt")).Trim();
    readonly NetworkGate gate=new();readonly List<OwnedRegistry> owned;readonly Dictionary<string,string> paths;
    readonly string journal=Path.Combine(Env.Root,"state","registry-owned.json");
    long pathsChecked=-10000;
    readonly Dictionary<string,long> missingSince=[];readonly HashSet<string> closedOnce=[];
    public IReadOnlyDictionary<string,string> Paths=>paths;
    readonly Dictionary<string,BrowserNetworkState> networkStates=[];public ImmutableArray<BrowserNetworkState> NetworkStates=>networkStates.Values.ToImmutableArray();
    public Browsers() {owned=File.Exists(journal)?PolicyJson.Parse<List<OwnedRegistry>>(File.ReadAllText(journal)):[];paths=FindPaths();string file=Path.Combine(Env.Root,"state","browser-paths.json");if(File.Exists(file)){var old=PolicyJson.Parse<Dictionary<string,string>>(File.ReadAllText(file));var known=FindPaths(false);foreach(var pair in old)if(known.TryGetValue(pair.Key,out var name) && name==pair.Value)paths[pair.Key]=pair.Value;else throw new InvalidDataException("Saved browser path scope changed");}Env.Save(file,paths);}
    public static Dictionary<string,string> FindPaths(bool existingOnly=true) {
        using var profile=Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\"+Env.Config.ProbeSid);string user=Environment.ExpandEnvironmentVariables(Convert.ToString(profile?.GetValue("ProfileImagePath"))??"");
        if(!Path.IsPathFullyQualified(user))throw new InvalidDataException("Managed user profile is unavailable");
        var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var browser in new[]{("Edge",@"Microsoft\Edge\Application\msedge.exe"),("Chrome",@"Google\Chrome\Application\chrome.exe")})foreach(string parent in new[]{Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),Path.Combine(user,@"AppData\Local")}) {string file=Path.Combine(parent,browser.Item2);if(!existingOnly || File.Exists(file))result[Path.GetFullPath(file)]=browser.Item1;}return result;
    }
    void EnsureValue(string keyPath,string name,RegistryValueKind kind,string value) {
        string prefix=Env.Config.ProbeSid+@"\Software\";if(!keyPath.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Registry ownership scope mismatch");
        using var hive=Registry.Users.OpenSubKey(Env.Config.ProbeSid,true)??throw new IOException("等待指定用户登录，以部署浏览器组件。");using var key=hive.CreateSubKey(keyPath[(Env.Config.ProbeSid.Length+1)..],true)??throw new IOException("Managed registry key unavailable");
        if(key.GetValueNames().Contains(name,StringComparer.OrdinalIgnoreCase)) {if(key.GetValueKind(name)!=kind || Convert.ToString(key.GetValue(name))!=value)throw new InvalidDataException("已有浏览器配置冲突，未覆盖："+name);return;}
        var entry=new OwnedRegistry(keyPath,name,kind,value);if(!owned.Contains(entry)){owned.Add(entry);Env.Save(journal,owned);}
        key.SetValue(name,kind==RegistryValueKind.DWord?int.Parse(value):value,kind);key.Flush();
        if(Convert.ToString(key.GetValue(name))!=value)throw new IOException("Browser registry readback failed");
    }
    public void Configure(bool websitesRequired=true) {
        if(!Guid.TryParseExact(Env.Config.RunId,"N",out _) || ExtensionId.Length!=32 || ExtensionId.Any(c=>c is <'a' or >'p'))throw new InvalidDataException("Extension ID invalid");
        string hostPath=Path.Combine(Env.Root,"native-host.json");Env.Save(hostPath,new {name=Host,description="AICT protected local browser bridge",path=Path.Combine(Env.Root,@"Agent\Aict.Beta.Agent.exe"),type="stdio",allowed_origins=new[]{"chrome-extension://"+ExtensionId+"/"}});
        foreach(string browser in new[]{"Edge","Chrome"}) {
            string root=browser=="Edge"?@"Microsoft\Edge":@"Google\Chrome";
            EnsureValue(Env.Config.ProbeSid+@"\Software\"+root+@"\NativeMessagingHosts\"+Host,"",RegistryValueKind.String,hostPath);
            if(!websitesRequired)continue;
            EnsureValue(Env.Config.ProbeSid+@"\Software\Policies\"+root,"BrowserGuestModeEnabled",RegistryValueKind.DWord,"0");
            EnsureValue(Env.Config.ProbeSid+@"\Software\Policies\"+root,browser=="Edge"?"InPrivateModeAvailability":"IncognitoModeAvailability",RegistryValueKind.DWord,"1");
        }
        if(!websitesRequired)RestoreModePolicies();
    }
    void RestoreModePolicies(){foreach(var item in owned.Where(o=>!o.Key.Contains(@"\NativeMessagingHosts\",StringComparison.OrdinalIgnoreCase))){ValidateOwnedModePolicy(item);using var key=Registry.Users.OpenSubKey(item.Key,true);if(key is null || !key.GetValueNames().Contains(item.Name,StringComparer.OrdinalIgnoreCase))continue;if(key.GetValueKind(item.Name)!=item.Kind || Convert.ToString(key.GetValue(item.Name))!=item.Value)throw new InvalidDataException("浏览器配置已被其他工具改变，未删除："+item.Name);key.DeleteValue(item.Name,false);key.Flush();}}
    static void ValidateOwnedModePolicy(OwnedRegistry item){string edge=Env.Config.ProbeSid+@"\Software\Policies\Microsoft\Edge",chrome=Env.Config.ProbeSid+@"\Software\Policies\Google\Chrome";bool key=item.Key.Equals(edge,StringComparison.OrdinalIgnoreCase)||item.Key.Equals(chrome,StringComparison.OrdinalIgnoreCase);bool mode=item.Name=="BrowserGuestModeEnabled"&&item.Value=="0" || item.Key.Equals(edge,StringComparison.OrdinalIgnoreCase)&&item.Name=="InPrivateModeAvailability"&&item.Value=="1" || item.Key.Equals(chrome,StringComparison.OrdinalIgnoreCase)&&item.Name=="IncognitoModeAvailability"&&item.Value=="1";if(!key || !mode || item.Kind!=RegistryValueKind.DWord)throw new InvalidDataException("Owned browser mode policy scope mismatch");}
    public bool Reconcile(WebPlan plan,ImmutableDictionary<string,bool> health,ImmutableDictionary<string,string>? reasons=null) {
        if(Environment.TickCount64-pathsChecked>5000){pathsChecked=Environment.TickCount64;bool changed=false;foreach(var pair in FindPaths())if(!paths.ContainsKey(pair.Key)){paths[pair.Key]=pair.Value;changed=true;}if(changed)Env.Save(Path.Combine(Env.Root,"state","browser-paths.json"),paths);}
        if(plan.Required && paths.Count==0)throw new InvalidOperationException("没有检测到受管理的 Edge 或 Chrome。");
        var desired=paths.ToDictionary(p=>p.Key,p=>File.Exists(p.Key) && plan.Required && !health.GetValueOrDefault(p.Value),StringComparer.OrdinalIgnoreCase);
        var before=gate.ReadStates(desired.Keys);
        try{gate.Set(desired);}catch{foreach(string name in new[]{"Edge","Chrome"}){var old=networkStates.GetValueOrDefault(name);networkStates[name]=new(name,false,old?.Restricted??true,"network_readback_failed",old?.Generation??0,old?.RestrictedSinceUtc,old?.ReleasedUtc);}throw;}
        var now=DateTimeOffset.UtcNow;
        foreach(string name in new[]{"Edge","Chrome"}){
            bool blocked=desired.Any(p=>paths[p.Key]==name&&p.Value),wasBlocked=before.Any(p=>paths[p.Key]==name&&p.Value);var old=networkStates.GetValueOrDefault(name);
            string reason=reasons?.GetValueOrDefault(name)??(!plan.Required?"not_required":blocked?"unconfirmed":"confirmed");long generation=old?.Generation??0;
            bool changed=wasBlocked!=blocked;if(changed||old is null)generation++;
            var started=blocked?(wasBlocked?old?.RestrictedSinceUtc??now:now):old?.RestrictedSinceUtc??(wasBlocked?now:null);
            var released=wasBlocked&&!blocked?now:old?.ReleasedUtc;
            var state=new BrowserNetworkState(name,true,blocked,reason,generation,started,released);networkStates[name]=state;
            if(old is null||old.Restricted!=blocked||old.Reason!=reason||!old.Confirmed)Env.Log("BrowserNetworkGateChanged",new{browser=name,restricted=blocked,confirmed=true,reason,generation});
        }
        return desired.Values.Any(v=>v);
    }
    public async Task CloseLostBrowsers(WebPlan plan,ImmutableDictionary<string,bool> health,Terminator terminator,CancellationToken ct) {
        foreach(string name in new[]{"Edge","Chrome"}){
            if(!plan.Required || health.GetValueOrDefault(name)){missingSince.Remove(name);closedOnce.Remove(name);continue;}
            if(!missingSince.TryGetValue(name,out long since)){missingSince[name]=Environment.TickCount64;continue;}
            if(closedOnce.Contains(name) || Environment.TickCount64-since<5000)continue;
            var targets=paths.Where(p=>p.Value==name && File.Exists(p.Key)).Select(p=>new ApplicationTarget(ApplicationTarget.Prefix+Aict.Desktop.AuthenticodeHash.Compute(p.Key)[2..],name,Path.GetFileName(p.Key),new FileInfo(p.Key).Length)).DistinctBy(t=>t.Id).ToImmutableArray();
            await terminator.EnforceAsync(new(targets,[]),ct);closedOnce.Add(name);Env.Log("BrowserProtectionLost",new{browser=name,networkRestricted=true,existingWindowsClosedOnce=true});
        }
    }
    public void Restore() {
        gate.RemoveObjects(paths.Keys);
        using var hive=Registry.Users.OpenSubKey(Env.Config.ProbeSid,true);
        if(hive is null){Env.Save(Path.Combine(Env.Root,"state","browser-cleanup.json"),new {ownNetworkFiltersRemoved=true,registryCleanupDeferred=true});return;}
        foreach(var item in owned.AsEnumerable().Reverse()) {
            if(!item.Key.StartsWith(Env.Config.ProbeSid+@"\Software\",StringComparison.OrdinalIgnoreCase) || !(item.Key.Contains(@"\Microsoft\Edge",StringComparison.OrdinalIgnoreCase) || item.Key.Contains(@"\Google\Chrome",StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("Invalid owned registry scope");
            // Keep the inert local host so closed browser profiles can read the completed-uninstall tombstone,
            // remove their persistent DNR rules and uninstall their own extension on next launch.
            if(item.Key.Contains(@"\NativeMessagingHosts\",StringComparison.OrdinalIgnoreCase))continue;
            ValidateOwnedModePolicy(item);
            using var key=hive.OpenSubKey(item.Key[(Env.Config.ProbeSid.Length+1)..],true);if(key is null || !key.GetValueNames().Contains(item.Name,StringComparer.OrdinalIgnoreCase))continue;
            if(key.GetValueKind(item.Name)!=item.Kind || Convert.ToString(key.GetValue(item.Name))!=item.Value)throw new InvalidDataException("浏览器配置已被其他工具改变，未删除："+item.Name);key.DeleteValue(item.Name,false);key.Flush();
        }
        Env.Save(Path.Combine(Env.Root,"state","browser-cleanup.json"),new {ownRegistryValuesRemoved=true,ownNetworkFiltersRemoved=true});
    }
    public void Dispose()=>gate.Dispose();
}
