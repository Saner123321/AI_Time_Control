using Aict.Beta;
using Aict.Core;
using Aict.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace Aict.Core.Tests;
[TestClass] public class GapChargeTests {
    [TestMethod] public void ShortGapChargesDurationThenLeavesRemainingBudgetUsable(){
        var p=Samples.Policy();var t=Samples.At(2,10);var l=RuleEngine.Account(p,new(),new(t,TimeSpan.FromSeconds(5),new(),ObservationKnown:false));
        var a=l.Get(Samples.GroupId,new(2026,11,2));Assert.AreEqual(0L,a.KnownTicks);Assert.AreEqual(TimeSpan.FromSeconds(5).Ticks,a.GapChargedTicks);Assert.IsTrue(a.HasGap&&a.GapChargeComplete);
        var e=RuleEngine.Evaluate(p,l,new("app.video"),new(t.AddSeconds(5)),false);Assert.IsFalse(e.Blocked);Assert.AreEqual(TimeSpan.FromSeconds(55),e.Groups.Single().QuotaRemaining);
        l=RuleEngine.Account(p,l,new(t.AddSeconds(5),TimeSpan.FromSeconds(55),new("app.video","video.example")));e=RuleEngine.Evaluate(p,l,new("app.video"),new(t.AddMinutes(1)));Assert.IsTrue(e.Blocked);CollectionAssert.Contains(e.Groups.Single().Reasons.ToArray(),BlockReason.QuotaExhausted);Assert.IsFalse(e.Groups.Single().Reasons.Contains(BlockReason.UsageGap));
    }
    [TestMethod] public void GapReplayAndOverlapCannotChargeTheSameTimeTwice(){
        var p=Samples.Policy();var s=new UsageSlice(Samples.At(2,10),TimeSpan.FromSeconds(10),new(),ObservationKnown:false);var l=RuleEngine.Account(p,new(),s);l=RuleEngine.Account(p,l,s);l=RuleEngine.Account(p,l,s with{StartUtc=s.StartUtc.AddSeconds(5)});Assert.AreEqual(TimeSpan.FromSeconds(15).Ticks,l.Get(Samples.GroupId,new(2026,11,2)).GapChargedTicks);
    }
    [TestMethod] public void GapSplitsAtAdjacentSlotsAndMidnightWithoutChargingUnrelatedPeriods(){
        var a=new QuotaSlot(Guid.NewGuid(),540,600,TimeSpan.FromMinutes(20));var b=new QuotaSlot(Guid.NewGuid(),600,660,TimeSpan.FromMinutes(20));var g=Samples.Quota() with{DailyQuota=null,QuotaSlots=[a,b]};var p=Samples.Policy(g);
        var l=RuleEngine.Account(p,new(),new(Samples.At(2,9,59,30),TimeSpan.FromMinutes(1),new(),ObservationKnown:false));Assert.AreEqual(TimeSpan.FromSeconds(30).Ticks,l.Get(g.Id,new(2026,11,2),a.Id).GapChargedTicks);Assert.AreEqual(TimeSpan.FromSeconds(30).Ticks,l.Get(g.Id,new(2026,11,2),b.Id).GapChargedTicks);Assert.AreEqual(2,l.Accounts.Count);
        l=RuleEngine.Account(Samples.Policy(),new(),new(Samples.At(2,23,59,50),TimeSpan.FromSeconds(30),new(),ObservationKnown:false));Assert.AreEqual(TimeSpan.FromSeconds(10).Ticks,l.Get(g.Id,new(2026,11,2)).GapChargedTicks);Assert.AreEqual(TimeSpan.FromSeconds(20).Ticks,l.Get(g.Id,new(2026,11,3)).GapChargedTicks);
    }
    [TestMethod] public void ConfirmedIdleLockAndSleepAreNotGapCharges(){
        foreach(var s in new[]{new UsageSlice(Samples.At(2,10),TimeSpan.FromSeconds(10),new("app.video"),SessionLocked:true),new(Samples.At(2,10),TimeSpan.FromSeconds(10),new("app.video"),Suspended:true),new(Samples.At(2,10),TimeSpan.FromSeconds(10),new("app.video"),IdleAtStart:TimeSpan.FromMinutes(1))})Assert.AreEqual(0,RuleEngine.Account(Samples.Policy(),new(),s).Accounts.Count);
    }
    [TestMethod] public void HardScheduleStillBlocksWithAChargedGapAndUnusedQuota(){
        var g=Samples.Quota() with{Windows=[new(Weekdays.All,540,720)]};var p=Samples.Policy(g);var l=RuleEngine.Account(p,new(),new(Samples.At(2,10),TimeSpan.FromSeconds(5),new(),ObservationKnown:false));Assert.IsTrue(RuleEngine.Evaluate(p,l,new("app.video"),new(Samples.At(2,10,1))).Blocked);
    }
    [TestMethod] public void LegacyMigrationIsExplicitEstimatedIdempotentAndPreservesMeasuredUse(){
        var p=Samples.Policy();var l=new UsageLedger().Put(Samples.GroupId,new(2026,11,2),new(TimeSpan.FromSeconds(12).Ticks,true));ActivityCounter[] rows=[new("2026-11-02","Asia/Shanghai","gap","unknown","统计缺口",0,TimeSpan.FromSeconds(5).Ticks)];var n=GapAccounting.MigrateLegacy(p,l,rows);var a=n.Get(Samples.GroupId,new(2026,11,2));Assert.AreEqual(TimeSpan.FromSeconds(12).Ticks,a.KnownTicks);Assert.AreEqual(TimeSpan.FromSeconds(17).Ticks,a.ChargedTicks);Assert.IsTrue(a.GapEstimated&&a.GapChargeComplete&&a.HasGap);Assert.AreEqual(n,GapAccounting.MigrateLegacy(p,n,rows));Assert.IsFalse(RuleEngine.Evaluate(p,n,new("app.video"),new(Samples.At(2,10))).Blocked);
        var missing=GapAccounting.MigrateLegacy(p,l,[]).Get(Samples.GroupId,new(2026,11,2));Assert.AreEqual(TimeSpan.FromMinutes(1).Ticks,missing.GapChargedTicks);Assert.IsTrue(missing.GapEstimated);
    }
    [TestMethod] public void EditsStorageAndCsvRetainMeasuredAndGapComponents(){
        var g=Samples.Quota();var p=Samples.Policy(g);var a=new DailyUsage(TimeSpan.FromSeconds(10).Ticks,true){GapChargedTicks=TimeSpan.FromSeconds(5).Ticks,GapChargeComplete=true};var l=new UsageLedger().Put(g.Id,new(2026,11,2),a);var next=g with{DailyQuotaWindow=new(540,720)};var moved=QuotaPeriods.CarryForward(p,Samples.Policy(next),l,Samples.At(2,10));Assert.AreEqual(a,moved.Get(g.Id,new(2026,11,2)));
        string path=Path.Combine(Environment.GetEnvironmentVariable("AICT_TEST_ROOT")!,Guid.NewGuid().ToString("N"),"state.sqlite");Directory.CreateDirectory(Path.GetDirectoryName(path)!);StateRepository.CreateNew(path,new(){Policy=p,Clock=new(Samples.At(2,10),true),Usage=l});var restored=StateRepository.OpenExisting(path).Read().Snapshot;Assert.AreEqual(a,restored.Usage.Get(g.Id,new(2026,11,2)));string csv=UsageExport.ToCsv(p,restored.Usage);StringAssert.Contains(csv,"gap_charged_seconds,total_charged_seconds");StringAssert.Contains(csv,",10,true,5,15,false,true");
    }
    [TestMethod] public async Task SlowIdentityWorkCannotBlockHeartbeatCallerOrReuseAnotherAppsIdentity(){
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();using var probe=new BackgroundIdentityProbe((key,stage)=>{stage("hash");entered.Set();release.Wait(TimeSpan.FromSeconds(5));return key;});
        try{Assert.IsTrue(probe.Sample("first").Pending);Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(2)));for(int i=0;i<100;i++)Assert.IsTrue(probe.Sample("second").Pending);Assert.AreEqual("hash",probe.Sample("first").Stage);}finally{release.Set();}
        for(int i=0;i<100&&probe.Sample("first").Pending;i++)await Task.Delay(10);Assert.AreEqual("first",probe.Sample("first").Id);Assert.IsNull(probe.Sample("second").Id);
    }
    [TestMethod] public async Task IdentityProbeFailureIsObservableAndDoesNotCrashSampling(){
        var done=new TaskCompletionSource<IdentityProbeObservation>(TaskCreationOptions.RunContinuationsAsynchronously);using var probe=new BackgroundIdentityProbe((key,stage)=>throw new IOException("fixture"),e=>done.TrySetResult(e));Assert.IsTrue(probe.Sample("app").Pending);var o=await done.Task.WaitAsync(TimeSpan.FromSeconds(3));Assert.AreEqual(nameof(IOException),o.ErrorType);Assert.IsTrue(probe.Sample("app").Pending);
    }
}
