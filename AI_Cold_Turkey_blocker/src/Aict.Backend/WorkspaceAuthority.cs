using System.Collections.Immutable;
using Aict.Contracts;
using Aict.Core;
using Aict.Enforcement;
using Aict.Storage;

namespace Aict.Backend;

public interface IWorkspaceClock {
    string BootId {get;}
    long UptimeMilliseconds {get;}
    DateTimeOffset UtcNow {get;}
}
public sealed class WorkspaceAuthority : IDisposable {
    readonly StateRepository repository;
    readonly IWorkspaceClock clock;
    readonly HashSet<string> protectedIds;
    readonly SemaphoreSlim gate=new(1,1);
    EngineSnapshot state;
    long revision,lastUptime;
    long dirtyMilliseconds;
    string? storageFault,executionFault,verifiedFingerprint;
    ImmutableArray<string> verifiedIds=[];
    DateTimeOffset? verifiedAt;
    public WorkspaceAuthority(StateRepository repository,IWorkspaceClock clock,IEnumerable<string> protectedIds) {
        this.repository=repository;this.clock=clock;this.protectedIds=protectedIds.ToHashSet(StringComparer.Ordinal);
        var stored=repository.Read();revision=stored.Revision;state=stored.Snapshot;
        long uptime=clock.UptimeMilliseconds;
        if(state.ServiceAnchor is {} anchor && anchor.BootId==clock.BootId && anchor.ClockWasTrusted && uptime>=anchor.UptimeMilliseconds) {
            var recovered=state.Clock.EffectiveUtc+TimeSpan.FromMilliseconds(uptime-anchor.UptimeMilliseconds);
            state=state with {Clock=new RuntimeClock(recovered,true).Advance(clock.UtcNow,TimeSpan.Zero)};
        }
        lastUptime=uptime;ValidateSupported(state.Policy);_ = ChangeQueue.TryApply(state,state.Clock.Reading,telemetryHealthy:false);
    }
    public static EngineSnapshot Initial(IWorkspaceClock clock) => new() {
        Policy=new() {ChangeDelay=TimeSpan.Zero},Clock=new(clock.UtcNow,true),ServiceAnchor=new(clock.BootId,clock.UptimeMilliseconds,true)
    };
    string Token => PolicyJson.Sha256(PolicyJson.Fingerprint(state.Policy)+"/"+state.Pending?.Id.ToString("N")+"/"+PolicyJson.Serialize(state.Applications));
    void ValidateSupported(PolicyConfiguration policy) {
        PolicyValidation.Validate(policy);
        foreach(var group in policy.Groups) {
            if(group.ApplicationMode==TargetMode.AllowSelected || group.WebsiteMode!=TargetMode.None || !group.WebsiteDomains.IsEmpty || !group.WebsiteExceptions.IsEmpty || QuotaSchedules.HasQuota(group))
                throw new NotSupportedException("本轮只连接应用黑名单、周计划、循环和锁定。网站、使用额度和应用白名单尚未接入。");
            foreach(string id in group.ApplicationIds.Concat(group.ApplicationExceptions)) {
                if(protectedIds.Contains(id)) throw new ArgumentException("系统必需程序和恢复工具不能加入限制。");
                if(!state.Applications.Any(a=>a.Id==id)) throw new ArgumentException("请先通过后台登记应用身份。");
            }
        }
    }
    void Commit(EngineSnapshot next) {
        next=next with {ServiceAnchor=new(clock.BootId,lastUptime,next.Clock.Trusted)};
        try {if(!repository.TryCommit(revision,next)) throw new InvalidDataException("Another writer changed state");revision++;state=next;dirtyMilliseconds=0;}
        catch(Exception e) when(e is IOException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException) {
            storageFault="规则存储校验/写入失败。修改已停止；现有系统限制保留，请保留数据排查。";throw new InvalidOperationException(storageFault,e);
        }
    }
    void Advance() {
        long uptime=clock.UptimeMilliseconds;
        if(uptime<lastUptime) {state=state with {Clock=state.Clock with {Trusted=false,Anomaly="UPTIME_REGRESSED"}};lastUptime=uptime;return;}
        long elapsed=uptime-lastUptime;lastUptime=uptime;dirtyMilliseconds=checked(dirtyMilliseconds+elapsed);
        state=state with {Clock=state.Clock.Advance(clock.UtcNow,TimeSpan.FromMilliseconds(elapsed))};
        if(storageFault is not null) return;
        var applied=ChangeQueue.TryApply(state,state.Clock.Reading,telemetryHealthy:false);
        if(applied.Status==ApplyStatus.Applied || dirtyMilliseconds>=5000) Commit(applied.Snapshot);
    }
    PreviewStatus Status() {
        var plan=ApplicationPlanner.Compile(state);
        return new(state.Policy,state.Pending,Token,state.Clock.EffectiveUtc,state.Clock.Trusted,state.Clock.Anomaly,
            storageFault is null?ChangeQueue.TryApply(state,state.Clock.Reading,false).Status:ApplyStatus.PolicyConflict,storageFault is null,storageFault) {
            Applications=state.Applications,Execution=new("VM_INTERACTIVE_APPLICATION_CONTROL",plan.Fingerprint,verifiedFingerprint,verifiedIds,verifiedAt,executionFault)
        };
    }
    public async Task TickAsync(CancellationToken cancellation=default) {
        await gate.WaitAsync(cancellation);try {Advance();} catch(InvalidOperationException) when(storageFault is not null) {} finally {gate.Release();}
    }
    public async Task<(ApplicationPlan Plan,ImmutableArray<ApplicationTarget> Targets)> DesiredAsync(CancellationToken cancellation=default) {
        await gate.WaitAsync(cancellation);try {return(ApplicationPlanner.Compile(state),state.Applications);} finally {gate.Release();}
    }
    public async Task ObserveAsync(string fingerprint,ImmutableArray<string> actual,DateTimeOffset at,string? fault,CancellationToken cancellation=default) {
        await gate.WaitAsync(cancellation);
        try {executionFault=fault;if(fault is null) {verifiedFingerprint=fingerprint;verifiedIds=actual;verifiedAt=at;}}
        finally {gate.Release();}
    }
    public async Task<PreviewResponse> HandleAsync(PreviewRequest request,CancellationToken cancellation=default) {
        await gate.WaitAsync(cancellation);
        try {
            request.Validate();Advance();
            if(request.Command==PreviewCommand.Status) return new(true,"OK","后台状态",Status());
            if(request.Command==PreviewCommand.ExportJson) return new(true,"OK","规则数据已读取；未采集真实用量。",Status(),PolicyJson.Serialize(new {mode="VM_INTERACTIVE_APPLICATION_CONTROL",telemetryConnected=false,state}));
            if(request.Command==PreviewCommand.ExportCsv) return new(true,"OK","未采集真实用量，空记录不代表零使用。",Status(),UsageExport.ToCsv(state.Policy,state.Usage));
            if(storageFault is not null) return new(false,"STORAGE_FAULT",storageFault,Status());
            if(request.EditToken!=Token) return new(false,"STALE_EDIT","后台配置或待生效修改已变化，请重新读取。",Status());
            if(request.Command==PreviewCommand.RegisterApplication) {
                if(!state.Clock.Trusted || state.Pending is not null || RuleEngine.IsModificationLocked(state.Policy,state.Usage,state.Clock.Reading,false)) return new(false,"LOCKED","锁定或等待修改期间不能登记新的应用。",Status());
                var app=request.Application!;
                if(protectedIds.Contains(app.Id)) return new(false,"ESSENTIAL_APP","系统必需程序和本软件恢复工具不能被封锁。",Status());
                if(state.Applications.Any(a=>a.Id==app.Id)) return new(true,"ALREADY_REGISTERED","此文件身份已登记。",Status());
                if(state.Applications.Length>=64) return new(false,"CATALOG_FULL","当前最多登记64个文件版本。",Status());
                Commit(state with {Applications=state.Applications.Add(app)});return new(true,"REGISTERED","应用身份已保存；尚未添加到生效规则。",Status());
            }
            if(request.Command==PreviewCommand.CancelPending) {
                if(state.Pending?.Id!=request.PendingId) return new(false,"STALE_EDIT","待生效修改已变化。",Status());
                Commit(ChangeQueue.Cancel(state));return new(true,"CANCELLED","待生效修改已撤销，原规则保留。",Status());
            }
            if(state.Pending is not null) return new(false,"PENDING_EXISTS","已有冻结修改。先撤销后再提交新内容，等待会重新计时。",Status());
            ValidateSupported(request.Policy!);
            var next=ChangeQueue.Submit(state,request.Policy!,state.Clock.Reading);var apply=ChangeQueue.TryApply(next,state.Clock.Reading,false);Commit(apply.Snapshot);
            return new(true,apply.Status==ApplyStatus.Applied?"SAVED_EXECUTION_PENDING":"QUEUED",apply.Status==ApplyStatus.Applied?
                "规则已保存，等待系统执行确认；请查看左侧执行状态。":"完整修改已冻结，按当前规则的延迟和锁定等待。",Status());
        } catch(Exception e) when(e is ArgumentException or NotSupportedException or InvalidOperationException or InvalidDataException or IOException) {
            return new(false,storageFault is null?"REJECTED":"STORAGE_FAULT",storageFault ?? e.Message);
        } finally {gate.Release();}
    }
    public void Dispose()=>gate.Dispose();
}
