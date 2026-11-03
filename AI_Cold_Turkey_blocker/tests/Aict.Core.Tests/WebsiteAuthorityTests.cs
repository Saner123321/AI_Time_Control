using Aict.Core;
using Aict.Beta;
using Aict.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace Aict.Core.Tests;
[TestClass] public class WebsiteAuthorityTests {
    sealed class Clock:IClock {public string BootId=>"test";public long Uptime{get;set;} public long AwakeMilliseconds=>Uptime;public DateTimeOffset UtcNow=>DateTimeOffset.Parse("2026-11-03T12:00:00Z").AddMilliseconds(Uptime);}
    [TestMethod] public async Task OldComponentsCannotAcknowledgePagePlansAndUsageHonorsPageExceptions(){
        string folder=Path.Combine(Environment.GetEnvironmentVariable("AICT_TEST_ROOT")??Path.GetTempPath(),"url-authority-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);var c=new Clock();
        using var a=new Authority(StateRepository.CreateNew(Path.Combine(folder,"engine.sqlite"),Authority.Initial(c)),c,[]);
        var g=new RuleGroup{Id=Guid.NewGuid(),Name="页面额度",WebsiteMode=TargetMode.BlockSelected,WebsiteDomains=["*bilibili*"],WebsiteExceptions=["bilibili.com/video/BV1ABC"],DailyQuota=TimeSpan.FromMinutes(1)};
        var s=(await a.Handle(new())).Status!;Assert.IsTrue((await a.Handle(new(){Command=Command.SubmitPolicy,EditToken=s.EditToken,Policy=new(){ChangeDelay=TimeSpan.Zero,Groups=[g]}})).Ok);
        await a.Handle(new(){Command=Command.AgentPulse,Agent=new(null,false,0,"Edge")},true);
        string hash=(await a.Desired()).Web.Fingerprint;var b=new BrowserSample("Edge",new string('1',32),hash,true,false,"bilibili.com"){Url="https://bilibili.com/video/BV1ABC"};
        await a.Handle(new(){Command=Command.BrowserPulse,Browser=b},true);Assert.IsFalse((await a.Desired()).BrowserHealthy["Edge"]);
        await a.Handle(new(){Command=Command.BrowserPulse,Browser=b with{PatternVersion=1}},true);Assert.IsTrue((await a.Desired()).BrowserHealthy["Edge"]);
        c.Uptime+=1000;Assert.AreEqual(0L,(await a.Handle(new())).Status!.Usage.Single().UsedTicks);
        await a.Handle(new(){Command=Command.BrowserPulse,Browser=b with{PatternVersion=1,Url="https://bilibili.com/video/BV1XYZ"}},true);
        c.Uptime+=1000;Assert.AreEqual(TimeSpan.FromSeconds(1).Ticks,(await a.Handle(new())).Status!.Usage.Single().UsedTicks);
    }
}
