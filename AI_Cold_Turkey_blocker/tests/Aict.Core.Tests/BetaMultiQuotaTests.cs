using Aict.Core;
using Aict.Beta;
using Aict.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace Aict.Core.Tests;
[TestClass] public class BetaMultiQuotaTests {
    sealed class Clock:IClock {public string BootId=>"multi";public long Uptime{get;set;}public long AwakeMilliseconds{get;set;}public DateTimeOffset UtcNow{get;set;}=Samples.At(2,9);public void Add(int ms,bool sleeping=false){Uptime+=ms;UtcNow=UtcNow.AddMilliseconds(ms);if(!sleeping)AwakeMilliseconds+=ms;}}
    [TestMethod] public async Task BackendReportsBothSlotsAndOnlyExhaustsTheActiveOneAcrossRestart(){
        var c=new Clock();var morning=new QuotaSlot(Guid.NewGuid(),540,720,TimeSpan.FromMinutes(1),"上午");var evening=new QuotaSlot(Guid.NewGuid(),1080,1320,TimeSpan.FromMinutes(2),"晚上");var g=Samples.Quota() with{ApplicationMode=TargetMode.None,ApplicationIds=[],DailyQuota=null,QuotaSlots=[morning,evening]};
        string folder=Path.Combine(Environment.GetEnvironmentVariable("AICT_TEST_ROOT")??Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);string db=Path.Combine(folder,"state.sqlite");var a=new Authority(StateRepository.CreateNew(db,Authority.Initial(c)),c,[]);var s=(await a.Handle(new())).Status!;Assert.IsTrue((await a.Handle(new(){Command=Command.SubmitPolicy,EditToken=s.EditToken,Policy=Samples.Policy(g)})).Ok);
        async Task Pulse(){await a.Handle(new(){Command=Command.AgentPulse,Agent=new(null,false,0,"Edge")},true);await a.Handle(new(){Command=Command.BrowserPulse,Browser=new("Edge",new string('1',32),(await a.Desired()).Web.Fingerprint,true,false,"video.example")},true);}
        await Pulse();for(int i=0;i<60;i++){c.Add(1000);await Pulse();}s=(await a.Handle(new())).Status!;Assert.AreEqual(2,s.Usage.Length);Assert.AreEqual(TimeSpan.FromMinutes(1).Ticks,s.Usage.Single(u=>u.QuotaSlotId==morning.Id).UsedTicks);Assert.AreEqual(0L,s.Usage.Single(u=>u.QuotaSlotId==evening.Id).UsedTicks);Assert.AreEqual(1,(await a.Desired()).Web.Groups.Length);
        c.Add((int)(Samples.At(2,18)-c.UtcNow).TotalMilliseconds,true);await Pulse();Assert.IsTrue((await a.Desired()).Web.Groups.IsEmpty);for(int i=0;i<2;i++){c.Add(1000);await Pulse();}a.Dispose();using var restored=new Authority(StateRepository.OpenExisting(db),c,[]);s=(await restored.Handle(new())).Status!;Assert.AreEqual(TimeSpan.FromMinutes(1).Ticks,s.Usage.Single(u=>u.QuotaSlotId==morning.Id).UsedTicks);Assert.AreEqual(TimeSpan.FromSeconds(2).Ticks,s.Usage.Single(u=>u.QuotaSlotId==evening.Id).UsedTicks);Assert.IsTrue(s.Usage.Single(u=>u.QuotaSlotId==evening.Id).QuotaWindowActive);
    }
}
