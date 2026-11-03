using System.Collections.Immutable;
using Aict.Core;
using Aict.Storage;
namespace Aict.Beta;
public interface IClock {string BootId{get;}long Uptime{get;}long AwakeMilliseconds{get;}DateTimeOffset UtcNow{get;}bool UserSessionPresent=>true;}
public sealed class Authority:IDisposable {
    readonly StateRepository repository;readonly IClock clock;readonly HashSet<string> essentials;readonly Action<string,object>? log;readonly Action<ActivityFrame>? recordActivity;readonly SemaphoreSlim gate=new(1,1);
    EngineSnapshot state;long revision,lastTick,lastAwake,lastAgent=-100000,lastPersist;readonly long started;AgentSample? agent;
    readonly Dictionary<string,(BrowserSample Sample,long Tick)> browsers=[];
    readonly DateTimeOffset? trialEndsUtc;
    bool TrialExpired=>trialEndsUtc is {} end && (clock.UtcNow>=end || state.Clock.EffectiveUtc>=end);
    string? storageFault,appFault,verifiedApp,webFault,publishedWeb;ImmutableArray<string> actualDenied=[];int actualAllow;bool firewallClosed,gateConfirmed;
    public Authority(StateRepository repository,IClock clock,IEnumerable<string> essentialIds,Action<string,object>? log=null,Action<ActivityFrame>? recordActivity=null,DateTimeOffset? trialEndsUtc=null) {
        this.trialEndsUtc=trialEndsUtc;
        this.repository=repository;this.clock=clock;essentials=essentialIds.ToHashSet(StringComparer.Ordinal);this.log=log;this.recordActivity=recordActivity;var saved=repository.Read();revision=saved.Revision;state=saved.Snapshot;lastTick=clock.Uptime;lastAwake=clock.AwakeMilliseconds;lastPersist=lastTick;started=lastTick;
        if(state.ServiceAnchor is {} anchor && anchor.ClockWasTrusted) {
            if(anchor.BootId==clock.BootId && lastTick>=anchor.UptimeMilliseconds)state=state with {Clock=new RuntimeClock(state.Clock.EffectiveUtc+TimeSpan.FromMilliseconds(lastTick-anchor.UptimeMilliseconds),true).Advance(clock.UtcNow,TimeSpan.Zero)};
            else if(anchor.BootId!=clock.BootId && clock.UtcNow>=state.Clock.EffectiveUtc && clock.UtcNow-state.Clock.EffectiveUtc<=TimeSpan.FromDays(2)) {state=state with {Clock=new(clock.UtcNow,true)};log?.Invoke("BootWallAnchor",new {at=clock.UtcNow});}
        }
        Validate(state.Policy);
        if(state.Pending is {ApplyAtDeadline:false} pending){state=state with {Pending=pending with {ApplyAtDeadline=true}};log?.Invoke("PendingTimingMigrated",new{pending.Id,pending.NotBeforeUtc});}
    }
    public static EngineSnapshot Initial(IClock clock)=>new(){Policy=new(){ChangeDelay=TimeSpan.Zero},Clock=new(clock.UtcNow,true),ServiceAnchor=new(clock.BootId,clock.Uptime,true)};
    bool AgentHealthy=>clock.Uptime-lastAgent<=3500;
    string Token=>PolicyJson.Sha256(PolicyJson.Fingerprint(state.Policy)+"/"+state.Pending?.Id+"/"+PolicyJson.Serialize(state.Applications)+"/"+state.UninstallReady);
    void Validate(PolicyConfiguration p) {
        PolicyValidation.Validate(p);if(p.Groups.Count(g=>g.ApplicationMode==TargetMode.AllowSelected)>8)throw new ArgumentException("应用白名单规则最多8组。");
        if(trialEndsUtc is not null && p.Groups.Any(g=>g.Enabled && g.ApplicationMode==TargetMode.AllowSelected))throw new ArgumentException("主机首轮测试只限制明确选中的应用，暂不启用应用白名单。");
        foreach(var g in p.Groups)foreach(string id in g.ApplicationIds.Concat(g.ApplicationExceptions)) {if(essentials.Contains(id))throw new ArgumentException("不能限制系统必需程序和本软件组件。");if(!state.Applications.Any(a=>a.Id==id))throw new ArgumentException("请先选择并登记应用文件。");}
    }
    void Commit(EngineSnapshot next) {
        next=next with {ServiceAnchor=new(clock.BootId,lastTick,next.Clock.Trusted)};
        try {if(!repository.TryCommit(revision,next))throw new InvalidDataException("写入版本冲突");state=next;revision++;lastPersist=clock.Uptime;}
        catch(Exception e)when(e is IOException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException){storageFault="存储故障，修改已停止，保留现有规则："+e.Message;throw new InvalidOperationException(storageFault,e);}
    }
    string? ActiveDomain() {
        if(!AgentHealthy || agent is null || agent.Locked)return null;
        var focused=browsers.Values.Where(v=>clock.Uptime-v.Tick<=3500 && v.Sample.Browser==agent.ForegroundBrowser && v.Sample.Focused && !v.Sample.Idle).OrderByDescending(v=>v.Tick).FirstOrDefault();
        return focused.Sample?.Domain;
    }
    void Advance() {
        long now=clock.Uptime,awake=clock.AwakeMilliseconds,elapsed=now-lastTick,active=Math.Max(0,awake-lastAwake);lastTick=now;lastAwake=awake;
        if(elapsed<0){state=state with {Clock=state.Clock with {Trusted=false,Anomaly="UPTIME_REGRESSED"}};return;}
        var start=state.Clock.EffectiveUtc;state=state with {Clock=state.Clock.Advance(clock.UtcNow,TimeSpan.FromMilliseconds(elapsed))};
        if(storageFault is not null)return;
        if(TrialExpired && (state.Policy.Groups.Any(g=>g.Enabled) || state.Pending is not null || state.Policy.ChangeDelay!=TimeSpan.Zero)){
            Commit(state with {Policy=state.Policy with {ChangeDelay=TimeSpan.Zero,Groups=state.Policy.Groups.Select(g=>g with {Enabled=false}).ToImmutableArray()},Pending=null});
            log?.Invoke("HostTrialExpired",new {trialEndsUtc,rulesPreserved=true});
        }
        // Sleeping time is excluded by the OS unbiased interrupt clock. Missing awake observations are censored.
        bool suspended=elapsed-active>2500;
        if(!clock.UserSessionPresent && !AgentHealthy)state=state with {Usage=state.Usage with {LastAccountedUntilUtc=state.Clock.EffectiveUtc}};
        else if(elapsed>0 && !suspended) {
            bool known=AgentHealthy && active<4000;
            var focused=browsers.Values.Where(v=>clock.Uptime-v.Tick<=3500 && v.Sample.Browser==agent?.ForegroundBrowser && v.Sample.Focused && !v.Sample.Idle).OrderByDescending(v=>v.Tick).FirstOrDefault();
            var context=new ActivityContext(agent?.ApplicationId,ActiveDomain()){WebsiteUrl=focused.Sample?.Url};
            var slice=new UsageSlice(start,TimeSpan.FromMilliseconds(Math.Min(elapsed,31L*86400000)),context,SessionLocked:agent?.Locked??false,IdleAtStart:TimeSpan.FromSeconds(agent?.IdleSeconds??0),ObservationKnown:known);
            // Do not censor a newly installed, still-empty workspace or its first short agent startup interval.
            if(known || now-started>=5000){state=state with {Usage=RuleEngine.Account(state.Policy,state.Usage,slice)};recordActivity?.Invoke(new(state.Policy.TimeZoneId,slice,agent?.ApplicationName));}
        }
        var result=ChangeQueue.TryApply(state,state.Clock.Reading,AgentHealthy);
        if(result.Status==ApplyStatus.Applied){Commit(result.Snapshot with {UninstallReady=result.UninstallAuthorized});log?.Invoke("ChangeApplied",new {uninstall=result.UninstallAuthorized});}
        else if(now-lastPersist>=2000)Commit(state);
    }
    Status Status() {
        var app=Plans.Apps(state,AgentHealthy);var web=Plans.Web(state,AgentHealthy);
        var usage=state.Policy.Groups.SelectMany(g=>{var specs=QuotaSchedules.Specs(g).ToArray();if(specs.Length==0)return new[]{new UsageRow(g.Id,g.Name,state.Usage.Get(g.Id,RuleEngine.Day(state.Policy,state.Clock.EffectiveUtc)).KnownTicks,null,false)};return specs.Select(q=>{var period=QuotaPeriods.At(state.Policy,q,state.Clock.EffectiveUtc);var u=state.Usage.Get(g.Id,period.Day,q.Id);return new UsageRow(g.Id,g.Name,u.KnownTicks,q.Limit.Ticks,u.HasGap,period.Contains(state.Clock.EffectiveUtc),QuotaSchedules.Label(q),q.Id);}).ToArray();}).ToImmutableArray();
        return new(state.Policy,state.Pending,Token,state.Clock.EffectiveUtc,state.Clock.Trusted,state.Clock.Anomaly,storageFault is null,storageFault,state.Applications,usage,new(app.Fingerprint,verifiedApp,actualDenied,actualAllow,appFault),new(web.Required,browsers.Values.Count(v=>clock.Uptime-v.Tick<=3500 && v.Sample.AppliedPlan==web.Fingerprint),AgentHealthy,publishedWeb,firewallClosed,webFault,gateConfirmed),state.UninstallReady,trialEndsUtc,TrialExpired);
    }
    public async Task<(AppPlan Apps,WebPlan Web,ImmutableDictionary<string,bool> BrowserHealthy,bool Uninstall)> Desired(CancellationToken ct=default) {
        await gate.WaitAsync(ct);try {Advance();var web=Plans.Web(state,AgentHealthy);var healthy=new Dictionary<string,bool>();foreach(string name in new[]{"Edge","Chrome"})healthy[name]=AgentHealthy && browsers.Values.Any(v=>v.Sample.Browser==name && clock.Uptime-v.Tick<=3500 && v.Sample.AppliedPlan==web.Fingerprint && (agent?.ForegroundBrowser!=name || v.Sample.Focused));return(Plans.Apps(state,AgentHealthy),web,healthy.ToImmutableDictionary(),state.UninstallReady);}finally{gate.Release();}
    }
    public async Task Observe(AppPlan plan,string? appError,string? webHash,bool closed,string? webError,CancellationToken ct=default,bool networkConfirmed=true) {await gate.WaitAsync(ct);try {appFault=appError;webFault=webError;publishedWeb=webHash;firewallClosed=closed;gateConfirmed=networkConfirmed;if(appError is null){verifiedApp=plan.Fingerprint;actualDenied=plan.Denied.Select(a=>a.Id).ToImmutableArray();actualAllow=plan.Whitelists.Length;}}finally{gate.Release();}}
    public async Task<Response> Handle(Request request,bool trustedAgent=false,CancellationToken ct=default) {
        await gate.WaitAsync(ct);try {
            request.Validate();Advance();
            if(request.Command is Command.AgentPulse or Command.BrowserPulse) {
                if(!trustedAgent)return new(false,"UNAUTHORIZED","该接口仅供受保护的采集组件使用。");
                if(request.Agent is {} a){agent=a;lastAgent=clock.Uptime;return new(true,"OK","采集已接收");}
                var b=request.Browser!;
                if(state.Policy.Groups.Any(g=>g.Enabled && g.WebsiteMode!=TargetMode.None && g.WebsiteDomains.Concat(g.WebsiteExceptions).Any(p=>!WebsitePattern.IsDomain(p))) && b.PatternVersion!=1)b=b with {AppliedPlan=""};
                browsers[b.Browser+":"+b.ProfileId]=(b,clock.Uptime);foreach(string key in browsers.Where(v=>clock.Uptime-v.Value.Tick>10000).Select(v=>v.Key).ToArray())browsers.Remove(key);
                if(b.BlockedDomain is {} denied && Plans.Web(state,AgentHealthy).Groups.Any(g=>Plans.Denies(g,denied)))log?.Invoke("WebsiteNavigationDenied",new{browser=b.Browser,domain=PolicyValidation.Domain(denied)});
                return new(true,"OK","浏览器计划",BrowserPlan:Plans.Web(state,AgentHealthy));
            }
            if(request.Command==Command.Status)return new(true,"OK","后台状态",Status());
            if(request.Command==Command.ExportJson)return new(true,"OK","规则与用量快照",Status(),PolicyJson.Serialize(state));
            if(request.Command==Command.ExportCsv)return new(true,"OK","用量导出；缺口不是零使用。",Status(),UsageExport.ToCsv(state.Policy,state.Usage));
            if(request.Command is Command.GetActivities or Command.ExportActivityCsv)return new(false,"NOT_CONNECTED","统计存储需由后台服务提供。");
            if(storageFault is not null || state.UninstallReady)return new(false,"UNAVAILABLE",storageFault??"卸载已经获准，正在收尾。",Status());
            if(request.EditToken!=Token)return new(false,"STALE_EDIT","规则已经变化，请重新读取。",Status());
            if(request.Command==Command.UpdateWebsiteAnnotations){var edit=request.Annotations!;Commit(ExceptionCatalog.UpdateAnnotations(state,edit.GroupId,edit.Entries));log?.Invoke("WebsiteAnnotationsUpdated",new{edit.GroupId,entries=edit.Entries.Length});return new(true,"ANNOTATIONS_SAVED","备注和分组已保存；封锁内容与待生效时间保持不变。",Status());}
            if(TrialExpired && request.Command is Command.SubmitPolicy or Command.RegisterApplication)return new(false,"TRIAL_EXPIRED","主机首轮测试已结束，规则已保留并停用。可以导出或正常卸载。",Status());
            if(request.Command==Command.RegisterApplication) {
                if(state.Applications.Any(a=>a.Id==request.Application!.Id))return new(true,"ALREADY_REGISTERED","已登记此文件版本。",Status());
                if(!state.Clock.Trusted)return new(false,"LOCKED","时间状态异常，暂不登记新文件。",Status());
                if(essentials.Contains(request.Application!.Id))return new(false,"ESSENTIAL_APP","本软件和系统必需程序不能加入限制。",Status());
                if(state.Applications.Length>=64)return new(false,"CATALOG_FULL","当前最多登记64个应用文件版本。",Status());
                Commit(state with {Applications=state.Applications.Add(request.Application)});return new(true,"REGISTERED","应用已登记。",Status());
            }
            if(request.Command==Command.CancelPending){if(state.Pending?.Id!=request.PendingId)return new(false,"STALE_EDIT","待生效修改已变化。",Status());Commit(ChangeQueue.Cancel(state));log?.Invoke("PendingCancelled",new{});return new(true,"CANCELLED","原规则保留，冻结修改已撤销。",Status());}
            var target=request.Command==Command.RequestUninstall?state.Policy with {Groups=state.Policy.Groups.Select(g=>g with {Enabled=false}).ToImmutableArray()}:request.Policy!;Validate(target);
            if(!state.Clock.Trusted)return new(false,"LOCKED","时间异常，原规则继续执行。",Status());
            if(request.Command==Command.SubmitPolicy && PolicyJson.Fingerprint(target)==PolicyJson.Fingerprint(state.Policy))return new(true,"UNCHANGED","与当前生效规则相同，没有提交新修改；原待生效申请保留。",Status());
            if(state.Pending is {} existing && existing.TargetSha256==PolicyJson.Fingerprint(target) && (request.Command==Command.RequestUninstall)==(existing.Purpose==ChangePurpose.Uninstall))return new(true,"ALREADY_QUEUED","这份修改已在等待，原生效时间保留。",Status());
            bool immediate=request.Command==Command.SubmitPolicy && PolicyStrength.IsAtLeastAsRestrictive(state.Policy,target,state.Clock.EffectiveUtc);
            if(immediate){bool replaced=state.Pending is not null;Commit(state with {Policy=target,Pending=null,Usage=QuotaPeriods.CarryForward(state.Policy,target,state.Usage,state.Clock.EffectiveUtc)});log?.Invoke("ChangeSubmitted",new{purpose=ChangePurpose.ReplaceConfiguration,immediate=true,replacedPending=replaced});return new(true,"SAVED_EXECUTION_PENDING",replaced?"收紧修改已保存并立即执行；之前的待生效申请已取消。":"收紧修改已保存，正在立即执行。",Status());}
            var next=ChangeQueue.Submit(state,target,state.Clock.Reading,request.Command==Command.RequestUninstall?ChangePurpose.Uninstall:ChangePurpose.ReplaceConfiguration);
            next=next with {Pending=next.Pending! with {ApplyAtDeadline=true}};
            var apply=ChangeQueue.TryApply(next,state.Clock.Reading,AgentHealthy);Commit(apply.Snapshot with {UninstallReady=apply.UninstallAuthorized});log?.Invoke("ChangeSubmitted",new{purpose=next.Pending!.Purpose,immediate=apply.Status==ApplyStatus.Applied,applyAtDeadline=true});
            return new(true,apply.Status==ApplyStatus.Applied?"SAVED_EXECUTION_PENDING":"QUEUED",apply.Status==ApplyStatus.Applied?"修改已保存，正在执行；请查看执行状态。":"修改包含放宽或无法证明仅收紧的内容，已保存待生效规则；等待期间旧规则继续运行，到期自动切换。",Status());
        }catch(Exception e)when(e is ArgumentException or InvalidOperationException or IOException or InvalidDataException){log?.Invoke("RequestRejected",new{request.Command,error=e.Message});return new(false,"REJECTED",e.Message);}finally{gate.Release();}
    }
    public void Dispose()=>gate.Dispose();
}
