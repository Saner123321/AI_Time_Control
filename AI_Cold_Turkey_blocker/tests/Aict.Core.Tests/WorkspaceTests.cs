using System.Collections.Immutable;
using Aict.Backend;
using Aict.Contracts;
using Aict.Core;
using Aict.Enforcement;
using Aict.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Aict.Core.Tests;

[TestClass]
public sealed class WorkspaceTests {
    sealed class Clock : IWorkspaceClock {
        public string BootId {get;set;}="boot-one";public long UptimeMilliseconds {get;set;}=120000;
        public DateTimeOffset UtcNow {get;set;}=new(2026,11,3,1,0,0,TimeSpan.Zero);
        public void Add(int seconds) {UptimeMilliseconds+=seconds*1000;UtcNow+=TimeSpan.FromSeconds(seconds);}
    }
    static readonly ApplicationTarget App=new(ApplicationTarget.Prefix+new string('A',64),"测试应用","Test.exe",4096);
    static (StateRepository Store,Clock Time) Setup() {
        var time=new Clock();string dir=Path.Combine(Environment.GetEnvironmentVariable("AICT_TEST_ROOT")!,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        return(StateRepository.CreateNew(Path.Combine(dir,"workspace.sqlite"),WorkspaceAuthority.Initial(time)),time);
    }
    static async Task<PreviewStatus> Status(WorkspaceAuthority authority)=>(await authority.HandleAsync(new())).Status!;
    static async Task Register(WorkspaceAuthority authority,ApplicationTarget? app=null) {
        var result=await authority.HandleAsync(new() {Command=PreviewCommand.RegisterApplication,EditToken=(await Status(authority)).EditToken,Application=app ?? App});Assert.IsTrue(result.Ok,result.Message);
    }
    static PolicyConfiguration Policy(PreviewStatus state,bool locked=false)=>state.Policy with {ChangeDelay=TimeSpan.FromSeconds(30),Groups=[new() {Id=Guid.NewGuid(),Name="专注",ApplicationMode=TargetMode.BlockSelected,ApplicationIds=[App.Id],Cycle=new(state.EffectiveUtc,TimeSpan.FromSeconds(60),TimeSpan.FromSeconds(60)),LockWhileBlocking=locked}]};
    [TestMethod]
    public async Task UIOnlySubmitsPolicyAndCannotReportSavedAsExecuted() {
        var (store,time)=Setup();using var authority=new WorkspaceAuthority(store,time,[]);await Register(authority);
        var before=await Status(authority);var response=await authority.HandleAsync(new() {Command=PreviewCommand.SubmitPolicy,EditToken=before.EditToken,Policy=Policy(before)});
        Assert.AreEqual("SAVED_EXECUTION_PENDING",response.Code);Assert.IsFalse(response.Status!.Execution!.Verified);Assert.IsFalse(response.Status.SystemEnforcementActive);
        var plan=(await authority.DesiredAsync()).Plan;await authority.ObserveAsync(plan.Fingerprint,plan.DeniedIdentities,time.UtcNow,null);
        Assert.IsTrue((await Status(authority)).SystemEnforcementActive);
        await authority.ObserveAsync(plan.Fingerprint,[],time.UtcNow,"native error");Assert.IsFalse((await Status(authority)).SystemEnforcementActive);
    }
    [TestMethod]
    public async Task SameBootCrashKeepsDelayAndAdvancesTimeInsteadOfResettingWait() {
        var (store,time)=Setup();Guid pending;DateTimeOffset due;
        using(var authority=new WorkspaceAuthority(store,time,[])) {
            await Register(authority);var s=await Status(authority);var p=Policy(s);Assert.IsTrue((await authority.HandleAsync(new() {Command=PreviewCommand.SubmitPolicy,EditToken=s.EditToken,Policy=p})).Ok);
            s=await Status(authority);var response=await authority.HandleAsync(new() {Command=PreviewCommand.SubmitPolicy,EditToken=s.EditToken,Policy=p with {Groups=[]}});
            pending=response.Status!.Pending!.Id;due=response.Status.Pending.NotBeforeUtc;time.Add(10);await authority.TickAsync();
        }
        time.Add(5);using var recovered=new WorkspaceAuthority(StateRepository.OpenExisting(store.Path),time,[]);var status=await Status(recovered);
        Assert.IsTrue(status.ClockConsistent);Assert.AreEqual(pending,status.Pending!.Id);Assert.AreEqual(due,status.Pending.NotBeforeUtc);
        time.Add(15);await recovered.TickAsync();Assert.IsNull((await Status(recovered)).Pending);
    }
    [TestMethod]
    public async Task DifferentBootDoesNotTurnStoredTrustedFlagIntoAuthority() {
        var (store,time)=Setup();time.BootId="different-boot";using var authority=new WorkspaceAuthority(store,time,[]);
        Assert.IsFalse((await Status(authority)).ClockConsistent);
        var response=await authority.HandleAsync(new() {Command=PreviewCommand.RegisterApplication,EditToken=(await Status(authority)).EditToken,Application=App});Assert.IsFalse(response.Ok);
    }
    [TestMethod]
    public async Task ActualLockPreventsRegistrationAndDefersDisableBeyondTheDelay() {
        var (store,time)=Setup();using var authority=new WorkspaceAuthority(store,time,[]);await Register(authority);var s=await Status(authority);var p=Policy(s,true);
        Assert.IsTrue((await authority.HandleAsync(new() {Command=PreviewCommand.SubmitPolicy,EditToken=s.EditToken,Policy=p})).Ok);
        var register=await authority.HandleAsync(new() {Command=PreviewCommand.RegisterApplication,EditToken=(await Status(authority)).EditToken,Application=App with {Id=ApplicationTarget.Prefix+new string('B',64)}});Assert.AreEqual("LOCKED",register.Code);
        var disable=await authority.HandleAsync(new() {Command=PreviewCommand.SubmitPolicy,EditToken=(await Status(authority)).EditToken,Policy=p with {Groups=[]}});Assert.IsTrue(disable.Ok);
        time.Add(30);await authority.TickAsync();Assert.AreEqual(ApplyStatus.Locked,(await Status(authority)).PendingStatus);
        time.Add(30);await authority.TickAsync();Assert.IsTrue((await Status(authority)).Policy.Groups.IsEmpty);
    }
    [TestMethod]
    public async Task EssentialAppsAndUnknownIDsAreRejectedBeforeNativeWork() {
        var (store,time)=Setup();using var authority=new WorkspaceAuthority(store,time,[App.Id]);
        Assert.AreEqual("ESSENTIAL_APP",(await authority.HandleAsync(new() {Command=PreviewCommand.RegisterApplication,EditToken=(await Status(authority)).EditToken,Application=App})).Code);
        var s=await Status(authority);Assert.IsFalse((await authority.HandleAsync(new() {Command=PreviewCommand.SubmitPolicy,EditToken=s.EditToken,Policy=Policy(s)})).Ok);
    }
    [TestMethod]
    public async Task FailedSaveDoesNotAcknowledgeAndStaleCatalogDraftCannotOverwrite() {
        var (store,time)=Setup();using var authority=new WorkspaceAuthority(store,time,[]);var old=await Status(authority);await Register(authority);
        Assert.AreEqual("STALE_EDIT",(await authority.HandleAsync(new() {Command=PreviewCommand.SubmitPolicy,EditToken=old.EditToken,Policy=Policy(old)})).Code);
        var cached=store.Read();Assert.IsTrue(store.TryCommit(cached.Revision,cached.Snapshot));var s=await Status(authority);
        Assert.AreEqual("STORAGE_FAULT",(await authority.HandleAsync(new() {Command=PreviewCommand.SubmitPolicy,EditToken=s.EditToken,Policy=Policy(s)})).Code);
        Assert.IsFalse((await Status(authority)).StorageHealthy);
    }
    [TestMethod]
    public async Task UnsupportedBrowserQuotaAndWhitelistDoNotSilentlyBecomeApplicationOnly() {
        var (store,time)=Setup();using var authority=new WorkspaceAuthority(store,time,[]);await Register(authority);var s=await Status(authority);var p=Policy(s);
        foreach(var g in new[]{p.Groups[0] with {WebsiteMode=TargetMode.BlockSelected,WebsiteDomains=["example.com"]},p.Groups[0] with {DailyQuota=TimeSpan.FromMinutes(1)},p.Groups[0] with {ApplicationMode=TargetMode.AllowSelected}})
            Assert.IsFalse((await authority.HandleAsync(new() {Command=PreviewCommand.SubmitPolicy,EditToken=s.EditToken,Policy=p with {Groups=[g]}})).Ok);
    }
    [TestMethod]
    public void MultiTargetOwnershipRejectsExtraAllowRulesChangedSIDAndDTDs() {
        string run=Guid.NewGuid().ToString("N"),sid="S-1-5-21-1-2-3-1001";
        var apps=new[]{new PolicyTarget {Id=App.Id,Name=App.Name,FileName=App.FileName,Size=App.Size},new PolicyTarget {Id=ApplicationTarget.Prefix+new string('B',64),Name="第二个",FileName="Renamed.exe",Size=4096}};
        string xml=InteractivePolicyXml.Exe(run,sid,apps);Assert.AreEqual(2,InteractivePolicyXml.ReadOwned(xml,run,out string actual).Length);Assert.AreEqual(sid,actual);
        Assert.ThrowsExactly<InvalidOperationException>(()=>InteractivePolicyXml.ReadOwned(xml.Replace("Action=\"Deny\"","Action=\"Allow\""),run,out _));
        Assert.ThrowsExactly<InvalidOperationException>(()=>InteractivePolicyXml.ReadOwned(xml,Guid.NewGuid().ToString("N"),out _));
        Assert.ThrowsExactly<ArgumentException>(()=>(App with {FileName="../secret.exe"}).Validate());
        Assert.ThrowsExactly<System.Text.Json.JsonException>(()=>PolicyJson.Parse<PreviewRequest>("{\"command\":\"Status\",\"databasePath\":\"secret\"}"));
    }
}
