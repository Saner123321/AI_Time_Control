using System.Collections.Immutable;
using Aict.Core;
namespace Aict.Beta;
public enum Command {Status,SubmitPolicy,CancelPending,RegisterApplication,ExportJson,ExportCsv,RequestUninstall,AgentPulse,BrowserPulse,GetActivities,ExportActivityCsv,UpdateWebsiteAnnotations,ManualViewingPulse}
public sealed record WebsiteAnnotationEdit(Guid GroupId,ImmutableArray<WebsiteExceptionDetail> Entries);
public sealed record AgentSample(string? ApplicationId,bool Locked,double IdleSeconds,string? ForegroundBrowser=null,string? ApplicationName=null) {
    public bool IdentityPending {get;init;}
    public AgentDiagnostics? Diagnostics {get;init;}
}
public sealed record AgentDiagnostics(int ProcessId,long Sequence,long UptimeMilliseconds,long LoopIntervalMilliseconds,long PreviousRoundTripMilliseconds,string IdentityStage,long IdentityPendingMilliseconds,long ForegroundMilliseconds=0,long GcPauseMilliseconds=0,long JournalFailures=0);
public sealed record BrowserSample(string Browser,string ProfileId,string AppliedPlan,bool Focused,bool Idle,string? Domain,string? BlockedDomain=null) {
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public string? Url {get;init;}
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] public int PatternVersion {get;init;}
}
public sealed record Request {
    public int Version {get;init;}=1;
    public Command Command {get;init;}
    public string? EditToken {get;init;}
    public PolicyConfiguration? Policy {get;init;}
    public Guid? PendingId {get;init;}
    public ApplicationTarget? Application {get;init;}
    public AgentSample? Agent {get;init;}
    public BrowserSample? Browser {get;init;}
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public ManualViewingSample? ManualViewing {get;init;}
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public WebsiteAnnotationEdit? Annotations {get;init;}
    public void Validate() {
        if(Version!=1 || !Enum.IsDefined(Command))throw new ArgumentException("协议版本或请求类型无效。");
        if((Command==Command.ManualViewingPulse)!=(ManualViewing is not null))throw new ArgumentException("观看采集请求字段无效。");
        if(ManualViewing is {} m){if(!Enum.IsDefined(m.Observation)||m.VisibleUrls.IsDefault||m.VisibleUrls.Any(u=>u is null)||m.VisibleUrls.Length>8||m.CaptureAgeMs<0||m.CaptureAgeMs>86400000||m.UiAgeMs<0||m.AppliedPlan is null||m.Diagnostic is null||m.AppliedPlan.Length>64||m.AppliedPlan.Any(c=>!char.IsAsciiHexDigit(c))||m.RenderedKeys.IsDefault||m.RenderedKeys.Length>8||m.RenderedKeys.Any(k=>k is null||k.Length!=64||k.Any(c=>!char.IsAsciiHexDigit(c)))||m.Diagnostic.Length>100||m.Diagnostic.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c!='_'))throw new ArgumentException("观看采集数据无效。");foreach(string manualUrl in m.VisibleUrls)_=WebsitePattern.WebUrl(manualUrl);if(m.Observation!=ManualObservation.Confirmed&&!m.VisibleUrls.IsEmpty)throw new ArgumentException("未确认的网页不能作为网址样本。");}
        bool write=Command is Command.SubmitPolicy or Command.CancelPending or Command.RegisterApplication or Command.RequestUninstall or Command.UpdateWebsiteAnnotations;
        if((Command==Command.UpdateWebsiteAnnotations)!=(Annotations is not null))throw new ArgumentException("备注请求字段无效。");
        if(Annotations is {} annotations){if(annotations.GroupId==Guid.Empty)throw new ArgumentException("请选择规则。");ExceptionCatalog.ValidateEntries(annotations.Entries);}
        if(write!=(EditToken is not null) || (Command==Command.SubmitPolicy)!=(Policy is not null) || (Command==Command.CancelPending)!=(PendingId is not null) || (Command==Command.RegisterApplication)!=(Application is not null) || (Command==Command.AgentPulse)!=(Agent is not null) || (Command==Command.BrowserPulse)!=(Browser is not null))throw new ArgumentException("请求字段不符合协议。");
        if(EditToken is {} e && (e.Length!=64 || e.Any(c=>!char.IsAsciiHexDigit(c))))throw new ArgumentException("无效编辑版本。");
        if(PendingId==Guid.Empty)throw new ArgumentException("无效修改编号。");
        if(Policy is {} p)PolicyValidation.Validate(p);Application?.Validate();
        if(Browser?.Url is {} url) {var uri=new Uri(WebsitePattern.WebUrl(url));if(Browser.Domain is null || PolicyValidation.Domain(uri.IdnHost)!=PolicyValidation.Domain(Browser.Domain))throw new ArgumentException("浏览器网址与域名不一致。");}
        if(Agent is {} a) {if(!double.IsFinite(a.IdleSeconds) || a.IdleSeconds<0 || a.IdleSeconds>1e9 || a.ForegroundBrowser is not (null or "Edge" or "Chrome" or "ChatGPTIab") || a.ApplicationName is {} name && (name.Length>200 || name.Any(char.IsControl) || name.IndexOfAny(['\\','/'])>=0))throw new ArgumentException("无效空闲状态。");if(a.ApplicationId is {} id)_=Aict.Enforcement.ApplicationPlanner.Digest(id);if(a.Diagnostics is {} d && (d.ProcessId<=0 || d.Sequence<0 || d.UptimeMilliseconds<0 || d.LoopIntervalMilliseconds<0 || d.PreviousRoundTripMilliseconds<0 || d.IdentityPendingMilliseconds<0 || d.ForegroundMilliseconds<0 || d.GcPauseMilliseconds<0 || d.JournalFailures<0 || d.IdentityStage is not ("idle" or "queued" or "metadata" or "hash" or "unavailable")))throw new ArgumentException("无效采集诊断字段。");}
        if(Browser is {} b) {if(b.Browser is not ("Edge" or "Chrome") || !Guid.TryParseExact(b.ProfileId,"N",out _) || b.AppliedPlan.Length>64 || b.AppliedPlan.Any(c=>!char.IsAsciiHexDigit(c)))throw new ArgumentException("无效浏览器状态。");if(b.Domain is {} d)_=PolicyValidation.Domain(d);if(b.BlockedDomain is {} denied)_=PolicyValidation.Domain(denied);}
    }
}
public sealed record Response(bool Ok,string Code,string Message,Status? Status=null,string? ExportText=null,WebPlan? BrowserPlan=null,ImmutableArray<ActivityCounter>? Activities=null,BrowserNetworkState? BrowserNetwork=null,ManualViewingPlan? ManualViewingPlan=null);
public sealed record BrowserNetworkState(string Browser,bool Confirmed,bool Restricted,string Reason,long Generation,DateTimeOffset? RestrictedSinceUtc=null,DateTimeOffset? ReleasedUtc=null);
public sealed record UsageRow(Guid GroupId,string Name,long UsedTicks,long? LimitTicks,bool HasGap,bool QuotaWindowActive=true,string QuotaWindowLabel="每日 00:00—24:00",Guid? QuotaSlotId=null,long KnownTicks=0,long GapChargedTicks=0,bool GapEstimated=false,bool GapChargeComplete=true);
public sealed record Execution(string Desired,string? Verified,ImmutableArray<string> Denied,int AllowGroups,string? Fault) {
    public bool Confirmed=>Fault is null && Desired==Verified;
}
public sealed record WebExecution(bool Required,int ConnectedProfiles,bool ForegroundAgentHealthy,string? PublishedPlan,bool FirewallClosed,string? Fault,bool GateConfirmed=true,ImmutableArray<BrowserNetworkState>? Browsers=null,ManualViewingExecution? ManualViewing=null);
public sealed record RuleActivation(Guid GroupId,string RuleName,ImmutableArray<string> Details);
public sealed record Status(PolicyConfiguration Policy,PendingChange? Pending,string EditToken,DateTimeOffset EffectiveUtc,bool ClockConsistent,string? ClockAnomaly,bool StorageHealthy,string? Fault,ImmutableArray<ApplicationTarget> Applications,ImmutableArray<UsageRow> Usage,Execution Execution,WebExecution Websites,bool UninstallReady,DateTimeOffset? TrialEndsUtc=null,bool TrialExpired=false,ImmutableArray<RuleActivation>? ActiveBlocks=null) {
    public bool TelemetryConnected=>Websites.ForegroundAgentHealthy;
    public string Mode=>BuildMode.HostTrial?(TrialEndsUtc is null?"HOST_CONTINUOUS":"HOST_TRIAL"):"VM_INTEGRATED_BETA";
    public string Revision=>"startup-v15";
    public bool SystemEnforcementActive=>Execution.Confirmed && (!Execution.Denied.IsEmpty || Execution.AllowGroups>0);
}
public sealed record WebGroup(Guid Id,TargetMode Mode,ImmutableArray<string> Domains,ImmutableArray<string> Exceptions,ImmutableArray<BlockReason> Reasons,string Name="",ImmutableArray<string>? Details=null);
public sealed record WebPlan(ImmutableArray<WebGroup> Groups,bool Required) {
    public string Fingerprint=>PolicyJson.Sha256(PolicyJson.Serialize(Groups));
}
public sealed record AllowGroup(string Id,ImmutableArray<ApplicationTarget> Applications);
public sealed record AppPlan(ImmutableArray<ApplicationTarget> Denied,ImmutableArray<AllowGroup> Whitelists) {
    [System.Text.Json.Serialization.JsonIgnore] public string Fingerprint=>PolicyJson.Sha256(PolicyJson.Serialize(this));
}
public static class Plans {
    public static bool HasWebsiteScope(RuleGroup g)=>g.Enabled && (g.WebsiteMode==TargetMode.AllowSelected || g.WebsiteMode==TargetMode.BlockSelected && !g.WebsiteDomains.IsEmpty);
    public static WebPlan Web(EngineSnapshot s,bool telemetry) => new(s.Policy.Groups.Where(g=>HasWebsiteScope(g) && !RuleEngine.ActivationReasons(s.Policy,g,s.Usage,s.Clock.Reading,telemetry).IsEmpty).Select(g=>new WebGroup(g.Id,g.WebsiteMode,g.WebsiteDomains.Select(WebsitePattern.Normalize).ToImmutableArray(),g.WebsiteExceptions.Select(WebsitePattern.Normalize).ToImmutableArray(),RuleEngine.ActivationReasons(s.Policy,g,s.Usage,s.Clock.Reading,telemetry),g.Name,BlockExplanations.Describe(s.Policy,g,s.Usage,s.Clock.Reading,telemetry))).ToImmutableArray(),s.Policy.Groups.Any(HasWebsiteScope));
    public static AppPlan Apps(EngineSnapshot s,bool telemetry) {
        var active=s.Policy.Groups.Where(g=>g.Enabled && !RuleEngine.ActivationReasons(s.Policy,g,s.Usage,s.Clock.Reading,telemetry).IsEmpty).ToArray();
        var deny=active.Where(g=>g.ApplicationMode==TargetMode.BlockSelected).SelectMany(g=>g.ApplicationIds.Where(id=>!g.ApplicationExceptions.Contains(id))).Distinct().Order(StringComparer.Ordinal).Select(id=>s.Applications.Single(a=>a.Id==id)).ToImmutableArray();
        var allow=active.Where(g=>g.ApplicationMode==TargetMode.AllowSelected).Select(g=>new AllowGroup(g.Id.ToString("N"),g.ApplicationIds.Concat(g.ApplicationExceptions).Distinct().Order(StringComparer.Ordinal).Select(id=>s.Applications.Single(a=>a.Id==id)).ToImmutableArray())).OrderBy(g=>g.Id,StringComparer.Ordinal).ToImmutableArray();return new(deny,allow);
    }
    public static bool Denies(WebGroup group,string domain) {
        return !WebsitePattern.In(group.Exceptions,domain) && (group.Mode==TargetMode.AllowSelected?!WebsitePattern.In(group.Domains,domain):WebsitePattern.In(group.Domains,domain));
    }
}
