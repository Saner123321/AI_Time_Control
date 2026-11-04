using System.Buffers.Binary;
using System.Text;
using Aict.Backend;
using Aict.Contracts;
using Aict.Core;
using Aict.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Aict.Core.Tests;

[TestClass]
public sealed class PreviewTests {
    sealed class ManualTime : TimeProvider {
        public DateTimeOffset Wall=new(2026,11,2,1,0,0,TimeSpan.Zero);
        public long Stamp;
        public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
        public override long GetTimestamp()=>Stamp;
        public override DateTimeOffset GetUtcNow()=>Wall;
        public void Add(TimeSpan elapsed) {Wall+=elapsed;Stamp+=elapsed.Ticks;}
    }
    static (StateRepository Repository,ManualTime Time) NewRepository(PolicyConfiguration? policy=null) {
        string directory=Path.Combine(Environment.GetEnvironmentVariable("AICT_TEST_ROOT") ?? throw new InvalidOperationException("Use the project test script"),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var time=new ManualTime();return (StateRepository.CreateNew(Path.Combine(directory,"preview.sqlite"),new() {Policy=policy ?? new() {ChangeDelay=TimeSpan.Zero},Clock=new(time.Wall,true)}),time);
    }
    static async Task<PreviewStatus> Status(PreviewAuthority host) => (await host.HandleAsync(new())).Status!;
    [TestMethod]
    public async Task PreviewIsExplicitlyUnenforcedAndNeverClaimsRealUsage() {
        var (repo,time)=NewRepository();using var host=new PreviewAuthority(repo,time);var status=await Status(host);
        Assert.IsFalse(status.SystemEnforcementActive);Assert.IsFalse(status.TelemetryConnected);StringAssert.Contains(status.Mode,"NO_SYSTEM_ENFORCEMENT");
        var export=await host.HandleAsync(new() {Command=PreviewCommand.ExportJson});var parsed=PolicyJson.Parse<PreviewExport>(export.ExportText!);
        Assert.IsFalse(parsed.TelemetryConnected);Assert.AreEqual(0,parsed.State.Usage.Accounts.Count);
        StringAssert.Contains(parsed.UsageNotice,"不代表零使用");
    }
    [TestMethod]
    public async Task CommitThenRestartRetainsFrozenModificationAndOldDelay() {
        var policy=new PolicyConfiguration {ChangeDelay=TimeSpan.FromHours(24)};
        var (repo,time)=NewRepository(policy);Guid frozen;
        using(var host=new PreviewAuthority(repo,time)) {
            string token=(await Status(host)).EditToken;
            var response=await host.HandleAsync(new() {Command=PreviewCommand.SubmitPolicy,EditToken=token,Policy=policy with {ChangeDelay=TimeSpan.Zero}});
            Assert.IsTrue(response.Ok);Assert.AreEqual("QUEUED",response.Code);frozen=response.Status!.Pending!.Id;
            Assert.AreEqual(time.Wall+TimeSpan.FromHours(24),response.Status.Pending.NotBeforeUtc);
        }
        using var restarted=new PreviewAuthority(StateRepository.OpenExisting(repo.Path),time);
        var status=await Status(restarted);Assert.AreEqual(frozen,status.Pending!.Id);Assert.AreEqual(policy.ChangeDelay,status.Policy.ChangeDelay);
        time.Add(TimeSpan.FromHours(24));await restarted.TickAsync();Assert.IsNull((await Status(restarted)).Pending);
        Assert.AreEqual(TimeSpan.Zero,(await Status(restarted)).Policy.ChangeDelay);
    }
    [TestMethod]
    public async Task StaleEditorCannotOverwriteAnotherChangeAndTicksDoNotInvalidateDraft() {
        var (repo,time)=NewRepository();using var host=new PreviewAuthority(repo,time);
        string token=(await Status(host)).EditToken;time.Add(TimeSpan.FromSeconds(6));await host.TickAsync();Assert.AreEqual(token,(await Status(host)).EditToken);
        var first=await host.HandleAsync(new() {Command=PreviewCommand.SubmitPolicy,EditToken=token,Policy=new() {ChangeDelay=TimeSpan.FromHours(1)}});
        Assert.IsTrue(first.Ok);
        var stale=await host.HandleAsync(new() {Command=PreviewCommand.SubmitPolicy,EditToken=token,Policy=new() {ChangeDelay=TimeSpan.Zero}});
        Assert.AreEqual("STALE_EDIT",stale.Code);Assert.AreEqual(TimeSpan.FromHours(1),(await Status(host)).Policy.ChangeDelay);
    }
    [TestMethod]
    public async Task ExistingFrozenPayloadCannotBeReplacedWithoutCancellationAndNewWait() {
        var policy=new PolicyConfiguration {ChangeDelay=TimeSpan.FromHours(1)};var (repo,time)=NewRepository(policy);using var host=new PreviewAuthority(repo,time);
        var first=await host.HandleAsync(new() {Command=PreviewCommand.SubmitPolicy,EditToken=(await Status(host)).EditToken,Policy=policy with {ChangeDelay=TimeSpan.Zero}});
        var frozen=first.Status!.Pending!;time.Add(TimeSpan.FromMinutes(30));await host.TickAsync();
        var replacement=await host.HandleAsync(new() {Command=PreviewCommand.SubmitPolicy,EditToken=(await Status(host)).EditToken,Policy=policy});
        Assert.AreEqual("PENDING_EXISTS",replacement.Code);Assert.AreEqual(frozen,(await Status(host)).Pending);
        var wrong=await host.HandleAsync(new() {Command=PreviewCommand.CancelPending,EditToken=(await Status(host)).EditToken,PendingId=Guid.NewGuid()});Assert.IsFalse(wrong.Ok);
        var cancel=await host.HandleAsync(new() {Command=PreviewCommand.CancelPending,EditToken=(await Status(host)).EditToken,PendingId=frozen.Id});Assert.IsTrue(cancel.Ok);
        Assert.AreEqual(policy,(await Status(host)).Policy);
        var again=await host.HandleAsync(new() {Command=PreviewCommand.SubmitPolicy,EditToken=(await Status(host)).EditToken,Policy=policy});
        Assert.AreNotEqual(frozen.Id,again.Status!.Pending!.Id);Assert.AreEqual(time.Wall+TimeSpan.FromHours(1),again.Status.Pending.NotBeforeUtc);
    }
    [TestMethod]
    public async Task UnknownClockCannotBeUnlockedByClientAndBackwardRestartDoesNotTrustWallTime() {
        var (repo,time)=NewRepository();time.Wall-=TimeSpan.FromHours(2);using var host=new PreviewAuthority(repo,time);
        Assert.IsFalse((await Status(host)).ClockConsistent);
        var request=new PreviewRequest {Command=PreviewCommand.SubmitPolicy,EditToken=(await Status(host)).EditToken,Policy=new()};
        Assert.IsFalse((await host.HandleAsync(request)).Ok);Assert.IsNull(repo.Read().Snapshot.Pending);
    }
    [TestMethod]
    public async Task CorruptionFailsClosedAndDoesNotReportAnUnsavedPolicyAsSaved() {
        var (repo,time)=NewRepository();using var host=new PreviewAuthority(repo,time);var before=await Status(host);
        using(var sql=new SqliteConnection($"Data Source={repo.Path};Pooling=False")) {sql.Open();using var cmd=sql.CreateCommand();cmd.CommandText="UPDATE engine_state SET sha256='invalid';";cmd.ExecuteNonQuery();}
        var response=await host.HandleAsync(new() {Command=PreviewCommand.SubmitPolicy,EditToken=before.EditToken,Policy=new()});Assert.IsFalse(response.Ok);Assert.AreEqual("STORAGE_FAULT",response.Code);
        Assert.IsFalse((await Status(host)).StorageHealthy);Assert.AreEqual(before.EditToken,(await Status(host)).EditToken);
        Assert.ThrowsExactly<InvalidDataException>(()=>repo.Read());
    }
    [TestMethod]
    public async Task ConcurrentSubmissionsCannotBothUseTheSameEditToken() {
        var (repo,time)=NewRepository();using var host=new PreviewAuthority(repo,time);string token=(await Status(host)).EditToken;
        var a=new PreviewRequest {Command=PreviewCommand.SubmitPolicy,EditToken=token,Policy=new() {ChangeDelay=TimeSpan.FromHours(1)}};
        var b=a with {Policy=new() {ChangeDelay=TimeSpan.FromHours(2)}};
        var results=await Task.WhenAll(host.HandleAsync(a),host.HandleAsync(b));Assert.AreEqual(1,results.Count(r=>r.Ok));
    }
    [TestMethod]
    public async Task ElapsedDelayDoesNotBypassTheOldScheduleLock() {
        var group=new RuleGroup {Id=Guid.NewGuid(),Name="工作",WebsiteMode=TargetMode.BlockSelected,WebsiteDomains=["example.com"],Windows=[new(Weekdays.All,9*60,18*60)],LockWhileBlocking=true};
        var policy=new PolicyConfiguration {ChangeDelay=TimeSpan.FromHours(1),Groups=[group]};
        var (repo,time)=NewRepository(policy);using var host=new PreviewAuthority(repo,time);
        var response=await host.HandleAsync(new() {Command=PreviewCommand.SubmitPolicy,EditToken=(await Status(host)).EditToken,Policy=policy with {Groups=[]}});Assert.IsTrue(response.Ok);
        time.Add(TimeSpan.FromHours(1));await host.TickAsync();Assert.AreEqual(ApplyStatus.Locked,(await Status(host)).PendingStatus);
        time.Add(TimeSpan.FromHours(8));await host.TickAsync();Assert.IsNull((await Status(host)).Pending);Assert.IsTrue((await Status(host)).Policy.Groups.IsEmpty);
    }
    [TestMethod]
    public async Task CollectorLossAloneDoesNotLockAnUnspentQuotaInPreview() {
        var group=new RuleGroup {Id=Guid.NewGuid(),Name="额度",WebsiteMode=TargetMode.BlockSelected,WebsiteDomains=["example.com"],DailyQuota=TimeSpan.FromHours(1),LockWhileBlocking=true};
        var policy=new PolicyConfiguration {ChangeDelay=TimeSpan.Zero,Groups=[group]};var (repo,time)=NewRepository(policy);using var host=new PreviewAuthority(repo,time);
        var queued=await host.HandleAsync(new() {Command=PreviewCommand.SubmitPolicy,EditToken=(await Status(host)).EditToken,Policy=policy with {Groups=[]}});
        Assert.IsTrue(queued.Ok);Assert.AreEqual(ApplyStatus.NoPendingChange,queued.Status!.PendingStatus);Assert.IsNull(queued.Status.Pending);Assert.IsTrue(queued.Status.Policy.Groups.IsEmpty);
    }
    [TestMethod]
    public async Task WallClockJumpCannotFinishAnHourDelayInOneSecond() {
        var policy=new PolicyConfiguration {ChangeDelay=TimeSpan.FromHours(1)};var (repo,time)=NewRepository(policy);using var host=new PreviewAuthority(repo,time);
        var queued=await host.HandleAsync(new() {Command=PreviewCommand.SubmitPolicy,EditToken=(await Status(host)).EditToken,Policy=new()});
        time.Add(TimeSpan.FromSeconds(1));time.Wall+=TimeSpan.FromDays(5);await host.TickAsync();var after=await Status(host);
        Assert.IsFalse(after.ClockConsistent);Assert.AreEqual(queued.Status!.Pending!.Id,after.Pending!.Id);Assert.AreEqual(ApplyStatus.Locked,after.PendingStatus);
    }
    [TestMethod]
    public void StrictContractRejectsInjectedAuthorityAndUnexpectedFields() {
        foreach(string injected in new[]{"clock","usage","snapshot","databasePath","commandLine","trusted","pending"}) {
            Assert.ThrowsExactly<System.Text.Json.JsonException>(()=>PolicyJson.Parse<PreviewRequest>("{\"command\":\"Status\",\""+injected+"\":{}}"));
        }
        Assert.ThrowsExactly<ArgumentException>(()=>new PreviewRequest {Command=(PreviewCommand)999}.Validate());
        Assert.ThrowsExactly<ArgumentException>(()=>new PreviewRequest {Command=PreviewCommand.Status,Policy=new()}.Validate());
        Assert.ThrowsExactly<ArgumentException>(()=>new PreviewRequest {Command=PreviewCommand.SubmitPolicy,Policy=new()}.Validate());
    }
    [TestMethod]
    public async Task FramingRoundTripsAndRejectsOversizedTruncatedAndInvalidUtf8BeforeParsing() {
        using var normal=new MemoryStream();await PipeFrames.WriteAsync(normal,"{\"test\":\"中文\"}",100);normal.Position=0;Assert.AreEqual("{\"test\":\"中文\"}",await PipeFrames.ReadAsync(normal,100));
        foreach(int size in new[]{-1,0,PipeFrames.MaxRequestBytes+1,int.MaxValue}) {byte[] header=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(header,size);using var bad=new MemoryStream(header);await Assert.ThrowsExactlyAsync<InvalidDataException>(()=>PipeFrames.ReadAsync(bad,PipeFrames.MaxRequestBytes));}
        using var truncated=new MemoryStream(new byte[]{5,0,0,0,65});await Assert.ThrowsExactlyAsync<EndOfStreamException>(()=>PipeFrames.ReadAsync(truncated,100));
        using var utf8=new MemoryStream(new byte[]{1,0,0,0,255});await Assert.ThrowsExactlyAsync<DecoderFallbackException>(()=>PipeFrames.ReadAsync(utf8,100));
    }
}
