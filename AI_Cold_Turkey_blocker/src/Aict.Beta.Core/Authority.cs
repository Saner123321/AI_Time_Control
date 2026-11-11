using System.Collections.Immutable;
using Stopwatch=System.Diagnostics.Stopwatch;
using Aict.Core;
using Aict.Storage;
namespace Aict.Beta;
public interface IClock {string BootId{get;}long Uptime{get;}long AwakeMilliseconds{get;}DateTimeOffset UtcNow{get;}bool UserSessionPresent=>true;}
public sealed class Authority:IDisposable {
    readonly StateRepository repository;readonly IClock clock;readonly HashSet<string> essentials;readonly Action<string,object>? log;readonly Action<ActivityFrame>? recordActivity;readonly SemaphoreSlim gate=new(1,1);
    EngineSnapshot state;long revision,lastTick,lastAwake,lastAgent=-100000,lastPersist;readonly long started;AgentSample? agent;bool reportedAgentHealthy;long? lossSince;DateTimeOffset? gapSince;long gapTicks,gapReportAt,lastSlowCommit;
    readonly Dictionary<string,(BrowserSample Sample,long Tick,long AwakeTick)> browsers=[];readonly BrowserReadinessTracker networkReadiness=new();ImmutableArray<BrowserNetworkState> networkStates=[];
    ManualViewingSample? manual;long manualPulseAt=-100000,manualProofAt=-100000;ImmutableArray<string> manualUrls=[];bool manualProtected;string manualDiagnostic="";
    readonly DateTimeOffset? trialEndsUtc;readonly bool restrictApplicationAllowlist;readonly ActivityCounter[]? legacyHistory;
    bool TrialExpired=>trialEndsUtc is {} end && (clock.UtcNow>=end || state.Clock.EffectiveUtc>=end);
    string? storageFault,appFault,verifiedApp,webFault,publishedWeb;ImmutableArray<string> actualDenied=[];int actualAllow;bool firewallClosed,gateConfirmed;
    public Authority(StateRepository repository,IClock clock,IEnumerable<string> essentialIds,Action<string,object>? log=null,Action<ActivityFrame>? recordActivity=null,DateTimeOffset? trialEndsUtc=null,bool restrictApplicationAllowlist=false,IEnumerable<ActivityCounter>? legacyActivity=null) {
        this.trialEndsUtc=trialEndsUtc;this.restrictApplicationAllowlist=restrictApplicationAllowlist;legacyHistory=legacyActivity?.ToArray();
        this.repository=repository;this.clock=clock;essentials=essentialIds.ToHashSet(StringComparer.Ordinal);this.log=log;this.recordActivity=recordActivity;var saved=repository.Read();revision=saved.Revision;state=saved.Snapshot;lastTick=clock.Uptime;lastAwake=clock.AwakeMilliseconds;lastPersist=lastTick;started=lastTick;
        bool restartCharged=false;var savedTime=state.Clock.EffectiveUtc;
        if(state.ServiceAnchor is {} anchor && anchor.ClockWasTrusted) {
            if(anchor.BootId==clock.BootId && lastTick>=anchor.UptimeMilliseconds)state=state with {Clock=new RuntimeClock(state.Clock.EffectiveUtc+TimeSpan.FromMilliseconds(lastTick-anchor.UptimeMilliseconds),true).Advance(clock.UtcNow,TimeSpan.Zero)};
            else if(anchor.BootId!=clock.BootId && clock.UtcNow>=state.Clock.EffectiveUtc && clock.UtcNow-state.Clock.EffectiveUtc<=TimeSpan.FromDays(2)) {state=state with {Clock=new(clock.UtcNow,true)};log?.Invoke("BootWallAnchor",new {at=clock.UtcNow});}
        }
        if(state.ServiceAnchor is {} prior && prior.BootId==clock.BootId && prior.ClockWasTrusted && state.Clock.Trusted && lastTick>prior.UptimeMilliseconds && clock.UserSessionPresent){
            long elapsed=lastTick-prior.UptimeMilliseconds;long? awake=prior.AwakeMilliseconds is {} oldAwake?lastAwake-oldAwake:null;
            if(awake is >=0 && awake<=elapsed && elapsed-awake<=2500 && elapsed<=31L*86400000){state=state with{Usage=RuleEngine.Account(state.Policy,state.Usage,new(savedTime,TimeSpan.FromMilliseconds(elapsed),new(),ObservationKnown:false))};restartCharged=true;Trace("ServiceRestartGapCharged",new{fromUtc=savedTime,durationMs=elapsed,awakeMs=awake});}
            else Trace("ServiceRestartGapNotMeasured",new{uptimeGapMs=elapsed,awakeGapMs=awake,reason=awake is null?"legacy_anchor_without_awake_clock":"includes_sleep_or_untrusted_awake_interval"});
        }
        if(legacyHistory is not null){var migrated=GapAccounting.MigrateLegacy(state.Policy,state.Usage,legacyHistory,Trace);if(migrated!=state.Usage||restartCharged){Commit(state with{Usage=migrated});restartCharged=false;}}
        if(restartCharged)Commit(state);

        Validate(state.Policy);
        if(state.Pending is {ApplyAtDeadline:false} pending){state=state with {Pending=pending with {ApplyAtDeadline=true}};log?.Invoke("PendingTimingMigrated",new{pending.Id,pending.NotBeforeUtc});}
    }
    public static EngineSnapshot Initial(IClock clock)=>new(){Policy=new(){ChangeDelay=TimeSpan.Zero},Clock=new(clock.UtcNow,true),ServiceAnchor=new(clock.BootId,clock.Uptime,true){AwakeMilliseconds=clock.AwakeMilliseconds}};
    bool AgentHealthy=>clock.Uptime-lastAgent<=3500;
    string Token=>PolicyJson.Sha256(PolicyJson.Fingerprint(state.Policy)+"/"+state.Pending?.Id+"/"+PolicyJson.Serialize(state.Applications)+"/"+state.UninstallReady);
    void Validate(PolicyConfiguration p) {
        PolicyValidation.Validate(p);if(p.Groups.Count(g=>g.ApplicationMode==TargetMode.AllowSelected)>8)throw new ArgumentException("应用白名单规则最多8组。");
        if((restrictApplicationAllowlist || trialEndsUtc is not null) && p.Groups.Any(g=>g.Enabled && g.ApplicationMode==TargetMode.AllowSelected))throw new ArgumentException("主机版本暂只限制明确选中的应用，不启用应用白名单。");
        foreach(var g in p.Groups)foreach(string id in g.ApplicationIds.Concat(g.ApplicationExceptions)) {if(essentials.Contains(id))throw new ArgumentException("不能限制系统必需程序和本软件组件。");if(!state.Applications.Any(a=>a.Id==id))throw new ArgumentException("请先选择并登记应用文件。");}
    }
    void Commit(EngineSnapshot next) {
        if(legacyHistory is not null && next.Usage.Accounts.Values.Any(a=>a.UnresolvedGap))next=next with{Usage=GapAccounting.MigrateLegacy(next.Policy,next.Usage,legacyHistory,Trace)};
        next=next with {ServiceAnchor=new(clock.BootId,lastTick,next.Clock.Trusted){AwakeMilliseconds=lastAwake}};
        var commitWatch=Stopwatch.StartNew();
        try {if(!repository.TryCommit(revision,next))throw new InvalidDataException("写入版本冲突");state=next;revision++;lastPersist=clock.Uptime;}
        catch(Exception e)when(e is IOException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException){storageFault="存储故障，修改已停止，保留现有规则："+e.Message;throw new InvalidOperationException(storageFault,e);}
        finally{if(commitWatch.ElapsedMilliseconds>250 && clock.Uptime-lastSlowCommit>10000){lastSlowCommit=clock.Uptime;Trace("StorageCommitSlow",new{durationMs=commitWatch.ElapsedMilliseconds});}}
    }
    void Trace(string kind,object detail){try{log?.Invoke(kind,detail);}catch{}}
    void Health(long elapsed,long awake){
        bool healthy=AgentHealthy;if(healthy==reportedAgentHealthy)return;reportedAgentHealthy=healthy;
        if(!healthy){lossSince=clock.Uptime;Trace("CollectorHeartbeatLost",new{heartbeatAgeMs=clock.Uptime-lastAgent,serviceSliceMs=elapsed,awakeSliceMs=awake,lastSample=agent?.Diagnostics,lastKnownLocked=agent?.Locked,lastKnownIdleSeconds=agent?.IdleSeconds,managedSessionPresent=clock.UserSessionPresent});}
        else{Trace(lossSince is null?"CollectorReady":"CollectorHeartbeatRecovered",new{outageObservedMs=lossSince is {} start?clock.Uptime-start:0,lastSample=agent?.Diagnostics});lossSince=null;}
    }
    void Gap(bool unknown,DateTimeOffset from,TimeSpan duration,string reason){
        if(unknown){if(gapSince is null){gapSince=from;gapTicks=0;gapReportAt=clock.Uptime;Trace("QuotaGapStarted",new{fromUtc=from,reason,heartbeatAgeMs=clock.Uptime-lastAgent,lastSample=agent?.Diagnostics});}gapTicks=checked(gapTicks+duration.Ticks);if(clock.Uptime-gapReportAt>=60000){gapReportAt=clock.Uptime;Trace("QuotaGapProgress",new{fromUtc=gapSince,unobservedSeconds=gapTicks/(double)TimeSpan.TicksPerSecond,reason});}}
        else if(gapSince is not null){Trace("QuotaGapEnded",new{fromUtc=gapSince,untilUtc=from,unobservedSeconds=gapTicks/(double)TimeSpan.TicksPerSecond,reason});gapSince=null;gapTicks=0;}
    }
    string? ActiveDomain() {
        if(!AgentHealthy || agent is null || agent.Locked)return null;
        var focused=browsers.Values.Where(v=>clock.Uptime-v.Tick<=3500 && v.Sample.Browser==agent.ForegroundBrowser && v.Sample.Focused && !v.Sample.Idle).OrderByDescending(v=>v.Tick).FirstOrDefault();
        return focused.Sample?.Domain;
    }
    ImmutableArray<string> ManualUrls()=>clock.AwakeMilliseconds>=manualProofAt&&clock.AwakeMilliseconds-manualProofAt<=2500?manualUrls:[];
    ManualViewingExecution ManualExecution(){
        bool connected=clock.Uptime-manualPulseAt<=4000&&manual is {UiAgeMs:<3000};
        var urls=ManualUrls();var plan=ManualViewingRules.Decide(Plans.Web(state,AgentHealthy),state.Policy,urls);
        var wanted=plan.Pages.Where(p=>p.Denied).Select(p=>p.Key).Order().ToArray();
        bool applied=connected&&manual!.Observation!=ManualObservation.Unavailable&&manual.AppliedPlan==plan.Fingerprint&&manual.RenderedKeys.Order().SequenceEqual(wanted);
        return new(connected,manual?.Observation.ToString()??"Waiting",urls.Length,wanted.Length,manual?.RenderedKeys.Length??0,applied,manualProtected,!connected?"观看保护尚未连接":manual!.Diagnostic=="mask_configuration_failed"?"遮挡窗口未确认，请导出诊断":manual!.Observation==ManualObservation.Unavailable?"内置网页暂未识别："+manual.Diagnostic:null);
    }
    void Advance() {
        long now=clock.Uptime,awake=clock.AwakeMilliseconds,elapsed=now-lastTick,active=Math.Max(0,awake-lastAwake);lastTick=now;lastAwake=awake;
        if(elapsed<0){state=state with {Clock=state.Clock with {Trusted=false,Anomaly="UPTIME_REGRESSED"}};return;}
        var start=state.Clock.EffectiveUtc;state=state with {Clock=state.Clock.Advance(clock.UtcNow,TimeSpan.FromMilliseconds(elapsed))};
        Health(elapsed,active);
        if(storageFault is not null)return;
        if(TrialExpired && (state.Policy.Groups.Any(g=>g.Enabled) || state.Pending is not null || state.Policy.ChangeDelay!=TimeSpan.Zero)){
            Commit(state with {Policy=state.Policy with {ChangeDelay=TimeSpan.Zero,Groups=state.Policy.Groups.Select(g=>g with {Enabled=false}).ToImmutableArray()},Pending=null});
            log?.Invoke("HostTrialExpired",new {trialEndsUtc,rulesPreserved=true});
        }
        // Sleeping time is excluded by the OS unbiased interrupt clock. Missing awake observations are censored.
        bool suspended=elapsed-active>2500;
        if(suspended)Trace("SuspendedIntervalExcluded",new{elapsedMs=elapsed,awakeMs=active});
        if(!clock.UserSessionPresent && !AgentHealthy)state=state with {Usage=state.Usage with {LastAccountedUntilUtc=state.Clock.EffectiveUtc}};
        else if(elapsed>0 && !suspended) {
            var focused=browsers.Values.Where(v=>clock.Uptime-v.Tick<=3500 && v.Sample.Browser==agent?.ForegroundBrowser && v.Sample.Focused && !v.Sample.Idle).OrderByDescending(v=>v.Tick).FirstOrDefault();
            bool identityUnknown=agent is {IdentityPending:true,Locked:false,IdleSeconds:<60} && state.Policy.Groups.Any(g=>g.Enabled && RuleEngine.HasApplicationScope(g) && QuotaSchedules.HasQuota(g));
            bool browserUnknown=agent?.ForegroundBrowser!="ChatGPTIab" && AgentHealthy && agent is {Locked:false,IdleSeconds:<60,ForegroundBrowser:not null} && !browsers.Values.Any(v=>v.Sample.Browser==agent.ForegroundBrowser && clock.Uptime-v.Tick<=3500 && v.Sample.Focused) && state.Policy.Groups.Any(g=>g.Enabled && RuleEngine.HasWebsiteScope(g) && QuotaSchedules.HasQuota(g));
            bool iab=AgentHealthy&&agent is {ForegroundBrowser:"ChatGPTIab",Locked:false};
            var visible=iab?ManualUrls():[];
            bool manualUnknown=iab&&!visible.IsEmpty&&(manual?.Observation!=ManualObservation.Confirmed||clock.Uptime-manualPulseAt>3500);
            bool known=AgentHealthy && active<4000 && !identityUnknown && !browserUnknown&&!manualUnknown;
            string reason=!AgentHealthy?"collector_heartbeat_stale":active>=4000?"service_iteration_delayed":identityUnknown?"application_identity_pending":browserUnknown?"browser_observation_missing":manualUnknown?"manual_view_observation_missing":"observations_resumed";
            var context=new ActivityContext(agent?.ApplicationId,iab?null:ActiveDomain()){WebsiteUrl=iab?null:focused.Sample?.Url,WebsiteUrls=visible.IsEmpty?default:visible};
            var slice=new UsageSlice(start,TimeSpan.FromMilliseconds(Math.Min(elapsed,31L*86400000)),context,SessionLocked:agent?.Locked??false,IdleAtStart:!visible.IsEmpty?TimeSpan.Zero:TimeSpan.FromSeconds(agent?.IdleSeconds??0),ObservationKnown:known){GapScopeKnown=manualUnknown&&AgentHealthy&&active<4000&&!identityUnknown};
            // Do not censor a newly installed, still-empty workspace or its first short agent startup interval.
            if(known || now-started>=5000 || state.Policy.Groups.Any(RuleEngine.HasScopedQuota)){Gap(!known,start,slice.Duration,reason);state=state with {Usage=RuleEngine.Account(state.Policy,state.Usage,slice)};recordActivity?.Invoke(new(state.Policy.TimeZoneId,slice,agent?.ApplicationName));}
        }
        var result=ChangeQueue.TryApply(state,state.Clock.Reading,AgentHealthy);
        if(result.Status==ApplyStatus.Applied){Commit(result.Snapshot with {UninstallReady=result.UninstallAuthorized});log?.Invoke("ChangeApplied",new {uninstall=result.UninstallAuthorized});}
        else if(now-lastPersist>=2000)Commit(state);
    }
    Status Status() {
        var app=Plans.Apps(state,AgentHealthy);var web=Plans.Web(state,AgentHealthy);
        var usage=state.Policy.Groups.SelectMany(g=>{var specs=QuotaSchedules.Specs(g).ToArray();if(specs.Length==0)return new[]{new UsageRow(g.Id,g.Name,state.Usage.Get(g.Id,RuleEngine.Day(state.Policy,state.Clock.EffectiveUtc)).ChargedTicks,null,false)};return specs.Select(q=>{var period=QuotaPeriods.At(state.Policy,q,state.Clock.EffectiveUtc);var u=state.Usage.Get(g.Id,period.Day,q.Id);return new UsageRow(g.Id,g.Name,u.ChargedTicks,q.Limit.Ticks,u.HasGap,period.Contains(state.Clock.EffectiveUtc),QuotaSchedules.Label(q),q.Id,u.KnownTicks,u.GapChargedTicks,u.GapEstimated,!u.UnresolvedGap);}).ToArray();}).ToImmutableArray();
        var explanations=state.Policy.Groups.Where(g=>g.Enabled).Select(g=>new RuleActivation(g.Id,g.Name,BlockExplanations.Describe(state.Policy,g,state.Usage,state.Clock.Reading,AgentHealthy))).Where(r=>!r.Details.IsEmpty).ToImmutableArray();
        return new(state.Policy,state.Pending,Token,state.Clock.EffectiveUtc,state.Clock.Trusted,state.Clock.Anomaly,storageFault is null,storageFault,state.Applications,usage,new(app.Fingerprint,verifiedApp,actualDenied,actualAllow,appFault),new(web.Required,browsers.Values.Count(v=>clock.Uptime-v.Tick<=3500 && v.Sample.AppliedPlan==web.Fingerprint),AgentHealthy,publishedWeb,firewallClosed,webFault,gateConfirmed,networkStates,ManualExecution()),state.UninstallReady,trialEndsUtc,TrialExpired,explanations);
    }
    public async Task<(AppPlan Apps,WebPlan Web,ImmutableDictionary<string,bool> BrowserHealthy,bool Uninstall,ImmutableDictionary<string,bool> NetworkReady,ImmutableDictionary<string,string> NetworkReasons)> Desired(CancellationToken ct=default) {
        await gate.WaitAsync(ct);try {
            Advance();var web=Plans.Web(state,AgentHealthy);var healthy=new Dictionary<string,bool>();var ready=new Dictionary<string,bool>();var reasons=new Dictionary<string,string>();
            foreach(string name in new[]{"Edge","Chrome"}){
                healthy[name]=browsers.Values.Any(v=>v.Sample.Browser==name && clock.Uptime-v.Tick<=3500 && v.Sample.AppliedPlan==web.Fingerprint && (!AgentHealthy?v.Sample.Focused:agent?.ForegroundBrowser!=name || v.Sample.Focused));
                var decision=networkReadiness.Evaluate(name,web.Required,web.Fingerprint,browsers.Values.Where(v=>v.Sample.Browser==name).Select(v=>new BrowserProof(v.Sample.ProfileId,v.Sample.AppliedPlan,v.Sample.Focused,v.AwakeTick)),clock.AwakeMilliseconds,AgentHealthy,agent?.ForegroundBrowser);
                ready[name]=decision.Ready;reasons[name]=decision.Reason;
            }
            return(Plans.Apps(state,AgentHealthy),web,healthy.ToImmutableDictionary(),state.UninstallReady,ready.ToImmutableDictionary(),reasons.ToImmutableDictionary());
        }finally{gate.Release();}
    }
    public async Task Observe(AppPlan plan,string? appError,string? webHash,bool closed,string? webError,CancellationToken ct=default,bool networkConfirmed=true,ImmutableArray<BrowserNetworkState>? browserNetworks=null) {await gate.WaitAsync(ct);try {appFault=appError;webFault=webError;publishedWeb=webHash;firewallClosed=closed;gateConfirmed=networkConfirmed;if(browserNetworks is {} networks)networkStates=networkConfirmed?networks:networks.Select(n=>n with{Confirmed=false,Reason="network_readback_failed"}).ToImmutableArray();else if(!networkConfirmed)networkStates=networkStates.Select(n=>n with{Confirmed=false,Reason="network_readback_failed"}).ToImmutableArray();if(appError is null){verifiedApp=plan.Fingerprint;actualDenied=plan.Denied.Select(a=>a.Id).ToImmutableArray();actualAllow=plan.Whitelists.Length;}}finally{gate.Release();}}
    public async Task<Response> Handle(Request request,bool trustedAgent=false,CancellationToken ct=default,bool trustedViewer=false,bool viewerProtected=false) {
        var wait=Stopwatch.StartNew();await gate.WaitAsync(ct);try {
            if(wait.ElapsedMilliseconds>250)Trace("AuthorityQueueWait",new{command=request.Command.ToString(),durationMs=wait.ElapsedMilliseconds});
            request.Validate();Advance();
            if(request.Command==Command.ManualViewingPulse){
                if(!trustedViewer)return new(false,"UNAUTHORIZED","该接口仅供受保护的观看采集组件使用。");
                manual=request.ManualViewing!;manualPulseAt=clock.Uptime;manualProtected=viewerProtected;
                if(manual.Observation==ManualObservation.Confirmed&&manual.CaptureAgeMs<=2500){manualUrls=manual.VisibleUrls.Select(WebsitePattern.WebUrl).Distinct().ToImmutableArray();manualProofAt=clock.AwakeMilliseconds-manual.CaptureAgeMs;}
                else if(manual.Observation==ManualObservation.OutsideClient){manualUrls=[];manualProofAt=-100000;}
                string diagnostic=manual.Observation+"/"+manual.Diagnostic+"/"+viewerProtected;
                if(diagnostic!=manualDiagnostic){manualDiagnostic=diagnostic;Trace("ManualViewingState",new{observation=manual.Observation.ToString(),reason=manual.Diagnostic,processProtected=viewerProtected,visiblePages=manual.VisibleUrls.Length});}
                var plan=ManualViewingRules.Decide(Plans.Web(state,AgentHealthy),state.Policy,ManualUrls(),state.UninstallReady);
                return new(true,"OK","人工观看计划",ManualViewingPlan:plan);
            }
            if(request.Command is Command.AgentPulse or Command.BrowserPulse) {
                if(!trustedAgent)return new(false,"UNAUTHORIZED","该接口仅供受保护的采集组件使用。");
                if(request.Agent is {} a){
                    long sampleAt=Math.Min(clock.Uptime,a.Diagnostics?.UptimeMilliseconds??clock.Uptime);
                    if(sampleAt<lastAgent){Trace("CollectorSampleOutOfOrder",new{sampleAt,lastAgent,sample=a.Diagnostics});return new(true,"OK","旧采集样本已忽略");}
                    if(clock.Uptime-sampleAt>500)Trace("CollectorSampleDelayed",new{sampleAgeMs=clock.Uptime-sampleAt,sample=a.Diagnostics});
                    agent=a;lastAgent=sampleAt;Health(0,0);return new(true,"OK","采集已接收");
                }
                var b=request.Browser!;
                if(state.Policy.Groups.Any(g=>g.Enabled && g.WebsiteMode!=TargetMode.None && g.WebsiteDomains.Concat(g.WebsiteExceptions).Any(p=>!WebsitePattern.IsDomain(p))) && b.PatternVersion!=1)b=b with {AppliedPlan=""};
                browsers[b.Browser+":"+b.ProfileId]=(b,clock.Uptime,clock.AwakeMilliseconds);foreach(string key in browsers.Where(v=>clock.AwakeMilliseconds-v.Value.AwakeTick>10000).Select(v=>v.Key).ToArray())browsers.Remove(key);
                if(b.BlockedDomain is {} denied && Plans.Web(state,AgentHealthy).Groups.Any(g=>Plans.Denies(g,denied)))log?.Invoke("WebsiteNavigationDenied",new{browser=b.Browser,domain=PolicyValidation.Domain(denied)});
                return new(true,"OK","浏览器计划",BrowserPlan:Plans.Web(state,AgentHealthy),BrowserNetwork:networkStates.FirstOrDefault(n=>n.Browser==b.Browser));
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
