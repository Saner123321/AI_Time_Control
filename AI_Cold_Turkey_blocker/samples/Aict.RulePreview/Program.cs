using System.Collections.Immutable;
using Aict.Core;

if(args.Length!=1) {Console.Error.WriteLine("Usage: RulePreview OUTPUT_DIRECTORY (simulation only)");return 2;}
var output=Path.GetFullPath(args[0]);
if(Directory.Exists(output)) throw new IOException("Simulation output directory must be new; existing files are preserved");
Directory.CreateDirectory(output);
DateTimeOffset At(int day,int hour) => new DateTimeOffset(2026,11,day,hour,0,0,TimeSpan.FromHours(8)).ToUniversalTime();
var rule=new RuleGroup {Id=Guid.Parse("477dcab7-ed18-4875-b995-8f86697e0d92"),Name="工作日专注",ApplicationMode=TargetMode.BlockSelected,ApplicationIds=["demo.video"],WebsiteMode=TargetMode.BlockSelected,WebsiteDomains=["video.example"],
    Windows=[new(Weekdays.Monday|Weekdays.Tuesday|Weekdays.Wednesday|Weekdays.Thursday|Weekdays.Friday,540,1080)],DailyQuota=TimeSpan.FromMinutes(30),LockWhileBlocking=true};
var policy=new PolicyConfiguration {ChangeDelay=TimeSpan.FromHours(24),Groups=[rule]};
var state=new EngineSnapshot {Policy=policy,Clock=new(At(2,10),true)};
state=ChangeQueue.Submit(state,policy with {Groups=[rule with {Enabled=false}]},new(At(2,10)));
var trace=new[] {
    new {LocalTime="周一 10:00 提交",Status=ChangeQueue.TryApply(state,new(At(2,10))).Status.ToString()},
    new {LocalTime="周二 10:00 等待满24小时",Status=ChangeQueue.TryApply(state,new(At(3,10))).Status.ToString()},
    new {LocalTime="周二 18:00 封锁窗口结束",Status=ChangeQueue.TryApply(state,new(At(3,18))).Status.ToString()}
};
var usage=RuleEngine.Account(policy,new(),new(At(2,19),TimeSpan.FromSeconds(10),new("demo.video","video.example")));
state=state with {Usage=usage};
File.WriteAllText(Path.Combine(output,"combination-lock.json"),PolicyJson.Serialize(new {Kind="SIMULATION_ONLY_NO_SYSTEM_ENFORCEMENT",TimeZone=policy.TimeZoneId,SubmittedUtc=state.Pending!.SubmittedUtc,NotBeforeUtc=state.Pending.NotBeforeUtc,Trace=trace,SharedApplicationWebsiteChargeSeconds=10}));
File.WriteAllText(Path.Combine(output,"usage.csv"),UsageExport.ToCsv(policy,usage));
File.WriteAllText(Path.Combine(output,"state.json"),UsageExport.ToJson(state));
Console.WriteLine($"Simulation artifacts: {output}. No service/policy/network changes.");return 0;
