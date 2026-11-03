using System.ServiceProcess;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Aict.Core;
using Aict.Storage;
using Aict.Beta.Service;

try {
    if(args.SequenceEqual(new[]{"--build-info"})){Console.WriteLine(PolicyJson.Serialize(new{hostTrial=BuildMode.HostTrial,trialMinutes=BuildMode.TrialMinutes}));return 0;}
    if(args.SequenceEqual(new[]{"--offline-check"})) {if(Marshal.SizeOf<Aict.Beta.Service.Wfp.Filter>()!=200 || Marshal.SizeOf<Aict.Beta.Service.Wfp.Condition>()!=40)throw new InvalidOperationException("WFP ABI mismatch");UserScope.ValidateOffline();Console.WriteLine("WFP x64 layout and ALE user SD type 14 validated using Windows descriptor validation; no filtering engine opened");return 0;}
    if(args.Length==0)return 2;
    bool installer=args[0] is "--import-v1" or "--request-recovery" or "--check-state" or "--check-reinstall";Env.Guard(installer);
    if(args.SequenceEqual(new[]{"--vm-beta"})){ServiceBase.Run(new BetaService());return 0;}
    if(args.SequenceEqual(new[]{"--request-recovery"})){BetaService.RunMaintenance(true);return 0;}
    if(args.SequenceEqual(new[]{"--host-trial-safety"})){
        if(!BuildMode.HostTrial || !Env.Config.HostTrial || Env.Config.HostTrialEndsUtc is not {} end || DateTimeOffset.UtcNow<end)throw new UnauthorizedAccessException("Host trial safety deadline not reached");
        bool released=false;try{string file=Path.Combine(Env.Root,"state","status.json");var status=PolicyJson.Parse<Aict.Beta.Response>(File.ReadAllText(file)).Status;using var service=new ServiceController(Env.Name);released=!Aict.Beta.HostTrialSafety.NeedsIndependentCleanup(status,DateTimeOffset.UtcNow,File.GetLastWriteTimeUtc(file),service.Status==ServiceControllerStatus.Running);}catch(Exception e){Env.Log("TrialSafetyStatusUnavailable",new {error=e.Message});}
        if(released){Env.Save(Path.Combine(Env.Root,"state","trial-safety.json"),new{completed=true,independentCleanupRequired=false,atUtc=DateTimeOffset.UtcNow});return 0;}
        args=["--beta-cleanup-force"];
    }
    if(args.SequenceEqual(new[]{"--check-state"})){var s=StateRepository.OpenExisting(Path.Combine(Env.Root,"state","engine.sqlite")).Read().Snapshot;if(s.UninstallReady)throw new InvalidOperationException("此安装已获准卸载，保留的数据不会被自动清空或重新启用。");Console.WriteLine("Existing state preserved");return 0;}
    if(args.SequenceEqual(new[]{"--check-reinstall"})){var s=StateRepository.OpenExisting(Path.Combine(Env.Root,"state","engine.sqlite")).Read().Snapshot;var cleanup=PolicyJson.Parse<System.Text.Json.JsonElement>(File.ReadAllText(Path.Combine(Env.Root,"state","cleanup-result.json")));if(!cleanup.GetProperty("completed").GetBoolean() || (!s.UninstallReady && !cleanup.GetProperty("force").GetBoolean()))throw new UnauthorizedAccessException("原安装尚未获准解除，不能新建空白会话。");try{using var current=new ServiceController(Env.Name);_ = current.Status;throw new InvalidOperationException("原服务仍存在，不能并行重装。");}catch(InvalidOperationException e)when(e.InnerException is System.ComponentModel.Win32Exception w && w.NativeErrorCode==1060){}return 0;}
    if(args.Length==2 && args[0]=="--import-v1") {
        if(!Guid.TryParseExact(args[1],"N",out _))throw new ArgumentException("Invalid source session");
        string src=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"AICT-Interactive-"+args[1]);
        var dir=new DirectoryInfo(src);if(!dir.Exists || (dir.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Invalid legacy root");Env.CheckAcl(dir.GetAccessControl());
        var config=PolicyJson.Parse<System.Text.Json.JsonElement>(File.ReadAllText(Path.Combine(src,"config.json")));if(config.GetProperty("probeSid").GetString()!=Env.Config.ProbeSid)throw new UnauthorizedAccessException("Legacy user mismatch");
        try{using var old=new ServiceController("AICT-Interactive-"+args[1]);_ = old.Status;throw new InvalidOperationException("旧实验服务仍存在，拒绝并行覆盖。先通过旧版本恢复。 ");}catch(InvalidOperationException e)when(e.InnerException is System.ComponentModel.Win32Exception w && w.NativeErrorCode==1060){}
        string dest=Path.Combine(Env.Root,"state","engine.sqlite");if(File.Exists(dest))throw new IOException("Existing beta database left unchanged");string copy=Path.Combine(Env.Root,"state","legacy-import-"+Guid.NewGuid().ToString("N")+".sqlite");
        foreach(string suffix in new[]{"","-wal"}){string old=Path.Combine(src,"state","engine.sqlite"+suffix);if(File.Exists(old))File.Copy(old,copy+suffix,false);}
        var snapshot=StateRepository.OpenExisting(copy).Read().Snapshot;var environment=PolicyJson.Parse<System.Text.Json.JsonElement>(await Env.Native("--environment"));var now=DateTimeOffset.UtcNow;if(now<snapshot.Clock.EffectiveUtc)throw new InvalidOperationException("系统时间早于保存记录，拒绝迁移并重置时间。请先核对虚拟机时间。");
        snapshot=snapshot with {Clock=new(now,true),ServiceAnchor=new(environment.GetProperty("bootId").GetString()!,Environment.TickCount64,true)};
        _ = StateRepository.CreateNew(dest,snapshot);using(var marker=new FileStream(Path.Combine(Env.Root,"state","initialized.marker"),FileMode.CreateNew,FileAccess.Write,FileShare.Read))marker.Flush(true);
        Env.Save(Path.Combine(Env.Root,"state","migration.json"),new {sourceRunId=args[1],rules=snapshot.Policy.Groups.Length,pendingId=snapshot.Pending?.Id,quotaPreserved=true,sourceNotModified=true,administrativeStartupTimeAnchor=true,pendingDeadlinesAndCycleAnchorsPreserved=true});return 0;
    }
    if(args.Length==1 && args[0] is "--beta-cleanup" or "--beta-cleanup-force") {
        bool force=args[0]=="--beta-cleanup-force";if(!force && !StateRepository.OpenExisting(Path.Combine(Env.Root,"state","engine.sqlite")).Read().Snapshot.UninstallReady)throw new UnauthorizedAccessException("Backend has not authorized uninstall");
        var cleanupErrors=new List<Exception>();try{await Env.Native("--cleanup");}catch(Exception e){cleanupErrors.Add(e);}try{using var web=new Browsers();web.Restore();}catch(Exception e){cleanupErrors.Add(e);}if(cleanupErrors.Count>0)throw new AggregateException("Owned cleanup incomplete; independent app/network recovery attempts recorded",cleanupErrors);
        dynamic scheduler=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;try{scheduler.Connect();dynamic folder=scheduler.GetFolder("\\");foreach(string type in new[]{"Agent","Uninstall","Recovery","TrialSafety"}){string name="AICT-Beta-"+type+"-"+Env.Config.RunId;dynamic task;try{task=folder.GetTask(name);}catch(COMException e)when((uint)e.ErrorCode==0x80070002){continue;}string expected=type=="Agent"?Path.Combine(Env.Root,@"Agent\Aict.Beta.Agent.exe"):Path.Combine(Env.Root,@"Service\Aict.Beta.Service.exe");string expectedArgs=type=="Agent"?"--session-agent":type=="Uninstall"?"--beta-cleanup":type=="TrialSafety"?"--host-trial-safety":"--beta-cleanup-force";if(task.Definition.Actions.Count!=1 || !string.Equals(Convert.ToString(task.Definition.Actions.Item(1).Path),expected,StringComparison.OrdinalIgnoreCase) || Convert.ToString(task.Definition.Actions.Item(1).Arguments)!=expectedArgs)throw new InvalidDataException("Owned task changed; not deleted");if(type=="Agent")task.Stop(0);folder.DeleteTask(name,0);}}finally{Marshal.FinalReleaseComObject(scheduler);}
        using(var uninstall=Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",true)){string key="AICT-Beta-"+Env.Config.RunId;using(var entry=uninstall?.OpenSubKey(key)){if(entry is not null && !string.Equals(Convert.ToString(entry.GetValue("InstallLocation")),Env.Root,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Uninstall entry changed");}uninstall?.DeleteSubKey(key,false);}
        Env.Save(Path.Combine(Env.Root,"state","cleanup-result.json"),new {completed=true,force,atUtc=DateTimeOffset.UtcNow,dataRetained=true});return 0;
    }
    return 2;
}catch(Exception e){Console.Error.WriteLine(e.ToString());try{if(WindowsIdentity.GetCurrent().User?.Value=="S-1-5-18" && Directory.Exists(Path.Combine(Env.Root,"state")))Env.Save(Path.Combine(Env.Root,"state","operation-fault.json"),new{error=e.ToString(),args});}catch{ }return 1;}
