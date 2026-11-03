using Aict.Core;
using Aict.Contracts;
using Aict.Storage;

namespace Aict.Backend;

// Ordinary-user development host. This type neither installs a service nor enforces OS policy.
public sealed class PreviewAuthority : IDisposable {
    readonly StateRepository repository;
    readonly TimeProvider time;
    readonly SemaphoreSlim gate=new(1,1);
    EngineSnapshot state;
    long revision,lastTimestamp;
    TimeSpan sinceCheckpoint;
    string? fault;
    public PreviewAuthority(StateRepository repository,TimeProvider? time=null) {
        this.repository=repository;this.time=time ?? TimeProvider.System;
        var stored=repository.Read();revision=stored.Revision;state=stored.Snapshot;
        // Deliberately a SIMULATION anchor, not a verified production restart clock.
        state=state with {Clock=state.Clock.ReconcileVerifiedAnchor(this.time.GetUtcNow())};
        _=ChangeQueue.TryApply(state,state.Clock.Reading,telemetryHealthy:false);
        lastTimestamp=this.time.GetTimestamp();
    }
    string Token => PolicyJson.Sha256(PolicyJson.Fingerprint(state.Policy)+"/"+state.Pending?.Id.ToString("N"));
    PreviewStatus Status() => new(state.Policy,state.Pending,Token,state.Clock.EffectiveUtc,state.Clock.Trusted,state.Clock.Anomaly,
        fault is null?ChangeQueue.TryApply(state,state.Clock.Reading,telemetryHealthy:false).Status:ApplyStatus.PolicyConflict,fault is null,fault);
    void Persist(EngineSnapshot next) {
        try {
            if(!repository.TryCommit(revision,next)) throw new InvalidOperationException("Concurrent database writer detected");
            revision++;state=next;sinceCheckpoint=TimeSpan.Zero;
        } catch(Exception e) when(e is IOException or InvalidDataException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException) {
            fault="存储校验或写入失败；后台已停止接受修改，请保留数据库排查。";
            throw new InvalidOperationException(fault,e);
        }
    }
    void Advance() {
        long now=time.GetTimestamp();var elapsed=time.GetElapsedTime(lastTimestamp,now);lastTimestamp=now;
        state=state with {Clock=state.Clock.Advance(time.GetUtcNow(),elapsed)};sinceCheckpoint+=elapsed;
        if(fault is not null) return;
        var applied=ChangeQueue.TryApply(state,state.Clock.Reading,telemetryHealthy:false);
        if(applied.Status==ApplyStatus.Applied || sinceCheckpoint>=TimeSpan.FromSeconds(5)) Persist(applied.Snapshot);
    }
    public async Task TickAsync(CancellationToken cancellation=default) {
        await gate.WaitAsync(cancellation);
        try {Advance();} catch(InvalidOperationException) when(fault is not null) { } finally {gate.Release();}
    }
    public async Task<PreviewResponse> HandleAsync(PreviewRequest request,CancellationToken cancellation=default) {
        await gate.WaitAsync(cancellation);
        try {
            request.Validate();Advance();
            if(request.Command==PreviewCommand.RegisterApplication) return new(false,"PREVIEW_ONLY","普通预览不向系统服务注册应用。");
            if(request.Command==PreviewCommand.Status) return new(true,"OK","开发预览状态",Status());
            if(request.Command==PreviewCommand.ExportJson) return new(true,"OK","仅导出预览数据",Status(),PolicyJson.Serialize(new PreviewExport(
                "DEVELOPMENT_PREVIEW_NO_SYSTEM_ENFORCEMENT",false,"未采集真实使用量；空记录不代表零使用。",state)));
            if(request.Command==PreviewCommand.ExportCsv) return new(true,"OK","未采集真实使用量；CSV仅含现存记录，空表不代表零使用。",Status(),UsageExport.ToCsv(state.Policy,state.Usage));
            if(fault is not null) return new(false,"STORAGE_FAULT",fault,Status());
            if(request.EditToken!=Token) return new(false,"STALE_EDIT","规则或待生效修改已变化。请刷新后重新编辑，草稿未提交。",Status());
            if(request.Command==PreviewCommand.CancelPending) {
                if(state.Pending?.Id!=request.PendingId) return new(false,"STALE_EDIT","待生效修改已变化。",Status());
                Persist(ChangeQueue.Cancel(state));return new(true,"CANCELLED","待生效修改已撤销，原规则保留。",Status());
            }
            if(state.Pending is not null) return new(false,"PENDING_EXISTS","已有冻结修改。请先撤销，再提交新的完整修改并重新计时。",Status());
            var next=ChangeQueue.Submit(state,request.Policy!,state.Clock.Reading);
            var applied=ChangeQueue.TryApply(next,state.Clock.Reading,telemetryHealthy:false);
            Persist(applied.Snapshot);
            return new(true,applied.Status==ApplyStatus.Applied?"APPLIED":"QUEUED",applied.Status==ApplyStatus.Applied?
                "预览规则已生效；未执行系统封锁。":"完整修改已冻结保存，等待当前规则允许生效；未执行系统封锁。",Status());
        } catch(Exception e) when(e is ArgumentException or InvalidOperationException or IOException or System.Text.Json.JsonException) {
            return new(false,fault is null?"REJECTED":"STORAGE_FAULT",fault ?? "请求未获接受："+e.Message);
        } finally {gate.Release();}
    }
    public void Dispose() => gate.Dispose();
}
