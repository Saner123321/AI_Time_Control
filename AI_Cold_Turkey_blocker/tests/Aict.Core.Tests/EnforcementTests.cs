using System.Collections.Immutable;
using Aict.Core;
using Aict.Enforcement;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Aict.Core.Tests;

[TestClass]
public sealed class EnforcementTests {
    static readonly string Id=ApplicationPlanner.HashIdentity(new string('A',64));
    static EngineSnapshot State(RuleGroup? group=null) => new() {Policy=new() {TimeZoneId="UTC",Groups=[group ?? new() {Id=Guid.NewGuid(),Name="应用",ApplicationMode=TargetMode.BlockSelected,ApplicationIds=[Id],AlwaysBlock=true}]},Clock=new(new DateTimeOffset(2026,11,2,9,0,0,TimeSpan.Zero),true)};
    [TestMethod]
    public void BoundaryChangesDesiredHashPlanButIsNotAnExecutionProof() {
        var state=State();var group=state.Policy.Groups[0] with {AlwaysBlock=false,Cycle=new(state.Clock.EffectiveUtc+TimeSpan.FromSeconds(15),TimeSpan.FromMinutes(10),TimeSpan.FromMinutes(1))};state=state with {Policy=state.Policy with {Groups=[group]}};
        Assert.IsTrue(ApplicationPlanner.Compile(state).DeniedIdentities.IsEmpty);
        var at=state with {Clock=state.Clock with {EffectiveUtc=state.Clock.EffectiveUtc+TimeSpan.FromSeconds(15)}};CollectionAssert.AreEqual(new[]{Id},ApplicationPlanner.Compile(at).DeniedIdentities.ToArray());
    }
    [TestMethod]
    public void GroupExceptionDoesNotOverrideAnotherGroupAndDuplicatesAreCanonicalized() {
        var state=State();var original=state.Policy.Groups[0];
        state=state with {Policy=state.Policy with {Groups=[original with {ApplicationExceptions=[Id]},original with {Id=Guid.NewGuid(),ApplicationIds=[Id.ToLowerInvariant(),Id]}]}};
        Assert.AreEqual(Id,ApplicationPlanner.Compile(state).DeniedIdentities.Single());
    }
    [TestMethod]
    public void RestartAndMissingCollectorRemainConservative() {
        var state=State();state=state with {Policy=state.Policy with {Groups=[state.Policy.Groups[0] with {AlwaysBlock=false,DailyQuota=TimeSpan.FromHours(1)}]}};
        Assert.IsTrue(ApplicationPlanner.Compile(state,true).DeniedIdentities.IsEmpty);Assert.AreEqual(Id,ApplicationPlanner.Compile(state,false).DeniedIdentities.Single());
        state=state with {Clock=state.Clock.AfterRestartWithoutTrustedAnchor()};Assert.AreEqual(Id,ApplicationPlanner.Compile(state,true).DeniedIdentities.Single());
    }
    [TestMethod]
    public void UnsupportedWebsiteAllowlistAndProcessNamesAreRefusedEvenIfDisabled() {
        var state=State();var group=state.Policy.Groups[0];
        foreach(var unsupported in new[]{group with {Enabled=false,WebsiteMode=TargetMode.BlockSelected,WebsiteDomains=["example.com"]},group with {ApplicationMode=TargetMode.AllowSelected},group with {ApplicationIds=["notepad.exe"]}})
            Assert.ThrowsExactly<NotSupportedException>(()=>ApplicationPlanner.Compile(state with {Policy=state.Policy with {Groups=[unsupported]}}));
    }
    [TestMethod]
    public void PolicyXmlBindsRunSidAndSIPHashAndRejectsForeignOrModifiedShape() {
        string run="12345678123412341234123456789abc",sid="S-1-5-21-1-2-3-1001",hash="0x"+new string('A',64),xml=AppLockerXml.Exe(run,sid,hash,3072);
        Assert.IsTrue(AppLockerXml.Equivalent(System.Net.WebUtility.HtmlEncode(xml),xml));
        Assert.IsFalse(AppLockerXml.Equivalent(xml.Replace("Action=\"Deny\"","Action=\"Allow\""),xml));
        Assert.IsFalse(AppLockerXml.Equivalent(AppLockerXml.Exe(run,"S-1-5-21-1-2-3-1002",hash,3072),xml));
        Assert.IsFalse(AppLockerXml.Equivalent(AppLockerXml.Exe(Guid.NewGuid().ToString("N"),sid,hash,3072),xml));
        Assert.IsFalse(AppLockerXml.Equivalent(xml.Replace(hash,"0x"+new string('B',64)),xml));
        StringAssert.Contains(xml,"UserOrGroupSid=\""+sid+"\"");StringAssert.Contains(xml,"UserOrGroupSid=\"S-1-1-0\"");
        Assert.ThrowsExactly<ArgumentException>(()=>AppLockerXml.Exe(run,"S-1-1-0",hash,3072));
        Assert.ThrowsExactly<System.Xml.XmlException>(()=>AppLockerXml.Parse("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///secret'>]><x>&e;</x>"));
    }
    sealed class Adapter : IApplicationPolicyAdapter {
        public PolicyObservation Current=new(false,false,false);
        public bool RefuseRead,RefuseWrite,WrongReadback;
        public int Writes;
        public Task<PolicyObservation> ReadAsync(CancellationToken cancellation) => RefuseRead?throw new InvalidDataException("foreign ownership conflict"):Task.FromResult(Current);
        public Task<PolicyObservation> ApplyAsync(ApplicationPlan plan,CancellationToken cancellation) {
            Writes++;if(RefuseWrite) throw new IOException("apply failed");
            bool blocked=!plan.DeniedIdentities.IsEmpty;
            Current=WrongReadback?new(false,false,false):new(blocked,blocked,blocked);return Task.FromResult(Current);
        }
    }
    [TestMethod]
    public async Task AdapterFailureDoesNotTurnDesiredStateIntoVerifiedEnforcement() {
        var adapter=new Adapter {RefuseWrite=true};var reconciler=new ApplicationReconciler(adapter);var plan=ApplicationPlanner.Compile(State());
        Assert.IsFalse(await reconciler.ReconcileAsync(plan));Assert.IsNull(reconciler.LastVerifiedFingerprint);Assert.IsNull(reconciler.LastVerifiedUtc);Assert.IsNotNull(reconciler.Fault);
        adapter.RefuseWrite=false;Assert.IsTrue(await reconciler.ReconcileAsync(plan));Assert.AreEqual(plan.Fingerprint,reconciler.LastVerifiedFingerprint);
        var previous=reconciler.LastVerifiedUtc;adapter.RefuseRead=true;Assert.IsFalse(await reconciler.ReconcileAsync(plan));Assert.AreEqual(previous,reconciler.LastVerifiedUtc);Assert.AreEqual(2,adapter.Writes);
    }
    [TestMethod]
    public async Task ForeignConflictIsNotOverwrittenAndBadReadbackNeverAcknowledged() {
        var adapter=new Adapter {RefuseRead=true};var reconciler=new ApplicationReconciler(adapter);var plan=ApplicationPlanner.Compile(State());
        Assert.IsFalse(await reconciler.ReconcileAsync(plan));Assert.AreEqual(0,adapter.Writes);
        adapter.RefuseRead=false;adapter.WrongReadback=true;Assert.IsFalse(await reconciler.ReconcileAsync(plan));Assert.IsNull(reconciler.LastVerifiedFingerprint);
    }
    [TestMethod]
    public async Task ObservedLossReappliesButUnchangedVerifiedPlanDoesNotRewrite() {
        var adapter=new Adapter();var reconciler=new ApplicationReconciler(adapter);var plan=ApplicationPlanner.Compile(State());
        Assert.IsTrue(await reconciler.ReconcileAsync(plan));Assert.IsTrue(await reconciler.ReconcileAsync(plan));Assert.AreEqual(1,adapter.Writes);
        adapter.Current=new(false,false,false);Assert.IsTrue(await reconciler.ReconcileAsync(plan));Assert.AreEqual(2,adapter.Writes);
    }
}
