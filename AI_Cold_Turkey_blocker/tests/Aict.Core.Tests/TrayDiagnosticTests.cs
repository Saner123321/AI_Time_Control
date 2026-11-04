using Aict.Beta;
using Aict.Core;
using Aict.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace Aict.Core.Tests;
[TestClass] public class TrayDiagnosticTests {
    sealed class Clock:IClock {
        public string BootId=>"tray-diagnostics";public long Uptime{get;set;}=100000;public long AwakeMilliseconds=>Uptime;
        public DateTimeOffset UtcNow{get;set;}=Samples.At(2,10);
        public void Add(){Uptime+=1000;UtcNow=UtcNow.AddSeconds(1);}
    }
    static Authority Create(Clock clock){string path=Path.Combine(Environment.GetEnvironmentVariable("AICT_TEST_ROOT")!,Guid.NewGuid().ToString("N"),"state.sqlite");Directory.CreateDirectory(Path.GetDirectoryName(path)!);return new(StateRepository.CreateNew(path,Authority.Initial(clock)),clock,[]);}
    [TestMethod] public async Task PendingIdentityForEmptyApplicationListDoesNotBecomeWebsiteGap(){
        var c=new Clock();using var a=Create(c);var g=Samples.Quota() with{ApplicationIds=[],DailyQuota=TimeSpan.FromMinutes(20)};
        var s=(await a.Handle(new())).Status!;Assert.IsTrue((await a.Handle(new(){Command=Command.SubmitPolicy,EditToken=s.EditToken,Policy=Samples.Policy(g)})).Ok);
        await a.Handle(new(){Command=Command.AgentPulse,Agent=new(null,false,0,"Edge"){IdentityPending=true}},true);
        await a.Handle(new(){Command=Command.BrowserPulse,Browser=new("Edge",new string('a',32),(await a.Desired()).Web.Fingerprint,true,false,"video.example")},true);
        c.Add();s=(await a.Handle(new())).Status!;Assert.AreEqual(0L,s.Usage.Single().GapChargedTicks);Assert.AreEqual(TimeSpan.FromSeconds(1).Ticks,s.Usage.Single().KnownTicks);
    }
    [TestMethod] public void EmptyTargetQuotaIsNotConsideredScopedAndCannotBeEnabled(){
        var g=Samples.Quota() with{ApplicationIds=[],WebsiteDomains=[]};Assert.IsFalse(RuleEngine.HasScopedQuota(g));Assert.ThrowsExactly<ArgumentException>(()=>RuleEngine.Account(Samples.Policy(g),new(),new(Samples.At(2,10),TimeSpan.FromSeconds(10),new(),ObservationKnown:false)));
    }
    [TestMethod] public async Task PendingIdentityWithActualAppTargetsStillChargesUnknownTime(){
        var c=new Clock();using var a=Create(c);var target=new ApplicationTarget(ApplicationTarget.Prefix+new string('A',64),"fixture","fixture.exe",3072);var s=(await a.Handle(new())).Status!;
        Assert.IsTrue((await a.Handle(new(){Command=Command.RegisterApplication,EditToken=s.EditToken,Application=target})).Ok);s=(await a.Handle(new())).Status!;
        var g=Samples.Quota() with{ApplicationIds=[target.Id],WebsiteMode=TargetMode.None,WebsiteDomains=[]};Assert.IsTrue((await a.Handle(new(){Command=Command.SubmitPolicy,EditToken=s.EditToken,Policy=Samples.Policy(g)})).Ok);
        await a.Handle(new(){Command=Command.AgentPulse,Agent=new(null,false,0){IdentityPending=true}},true);c.Add();s=(await a.Handle(new())).Status!;Assert.AreEqual(TimeSpan.FromSeconds(1).Ticks,s.Usage.Single().GapChargedTicks);
    }
    [TestMethod] public async Task SlowSerializationOrDiskWriterCannotBlockDiagnosticProducer(){
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var journal=new DeferredJournal<int>(async _=>{entered.TrySetResult();await release.Task;},8);journal.Report(1);await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try{for(int i=0;i<100;i++)Assert.IsTrue(journal.Report(i));}finally{release.TrySetResult();}await journal.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(3));Assert.AreEqual(0L,journal.FailedWrites);
    }
    [TestMethod] public async Task DiagnosticWriteFailureDoesNotStopLaterEntries(){
        int successes=0;var journal=new DeferredJournal<int>(i=>{if(i==1)throw new IOException("fixture");Interlocked.Increment(ref successes);return Task.CompletedTask;},8);journal.Report(1);journal.Report(2);await journal.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(3));Assert.AreEqual(1L,journal.FailedWrites);Assert.AreEqual(1,successes);
    }
}
