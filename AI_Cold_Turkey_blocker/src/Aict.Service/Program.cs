using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.ServiceProcess;
using Aict.Contracts;
using Aict.Core;
using Aict.Enforcement;
using Aict.Service;
using Aict.Storage;

try {
    if(args.Length==1 && args[0]=="--offline-check") {
        var security=ServiceStatusPipe.Security("S-1-5-21-1-2-3-1001");
        if(!security.AreAccessRulesProtected) throw new InvalidOperationException("Pipe DACL is not protected");
        Console.WriteLine(security.GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.All));return 0;
    }
    if(args.Length!=1 || args[0] is not ("--vm-lab" or "--lab-client" or "--vm-interactive" or "--workspace-smoke")) return 2;
    // Physical host refused before directory reads, SCM connection, database or policy access.
    var config=LabEnvironment.Guard(args[0] is "--vm-lab" or "--vm-interactive",args[0] is "--vm-interactive" or "--workspace-smoke");
    if(args[0]=="--workspace-smoke") return await InteractiveSmoke.RunAsync(config);
    if(args[0]=="--vm-interactive") {ServiceBase.Run(new InteractiveService(config));return 0;}
    if(args[0]=="--lab-client") {
        async Task<ServiceReply> Call(string json) {
            using var pipe=new NamedPipeClientStream(".",ServiceStatusPipe.Name(config),PipeDirection.InOut,PipeOptions.Asynchronous,TokenImpersonationLevel.Identification);
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));await pipe.ConnectAsync(timeout.Token);
            ServicePeer.Verify(pipe,"AICT-Policy-"+config.RunId);
            await PipeFrames.WriteAsync(pipe,json,8192,timeout.Token);
            return PolicyJson.Parse<ServiceReply>(await PipeFrames.ReadAsync(pipe,512*1024,timeout.Token));
        }
        var response=await Call(PolicyJson.Serialize(new ServiceQuery()));
        if(!response.Ok || response.Status?.RunId!=config.RunId) throw new InvalidDataException("Service status identity mismatch");
        var stopAttempt=await Call("{\"version\":1,\"command\":\"Stop\"}");
        var injected=await Call("{\"version\":1,\"command\":\"Status\",\"clock\":{\"trusted\":true}}");
        if(stopAttempt.Ok || injected.Ok) throw new InvalidDataException("Unexpected authority accepted from limited client");
        LabEnvironment.Save(Path.Combine(LabEnvironment.Root,"probe","pipe-client.json"),new {runId=config.RunId,limitedToken=true,status=response.Status,stopRejected=true,clockInjectionRejected=true});
        Console.WriteLine("Limited-token status query succeeded");return 0;
    }
    ServiceBase.Run(new PolicyService(config));return 0;
} catch(Exception e) {Console.Error.WriteLine(e.GetType().Name+": "+e.Message);return 1;}

sealed class PolicyService : ServiceBase {
    readonly LabConfiguration config;
    internal PolicyService(LabConfiguration config) {this.config=config;ServiceName="AICT-Policy-"+config.RunId;CanStop=true;AutoLog=false;}
    readonly CancellationTokenSource stop=new();
    Task? work;
    LabServiceStatus? status;
    protected override void OnStart(string[] args) {
        work=Task.Run(Run);
    }
    protected override void OnStop() {stop.Cancel();if(work is not null && !work.Wait(TimeSpan.FromSeconds(25))) throw new System.TimeoutException("Worker stop timeout; policy was retained");}
    protected override void OnCustomCommand(int command) {
        if(command!=128) return;
        string marker=Path.Combine(LabEnvironment.Root,"crash-once.marker");
        try {using var file=new FileStream(marker,FileMode.CreateNew,FileAccess.Write,FileShare.Read);file.Flush(true);}
        catch(IOException) {return;}
        // Let SCM acknowledge the fixed control before terminating only this service process.
        ThreadPool.QueueUserWorkItem(_=>{Thread.Sleep(150);Process.GetCurrentProcess().Kill();});
    }
    async Task Run() {
        Task? pipe=null;
        try {
            string stateDirectory=Path.Combine(LabEnvironment.Root,"state"),database=Path.Combine(stateDirectory,"engine.sqlite");
            StateRepository repository;EngineSnapshot state;long revision;long stamp=Stopwatch.GetTimestamp();long? cycleStartTick=null;
            if(File.Exists(database)) {repository=StateRepository.OpenExisting(database);var stored=repository.Read();state=stored.Snapshot;revision=stored.Revision;}
            else {
                if(File.Exists(Path.Combine(stateDirectory,"initialized.marker"))) throw new InvalidDataException("Initialized database missing; refusing reset");
                string identity=ApplicationPlanner.HashIdentity(config.TargetSipHash[2..]);stamp=Stopwatch.GetTimestamp();var now=DateTimeOffset.UtcNow;cycleStartTick=stamp+15*Stopwatch.Frequency;
                var policy=new PolicyConfiguration {TimeZoneId="UTC",ChangeDelay=TimeSpan.FromHours(24),Groups=[new() {Id=Guid.ParseExact(config.RunId,"N"),Name="Frozen inert application fixture",ApplicationMode=TargetMode.BlockSelected,ApplicationIds=[identity],
                    Cycle=new(now+TimeSpan.FromSeconds(15),TimeSpan.FromMinutes(10),TimeSpan.FromMinutes(1)),LockWhileBlocking=true}]};
                state=new() {Policy=policy,Clock=new(now,true)};
                // An actual frozen, harmless pending change to verify SQLite survives process recovery.
                state=ChangeQueue.Submit(state,policy with {Groups=[policy.Groups[0] with {Enabled=false}]},state.Clock.Reading);
                repository=StateRepository.CreateNew(database,state);revision=0;
                using var marker=new FileStream(Path.Combine(stateDirectory,"initialized.marker"),FileMode.CreateNew,FileAccess.Write,FileShare.Read);marker.Flush(true);
            }
            string instance=Guid.NewGuid().ToString("N");var reconciler=new ApplicationReconciler(new FixturePolicyAdapter(config));
            void Commit(EngineSnapshot next) {if(!repository.TryCommit(revision,next)) throw new InvalidDataException("Unexpected database writer");state=next;revision++;}
            Commit(state); // Preserve restart's UNVERIFIED clock before any execution-layer calls.
            var plan=ApplicationPlanner.Compile(state);UpdateStatus(null);
            pipe=ServiceStatusPipe.RunAsync(config,()=>Volatile.Read(ref status) ?? throw new InvalidOperationException("Status not ready"),stop.Token);
            while(!stop.IsCancellationRequested) {
                long tick=Stopwatch.GetTimestamp();state=state with {Clock=state.Clock.Advance(DateTimeOffset.UtcNow,Stopwatch.GetElapsedTime(stamp,tick))};stamp=tick;
                var applied=ChangeQueue.TryApply(state,state.Clock.Reading,telemetryHealthy:false);Commit(applied.Snapshot);
                plan=ApplicationPlanner.Compile(state);
                await reconciler.ReconcileAsync(plan,stop.Token);
                UpdateStatus(reconciler.Fault);
                LabEnvironment.Save(Path.Combine(stateDirectory,"service-status.json"),status!);
                if(pipe.IsFaulted) throw new InvalidOperationException("Authorized status pipe failed",pipe.Exception);
                await Task.Delay(250,stop.Token);
            }
            void UpdateStatus(string? fault) => Volatile.Write(ref status,new(config.RunId,instance,Environment.ProcessId,state.Clock.Trusted,state.Clock.Anomaly ?? "INITIAL_INSTALL_ANCHOR",
                !plan.DeniedIdentities.IsEmpty,reconciler.LastObservation?.ReadbackBlocked,reconciler.LastVerifiedUtc,fault,revision,PolicyJson.Fingerprint(state.Policy),state.Pending?.Id,state.Pending?.TargetSha256,state.Pending?.NotBeforeUtc,state.Clock.EffectiveUtc,cycleStartTick,Stopwatch.Frequency));
        } catch(OperationCanceledException) when(stop.IsCancellationRequested) { }
        catch(Exception e) {
            try {LabEnvironment.Save(Path.Combine(LabEnvironment.Root,"state","service-fault.json"),new {runId=config.RunId,error=e.ToString(),policyNotAutomaticallyRemoved=true});}
            catch(Exception loggingFailure) {Console.Error.WriteLine("Fault evidence could not be saved: "+loggingFailure.Message);}
            // Nonzero process termination lets SCM recovery act. A clean service stop must not silently leave a dead scheduler.
            Environment.Exit(1);
        } finally {stop.Cancel();if(pipe is not null) await pipe;}
    }
}
