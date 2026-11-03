using System.Collections.Immutable;
using Aict.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly:Parallelize(Scope=ExecutionScope.MethodLevel)]
namespace Aict.Core.Tests;

internal static class Samples {
    internal static readonly Guid GroupId=Guid.Parse("b2f7e564-86e8-4b2a-b7bb-38d75d52d857");
    internal static DateTimeOffset At(int day,int hour=0,int minute=0,int second=0) => new DateTimeOffset(2026,11,day,hour,minute,second,TimeSpan.FromHours(8)).ToUniversalTime();
    internal static RuleGroup Quota() => new() {Id=GroupId,Name="Distractors",ApplicationMode=TargetMode.BlockSelected,ApplicationIds=["app.video"],WebsiteMode=TargetMode.BlockSelected,WebsiteDomains=["video.example"],DailyQuota=TimeSpan.FromSeconds(60)};
    internal static PolicyConfiguration Policy(RuleGroup? group=null) => new() {ChangeDelay=TimeSpan.Zero,Groups=[group ?? Quota()]};
    internal static RuleGroup Schedule() => Quota() with {DailyQuota=null,LockWhileBlocking=true,Windows=[new(Weekdays.Monday|Weekdays.Tuesday|Weekdays.Wednesday|Weekdays.Thursday|Weekdays.Friday,9*60,18*60)]};
    internal static EngineSnapshot Snapshot(PolicyConfiguration policy) => new() {Policy=policy,Clock=new(At(2),true)};
}

[TestClass]
public sealed class ScheduleTests {
    [TestMethod]
    [DataRow(2,8,59,false)] [DataRow(2,9,0,true)] [DataRow(2,17,59,true)] [DataRow(2,18,0,false)] [DataRow(7,10,0,false)]
    public void WeeklyHalfOpenWeekdayBoundary(int day,int hour,int minute,bool expected) {
        var result=RuleEngine.Evaluate(Samples.Policy(Samples.Schedule()),new(),new("app.video"),new(Samples.At(day,hour,minute)));
        Assert.AreEqual(expected,result.Blocked);
    }
    [TestMethod]
    [DataRow(2,22,false)] [DataRow(2,23,true)] [DataRow(3,1,true)] [DataRow(3,2,false)]
    public void CrossMidnightBelongsToStartingWeekday(int day,int hour,bool expected) {
        var g=Samples.Schedule() with {Windows=[new(Weekdays.Monday,23*60,2*60)]};
        Assert.AreEqual(expected,RuleEngine.Evaluate(Samples.Policy(g),new(),new("app.video"),new(Samples.At(day,hour))).Blocked);
    }
    [TestMethod]
    public void SundayWrapAndEndAt2400() {
        var g=Samples.Schedule() with {Windows=[new(Weekdays.Sunday,23*60,60)]};
        Assert.IsTrue(RuleEngine.Evaluate(Samples.Policy(g),new(),new("app.video"),new(Samples.At(2,0,30))).Blocked);
        g=g with {Windows=[new(Weekdays.Sunday,0,1440)]};
        Assert.IsFalse(RuleEngine.Evaluate(Samples.Policy(g),new(),new("app.video"),new(Samples.At(2))).Blocked);
    }
    [TestMethod]
    public void CycleAnchorDoesNotRestartAfterSerialization() {
        var anchor=Samples.At(2,9);var c=new FixedCycle(anchor,TimeSpan.FromMinutes(25),TimeSpan.FromMinutes(5));
        var copy=PolicyJson.Parse<FixedCycle>(PolicyJson.Serialize(c));
        Assert.IsFalse(RuleEngine.CycleActive(copy,anchor-TimeSpan.FromTicks(1)));
        Assert.IsTrue(RuleEngine.CycleActive(copy,anchor));
        Assert.IsFalse(RuleEngine.CycleActive(copy,anchor+TimeSpan.FromMinutes(25)));
        Assert.IsTrue(RuleEngine.CycleActive(copy,anchor+TimeSpan.FromMinutes(30)));
        Assert.IsFalse(RuleEngine.CycleActive(copy,anchor+TimeSpan.FromDays(1)+TimeSpan.FromMinutes(25)));
    }
    [TestMethod]
    public void HardScheduleBlocksWithUnusedQuota() {
        var g=Samples.Schedule() with {DailyQuota=TimeSpan.FromHours(1)};
        var result=RuleEngine.Evaluate(Samples.Policy(g),new(),new("app.video"),new(Samples.At(2,10)));
        Assert.IsTrue(result.Blocked);Assert.AreEqual(TimeSpan.FromHours(1),result.Groups[0].QuotaRemaining);
    }
    [TestMethod]
    public void ExceptionsDoNotOverrideAnotherGroup() {
        var a=Samples.Quota() with {AlwaysBlock=true,ApplicationExceptions=["app.video"],WebsiteMode=TargetMode.None};
        var b=a with {Id=Guid.NewGuid(),ApplicationExceptions=[]};var p=Samples.Policy(a) with {Groups=[a,b]};
        Assert.IsTrue(RuleEngine.Evaluate(p,new(),new("app.video"),new(Samples.At(2,10))).Blocked);
    }
    [TestMethod]
    public void AllowlistAndTrustedEssentialsRemainSeparateFromWebExceptions() {
        var g=Samples.Quota() with {AlwaysBlock=true,ApplicationMode=TargetMode.AllowSelected,ApplicationIds=["app.work"],WebsiteMode=TargetMode.None};
        Assert.IsFalse(RuleEngine.Evaluate(Samples.Policy(g),new(),new("app.work"),new(Samples.At(2))).Blocked);
        Assert.IsTrue(RuleEngine.Evaluate(Samples.Policy(g),new(),new("app.game"),new(Samples.At(2))).Blocked);
        Assert.IsFalse(RuleEngine.Evaluate(Samples.Policy(g),new(),new("SYSTEM.SHELL"),new(Samples.At(2)),essentialApplications:new HashSet<string>{"system.shell"}).Blocked);
    }
    [TestMethod]
    public void DomainBoundaryAndIdnaAreExact() {
        var g=Samples.Quota() with {AlwaysBlock=true,ApplicationMode=TargetMode.None};var p=Samples.Policy(g);
        Assert.IsTrue(RuleEngine.Evaluate(p,new(),new(WebsiteDomain:"www.VIDEO.example."),new(Samples.At(2))).Blocked);
        Assert.IsFalse(RuleEngine.Evaluate(p,new(),new(WebsiteDomain:"evilvideo.example"),new(Samples.At(2))).Blocked);
        Assert.AreEqual("xn--bcher-kva.example",PolicyValidation.Domain("bücher.example"));
        Assert.ThrowsExactly<ArgumentException>(()=>PolicyValidation.Domain("https://video.example/path"));
    }
    [TestMethod]
    public void ApplicationExceptionDoesNotExemptWebsiteDimension() {
        var g=Samples.Quota() with {AlwaysBlock=true,ApplicationExceptions=["app.video"]};
        Assert.IsTrue(RuleEngine.Evaluate(Samples.Policy(g),new(),new("app.video","video.example"),new(Samples.At(2))).Blocked);
    }
    [TestMethod]
    public void InvalidImportedRulesAreRejected() {
        var p=Samples.Policy(Samples.Schedule());
        Assert.ThrowsExactly<ArgumentException>(()=>PolicyValidation.Validate(p with {Groups=[p.Groups[0],p.Groups[0]]}));
        Assert.ThrowsExactly<ArgumentException>(()=>PolicyValidation.Validate(p with {Groups=[p.Groups[0] with {Windows=[new(Weekdays.Monday,60,60)]}]}));
        Assert.ThrowsExactly<ArgumentException>(()=>PolicyValidation.Validate(p with {TimeZoneId="Mars/Unknown"}));
        Assert.ThrowsExactly<System.Text.Json.JsonException>(()=>PolicyJson.Parse<PolicyConfiguration>("{\"bypassLocks\":true}"));
    }
}

[TestClass]
public sealed class UsageTests {
    [TestMethod]
    public void TrustedEssentialDoesNotConsumeAnAllowlistBudget() {
        var p=Samples.Policy(Samples.Quota() with {ApplicationMode=TargetMode.AllowSelected,ApplicationIds=[],WebsiteMode=TargetMode.None});
        var ledger=RuleEngine.Account(p,new(),new(Samples.At(2),TimeSpan.FromSeconds(10),new("system.shell")),new HashSet<string>{"system.shell"});
        Assert.AreEqual(0L,ledger.Get(Samples.GroupId,new(2026,11,2)).KnownTicks);
    }
    [TestMethod]
    public void ApplicationAndWebsiteUseOneSharedCharge() {
        var p=Samples.Policy();var ledger=RuleEngine.Account(p,new(),new(Samples.At(2,10),TimeSpan.FromSeconds(10),new("app.video","video.example")));
        Assert.AreEqual(TimeSpan.FromSeconds(10).Ticks,ledger.Get(Samples.GroupId,new(2026,11,2)).KnownTicks);
    }
    [TestMethod]
    public void MidnightSplitsUsageAndDoesNotCarryTheAllowance() {
        var p=Samples.Policy();var ledger=RuleEngine.Account(p,new(),new(Samples.At(2,23,59,40),TimeSpan.FromSeconds(40),new("app.video")));
        Assert.AreEqual(TimeSpan.FromSeconds(20).Ticks,ledger.Get(Samples.GroupId,new(2026,11,2)).KnownTicks);
        Assert.AreEqual(TimeSpan.FromSeconds(20).Ticks,ledger.Get(Samples.GroupId,new(2026,11,3)).KnownTicks);
        Assert.AreEqual(TimeSpan.FromSeconds(40),RuleEngine.Evaluate(p,ledger,new("app.video"),new(Samples.At(3,0,1))).Groups[0].QuotaRemaining);
    }
    [TestMethod]
    public void IdleThresholdCountsOnlyTheActivePart() {
        var p=Samples.Policy();var ledger=RuleEngine.Account(p,new(),new(Samples.At(2),TimeSpan.FromSeconds(5),new("app.video"),IdleAtStart:TimeSpan.FromSeconds(59)));
        Assert.AreEqual(TimeSpan.FromSeconds(1).Ticks,ledger.Get(Samples.GroupId,new(2026,11,2)).KnownTicks);
    }
    [TestMethod]
    [DataRow(false,false,false)] [DataRow(true,true,false)] [DataRow(true,false,true)]
    public void BackgroundLockAndSleepDoNotConsume(bool foreground,bool locked,bool suspended) {
        var p=Samples.Policy();var ledger=RuleEngine.Account(p,new(),new(Samples.At(2),TimeSpan.FromHours(2),new("app.video"),Foreground:foreground,SessionLocked:locked,Suspended:suspended));
        var account=ledger.Get(Samples.GroupId,new(2026,11,2));Assert.AreEqual(0L,account.KnownTicks);Assert.IsFalse(account.HasGap);
    }
    [TestMethod]
    public void DuplicateAndOverlapAreNotChargedTwice() {
        var p=Samples.Policy();var a=new UsageSlice(Samples.At(2),TimeSpan.FromSeconds(10),new("app.video"));
        var ledger=RuleEngine.Account(p,new(),a);ledger=RuleEngine.Account(p,ledger,a);
        ledger=RuleEngine.Account(p,ledger,a with {StartUtc=Samples.At(2)+TimeSpan.FromSeconds(5)});
        Assert.AreEqual(TimeSpan.FromSeconds(15).Ticks,ledger.Get(Samples.GroupId,new(2026,11,2)).KnownTicks);
    }
    [TestMethod]
    public void MissingDataIsNotAZeroAllowanceReport() {
        var p=Samples.Policy();var ledger=RuleEngine.Account(p,new(),new(Samples.At(2),TimeSpan.FromSeconds(10),new(),ObservationKnown:false));
        var result=RuleEngine.Evaluate(p,ledger,new("app.video"),new(Samples.At(2,10)));
        Assert.IsTrue(result.Blocked);Assert.IsNull(result.Groups[0].QuotaRemaining);Assert.IsTrue(result.Groups[0].Reasons.Contains(BlockReason.UsageGap));
    }
    [TestMethod]
    public void MultiDayGapMarksEveryCoveredDayButNotExclusiveEnd() {
        var p=Samples.Policy();var ledger=RuleEngine.Account(p,new(),new(Samples.At(1,23),TimeSpan.FromHours(49),new(),ObservationKnown:false));
        foreach(int day in new[]{1,2,3}) Assert.IsTrue(ledger.Get(Samples.GroupId,new(2026,11,day)).HasGap);
        Assert.IsFalse(ledger.Get(Samples.GroupId,new(2026,11,4)).HasGap);
    }
    [TestMethod]
    public void ExactExhaustionAndObservedOveruseAreRetained() {
        var p=Samples.Policy();var ledger=RuleEngine.Account(p,new(),new(Samples.At(2),TimeSpan.FromSeconds(60),new("app.video")));
        Assert.IsTrue(RuleEngine.Evaluate(p,ledger,new("app.video"),new(Samples.At(2,10))).Blocked);
        ledger=RuleEngine.Account(p,ledger,new(Samples.At(2)+TimeSpan.FromSeconds(60),TimeSpan.FromSeconds(3),new("app.video")));
        Assert.AreEqual(TimeSpan.FromSeconds(63).Ticks,ledger.Get(Samples.GroupId,new(2026,11,2)).KnownTicks);
    }
}

[TestClass]
public sealed class ExportTests {
    [TestMethod]
    public void CsvPreservesGapAndQuotesNamesWithoutSpreadsheetFormulaExecution() {
        var p=Samples.Policy(Samples.Quota() with {Name="=danger,\"quoted\""});
        var ledger=new UsageLedger().Put(Samples.GroupId,new(2026,11,2),new(15000000,true));
        string csv=UsageExport.ToCsv(p,ledger);
        StringAssert.Contains(csv,"\"'=danger,\"\"quoted\"\"\"");StringAssert.Contains(csv,",1.5,true\r\n");
    }
    [TestMethod]
    public void JsonExportKeepsFrozenPayloadAndUnknownUsage() {
        var p=Samples.Policy();var state=ChangeQueue.Submit(Samples.Snapshot(p),p,new(Samples.At(2)));
        state=state with {Usage=state.Usage.Put(Samples.GroupId,new(2026,11,2),new(0,true))};
        var roundtrip=PolicyJson.Parse<EngineSnapshot>(UsageExport.ToJson(state));
        Assert.AreEqual(state.Pending,roundtrip.Pending);Assert.IsTrue(roundtrip.Usage.Get(Samples.GroupId,new(2026,11,2)).HasGap);
    }
}

[TestClass]
public sealed class ChangeAndClockTests {
    [TestMethod]
    public void UnverifiedSubmissionCannotBackdateItsWaitingPeriod() {
        var p=Samples.Policy();var state=Samples.Snapshot(p);
        Assert.ThrowsExactly<InvalidOperationException>(()=>ChangeQueue.Submit(state,p,new(Samples.At(2),false)));
        Assert.ThrowsExactly<InvalidOperationException>(()=>ChangeQueue.Submit(state,p,new(Samples.At(1))));
    }
    [TestMethod]
    public void VerifiedClockRecoveryCannotLowerTheHighwater() {
        var state=new RuntimeClock(Samples.At(3),false);
        Assert.IsFalse(state.ReconcileVerifiedAnchor(Samples.At(2)).Trusted);
        Assert.AreEqual(Samples.At(3),state.ReconcileVerifiedAnchor(Samples.At(2)).EffectiveUtc);
        Assert.IsTrue(state.ReconcileVerifiedAnchor(Samples.At(3,1)).Trusted);
    }
    [TestMethod]
    public void MondaySubmissionWaitsUntilTuesday1800() {
        var p=Samples.Policy(Samples.Schedule()) with {ChangeDelay=TimeSpan.FromHours(24)};var target=p with {Groups=[p.Groups[0] with {Enabled=false}]};
        var state=ChangeQueue.Submit(Samples.Snapshot(p),target,new(Samples.At(2,10)));
        Assert.AreEqual(Samples.At(3,10),state.Pending!.NotBeforeUtc);
        Assert.AreEqual(ApplyStatus.WaitingForDelay,ChangeQueue.TryApply(state,new(Samples.At(3,9))).Status);
        Assert.AreEqual(ApplyStatus.Locked,ChangeQueue.TryApply(state,new(Samples.At(3,10))).Status);
        var applied=ChangeQueue.TryApply(state,new(Samples.At(3,18)));Assert.AreEqual(ApplyStatus.Applied,applied.Status);Assert.IsFalse(applied.Snapshot.Policy.Groups[0].Enabled);
    }
    [TestMethod]
    public void PayloadCannotBeSwappedAfterWaiting() {
        var p=Samples.Policy() with {ChangeDelay=TimeSpan.FromHours(24)};var target=p with {Groups=[p.Groups[0] with {Enabled=false}]};
        var state=ChangeQueue.Submit(Samples.Snapshot(p),target,new(Samples.At(2)));
        target=target with {Groups=[target.Groups[0] with {Name="Later edit"}]};
        Assert.AreNotEqual(PolicyJson.Serialize(target),state.Pending!.TargetJson);
        Assert.ThrowsExactly<InvalidDataException>(()=>ChangeQueue.TryApply(state with {Pending=state.Pending with {TargetJson=PolicyJson.Serialize(target)}},new(Samples.At(3))));
    }
    [TestMethod]
    public void CancelKeepsRulesAndResubmissionRestartsWaiting() {
        var p=Samples.Policy() with {ChangeDelay=TimeSpan.FromHours(24)};var state=Samples.Snapshot(p);var queued=ChangeQueue.Submit(state,p,new(Samples.At(2)));
        var canceled=ChangeQueue.Cancel(queued);Assert.IsNull(canceled.Pending);Assert.AreEqual(p,canceled.Policy);
        var next=ChangeQueue.Submit(canceled,p,new(Samples.At(2,10)));Assert.AreEqual(Samples.At(3,10),next.Pending!.NotBeforeUtc);Assert.AreNotEqual(queued.Pending!.Id,next.Pending.Id);
    }
    [TestMethod]
    public void ReducingTheDelayDoesNotShortenItsOwnWait() {
        var p=Samples.Policy() with {ChangeDelay=TimeSpan.FromHours(24)};var state=ChangeQueue.Submit(Samples.Snapshot(p),p with {ChangeDelay=TimeSpan.Zero},new(Samples.At(2)));
        Assert.AreEqual(ApplyStatus.WaitingForDelay,ChangeQueue.TryApply(state,new(Samples.At(2,23))).Status);
    }
    [TestMethod]
    public void ImportDisableAndUninstallShareLockChecks() {
        var p=Samples.Policy(Samples.Schedule());var target=p with {Groups=[p.Groups[0] with {Enabled=false}]};
        foreach(var purpose in Enum.GetValues<ChangePurpose>()) {
            var state=ChangeQueue.Submit(Samples.Snapshot(p),target,new(Samples.At(2,10)),purpose);
            Assert.AreEqual(ApplyStatus.Locked,ChangeQueue.TryApply(state,new(Samples.At(2,10))).Status);
            Assert.AreEqual(purpose==ChangePurpose.Uninstall,ChangeQueue.TryApply(state,new(Samples.At(2,18))).UninstallAuthorized);
        }
    }
    [TestMethod]
    public void StaleBasePolicyIsAConflict() {
        var p=Samples.Policy();var state=ChangeQueue.Submit(Samples.Snapshot(p),p,new(Samples.At(2)));
        state=state with {Policy=p with {Groups=[p.Groups[0] with {Name="Concurrent revision"}]}};
        Assert.AreEqual(ApplyStatus.PolicyConflict,ChangeQueue.TryApply(state,new(Samples.At(2))).Status);
    }
    [TestMethod]
    public void ForwardBackwardAndSmallRepeatedEditsCannotAccelerateTime() {
        var clock=new RuntimeClock(Samples.At(2),true);
        for(int i=0;i<30;i++) clock=clock.Advance(clock.EffectiveUtc+TimeSpan.FromMinutes(1),TimeSpan.FromSeconds(1));
        Assert.AreEqual(Samples.At(2)+TimeSpan.FromSeconds(30),clock.EffectiveUtc);
        var jump=clock.Advance(Samples.At(9),TimeSpan.FromSeconds(1));Assert.IsFalse(jump.Trusted);Assert.AreEqual(clock.EffectiveUtc+TimeSpan.FromSeconds(1),jump.EffectiveUtc);
        Assert.IsFalse(clock.Advance(Samples.At(1),TimeSpan.FromSeconds(1)).Trusted);
        Assert.IsFalse(clock.AfterRestartWithoutTrustedAnchor().Trusted);
    }
    [TestMethod]
    public void UntrustedClockCannotReleaseEvenAReadyChange() {
        var p=Samples.Policy();var state=ChangeQueue.Submit(Samples.Snapshot(p),p,new(Samples.At(2)));
        Assert.AreEqual(ApplyStatus.Locked,ChangeQueue.TryApply(state,new(Samples.At(2,12),false)).Status);
        Assert.AreEqual(ApplyStatus.Locked,ChangeQueue.TryApply(state with {Clock=new(Samples.At(3),true)},new(Samples.At(2,12))).Status);
    }
    [TestMethod]
    public void CalendarChangeDoesNotCreateFreshAllowance() {
        var p=Samples.Policy();var time=Samples.At(3,0,30);var usage=new UsageLedger().Put(Samples.GroupId,new(2026,11,3),new(TimeSpan.FromSeconds(40).Ticks));
        var state=Samples.Snapshot(p) with {Usage=usage};state=ChangeQueue.Submit(state,p with {TimeZoneId="UTC"},new(time));
        var result=ChangeQueue.TryApply(state,new(time));Assert.AreEqual(TimeSpan.FromSeconds(40).Ticks,result.Snapshot.Usage.Get(Samples.GroupId,new(2026,11,2)).KnownTicks);
    }
}
