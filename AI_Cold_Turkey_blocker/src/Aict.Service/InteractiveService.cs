using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.ServiceProcess;
using Aict.Backend;
using Aict.Contracts;
using Aict.Core;
using Aict.Enforcement;
using Aict.Storage;

namespace Aict.Service;

internal sealed class InteractiveService : ServiceBase {
    readonly LabConfiguration config;
    readonly CancellationTokenSource stop=new();
    Task? work;
    internal InteractiveService(LabConfiguration config) {this.config=config;ServiceName="AICT-Interactive-"+config.RunId;CanStop=true;AutoLog=false;}
    protected override void OnStart(string[] args) => work=Task.Run(Run);
    protected override void OnStop() {stop.Cancel();if(work is not null && !work.Wait(TimeSpan.FromSeconds(25))) throw new System.TimeoutException("Interactive scheduler stop timeout");}
    protected override void OnCustomCommand(int command) {
        if(command!=128) return;
        try {using var marker=new FileStream(Path.Combine(LabEnvironment.Root,"crash-once.marker"),FileMode.CreateNew,FileAccess.Write,FileShare.Read);marker.Flush(true);} catch(IOException) {return;}
        ThreadPool.QueueUserWorkItem(_=>{Thread.Sleep(150);Process.GetCurrentProcess().Kill();});
    }
    async Task Run() {
        Task? pipe=null;
        try {
            var environment=PolicyJson.Parse<EnvironmentInfo>(await Bridge("--environment",stop.Token));
            if(environment.RunId!=config.RunId || string.IsNullOrWhiteSpace(environment.BootId)) throw new InvalidDataException("Invalid OS continuation identity");
            var clock=new WorkspaceClock(environment.BootId);
            string stateDirectory=Path.Combine(LabEnvironment.Root,"state"),database=Path.Combine(stateDirectory,"engine.sqlite");
            StateRepository repository;
            if(File.Exists(database)) repository=StateRepository.OpenExisting(database);
            else {
                if(File.Exists(Path.Combine(stateDirectory,"initialized.marker"))) throw new InvalidDataException("Initialized database missing; refusing reset");
                repository=StateRepository.CreateNew(database,WorkspaceAuthority.Initial(clock));
                using var marker=new FileStream(Path.Combine(stateDirectory,"initialized.marker"),FileMode.CreateNew,FileAccess.Write,FileShare.Read);marker.Flush(true);
            }
            using var authority=new WorkspaceAuthority(repository,clock,environment.ProtectedIdentities);
            pipe=RunPipe(authority);
            var terminator=new SelectedProcessTerminator(config.ProbeSid,environment.ProtectedIdentities);
            while(!stop.IsCancellationRequested) {
                await authority.TickAsync(stop.Token);var (plan,targets)=await authority.DesiredAsync(stop.Token);
                try {
                    LabEnvironment.Save(Path.Combine(stateDirectory,"desired-plan.json"),new {runId=config.RunId,applications=targets.Where(a=>plan.DeniedIdentities.Contains(a.Id)).Select(a=>new {id=a.Id,name=a.Name,fileName=a.FileName,size=a.Size}).ToArray()});
                    var observation=PolicyJson.Parse<NativeObservation>(await Bridge("--policy-block",stop.Token));
                    if(observation.RunId!=config.RunId || !observation.DeniedIdentities.SequenceEqual(plan.DeniedIdentities)) throw new InvalidDataException("Actual AppLocker identities do not match the desired rule plan");
                    if(plan.DeniedIdentities.Length>0 && (!observation.ExePresent || !observation.CompanionPresent)) throw new InvalidDataException("Native policy collection not verified");
                    await authority.ObserveAsync(plan.Fingerprint,observation.DeniedIdentities,DateTimeOffset.UtcNow,null,stop.Token);
                    await terminator.EnforceAsync(plan.DeniedIdentities,stop.Token);
                } catch(OperationCanceledException) when(stop.IsCancellationRequested) {throw;}
                catch(Exception e) {await authority.ObserveAsync(plan.Fingerprint,[],DateTimeOffset.UtcNow,e.GetType().Name+": "+e.Message,stop.Token);}
                var status=await authority.HandleAsync(new(),stop.Token);
                LabEnvironment.Save(Path.Combine(stateDirectory,"workspace-status.json"),status);
                if(pipe.IsFaulted) throw new InvalidOperationException("Authorized workspace pipe failed",pipe.Exception);
                await Task.Delay(500,stop.Token);
            }
        } catch(OperationCanceledException) when(stop.IsCancellationRequested) { }
        catch(Exception e) {
            try {LabEnvironment.Save(Path.Combine(LabEnvironment.Root,"state","workspace-fault.json"),new {runId=config.RunId,error=e.ToString(),existingPoliciesRetained=true});} catch(Exception failure) {Console.Error.WriteLine(failure.Message);}
            Environment.Exit(1);
        } finally {stop.Cancel();if(pipe is not null) await pipe;}
    }
    async Task<string> Bridge(string mode,CancellationToken cancellation) {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);timeout.CancelAfter(TimeSpan.FromSeconds(35));
        using var process=new Process {StartInfo=new ProcessStartInfo(Path.Combine(LabEnvironment.Root,"AictInteractiveBridge.exe")) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true}};
        process.StartInfo.ArgumentList.Add(mode);process.Start();var output=process.StandardOutput.ReadToEndAsync(timeout.Token);var error=process.StandardError.ReadToEndAsync(timeout.Token);
        try {await process.WaitForExitAsync(timeout.Token);string result=await output,errors=await error;if(process.ExitCode!=0) throw new InvalidOperationException(errors.Trim());return result;}
        finally {if(!process.HasExited) {process.Kill(true);await process.WaitForExitAsync();}}
    }
    async Task RunPipe(WorkspaceAuthority authority) {
        var clients=new List<Task>();NamedPipeServerStream? waiting=null;
        NamedPipeServerStream Create(bool first)=>NamedPipeServerStreamAcl.Create("AICT-workspace-"+config.RunId,PipeDirection.InOut,5,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|(first?PipeOptions.FirstPipeInstance:PipeOptions.None),8192,8192,ServiceStatusPipe.Security(config.ProbeSid));
        try {
            waiting=Create(true);
            while(!stop.IsCancellationRequested) {
                clients.RemoveAll(t=>t.IsCompleted);if(clients.Count>=4) {await Task.WhenAny(clients);continue;}
                await waiting.WaitForConnectionAsync(stop.Token);var connected=waiting;waiting=Create(false);clients.Add(Serve(connected));
            }
        } catch(OperationCanceledException) when(stop.IsCancellationRequested) { }
        finally {waiting?.Dispose();await Task.WhenAll(clients);}
        async Task Serve(NamedPipeServerStream connected) {
            using(connected) using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop.Token)) {
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                try {
                    string json=await PipeFrames.ReadAsync(connected,PipeFrames.MaxRequestBytes,timeout.Token);string? sid=null;
                    connected.RunAsClient(()=>{using var identity=WindowsIdentity.GetCurrent();sid=identity.User?.Value;});
                    PreviewResponse response;
                    if(sid!=config.ProbeSid) response=new(false,"UNAUTHORIZED","此接口仅供指定的本机用户使用。");
                    else {try {response=await authority.HandleAsync(PolicyJson.Parse<PreviewRequest>(json),timeout.Token);} catch(Exception e) when(e is ArgumentException or System.Text.Json.JsonException or InvalidDataException) {response=new(false,"INVALID_REQUEST","请求格式不符合协议。");}}
                    await PipeFrames.WriteAsync(connected,PolicyJson.Serialize(response),PipeFrames.MaxResponseBytes,timeout.Token);
                } catch(Exception e) when(e is IOException or InvalidDataException or OperationCanceledException or UnauthorizedAccessException or System.Text.DecoderFallbackException) { }
            }
        }
    }
    sealed record EnvironmentInfo(string RunId,string BootId,ImmutableArray<string> ProtectedIdentities);
    sealed record NativeObservation(string RunId,bool ExePresent,bool CompanionPresent,ImmutableArray<string> DeniedIdentities);
    sealed class WorkspaceClock(string bootId) : IWorkspaceClock {
        public string BootId=>bootId;public long UptimeMilliseconds=>Environment.TickCount64;public DateTimeOffset UtcNow=>DateTimeOffset.UtcNow;
    }
}
