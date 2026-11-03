using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using Aict.Contracts;
using Aict.Core;

namespace Aict.Service;

internal static class InteractiveSmoke {
    internal static async Task<int> RunAsync(LabConfiguration config) {
        var checks=new List<string>();var launches=new List<object>();Process? demo=null;
        async Task<PreviewResponse> Raw(string json) {
            using var pipe=new NamedPipeClientStream(".","AICT-workspace-"+config.RunId,PipeDirection.InOut,PipeOptions.Asynchronous,TokenImpersonationLevel.Identification);
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(6));await pipe.ConnectAsync(timeout.Token);ServicePeer.Verify(pipe,"AICT-Interactive-"+config.RunId);
            await PipeFrames.WriteAsync(pipe,json,PipeFrames.MaxRequestBytes,timeout.Token);
            return PolicyJson.Parse<PreviewResponse>(await PipeFrames.ReadAsync(pipe,PipeFrames.MaxResponseBytes,timeout.Token));
        }
        Task<PreviewResponse> Call(PreviewRequest request)=>Raw(PolicyJson.Serialize(request));
        async Task<PreviewStatus> Status(){var value=await Call(new());if(!value.Ok || value.Status is null) throw new InvalidOperationException(value.Message);return value.Status;}
        void Check(bool good,string label) {if(!good) throw new InvalidOperationException(label);checks.Add(label);}
        try {
            var initial=await Status();Check(initial.Policy.Groups.IsEmpty && initial.Pending is null && initial.Policy.ChangeDelay==TimeSpan.Zero,"fresh workspace only; existing user rules are never replaced by smoke tests");
            var targets=new List<ApplicationTarget>();
            foreach(string relative in new[]{@"Targets\Target.exe",@"Targets\AictDemo.exe"}) {
                string path=Path.Combine(LabEnvironment.Root,relative);var app=new ApplicationTarget(ApplicationTarget.Prefix+Aict.Desktop.AuthenticodeHash.Compute(path)[2..],"Inert smoke sample",Path.GetFileName(path),new FileInfo(path).Length);
                var registered=await Call(new() {Command=PreviewCommand.RegisterApplication,EditToken=(await Status()).EditToken,Application=app});Check(registered.Ok,"limited client registers identity metadata without a server file path");targets.Add(app);
            }
            (bool Allowed,bool Denied) Launch(string file,string marker,string purpose) {
                long startTick=Stopwatch.GetTimestamp();var atUtc=DateTimeOffset.UtcNow;int nativeError=0,exitCode=-1;bool started=false,matched=false;
                try {using var p=Process.Start(new ProcessStartInfo(Path.Combine(LabEnvironment.Root,file)) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true})!;started=true;if(!p.WaitForExit(3000)) {p.Kill();throw new IOException("Inert fixture exit timed out");}exitCode=p.ExitCode;matched=exitCode==0 && p.StandardOutput.ReadToEnd().Trim()==marker && p.StandardError.ReadToEnd().Length==0;}
                catch(Win32Exception e){nativeError=e.NativeErrorCode;}
                launches.Add(new {purpose,atUtc,startTick,endTick=Stopwatch.GetTimestamp(),stopwatchFrequency=Stopwatch.Frequency,started,exitCode,markerMatched=matched,nativeErrorCode=nativeError});
                if(!matched && nativeError!=1260) throw new IOException("Unexpected fixture launch result: "+nativeError+"/"+exitCode);
                return(matched,nativeError==1260 && !started);
            }
            async Task<bool> WaitLaunch(bool denied,string purpose) {
                var wait=Stopwatch.StartNew();do {var attempt=Launch(@"Targets\Target.exe","AICT_FIXTURE_OK",purpose);if(denied?attempt.Denied:attempt.Allowed) return true;await Task.Delay(100);}while(wait.Elapsed.TotalSeconds<6);return false;
            }
            Check(Launch(@"Targets\Target.exe","AICT_FIXTURE_OK","baseline").Allowed,"baseline fixture starts before application policy");
            demo=Process.Start(new ProcessStartInfo(Path.Combine(LabEnvironment.Root,@"Targets\AictDemo.exe")) {UseShellExecute=false});if(demo is null) throw new IOException("Inert window did not launch");
            Check(demo.WaitForInputIdle(3000) && !demo.HasExited,"inert window is already running before the policy submission");
            var state=await Status();var policy=state.Policy with {Groups=[new() {Id=Guid.NewGuid(),Name="Automatic inert application check",ApplicationMode=TargetMode.BlockSelected,ApplicationIds=targets.Select(a=>a.Id).ToImmutableArray(),Cycle=new(state.EffectiveUtc,TimeSpan.FromSeconds(30),TimeSpan.FromMinutes(1)),LockWhileBlocking=true}]};
            var submitted=await Call(new() {Command=PreviewCommand.SubmitPolicy,EditToken=state.EditToken,Policy=policy});Check(submitted.Ok,"real backend persists the UI-style application policy");
            var timeout=Stopwatch.StartNew();PreviewStatus current;
            do {current=await Status();if(current.Execution?.Verified==true && current.Execution.VerifiedDeniedIdentities.Length==2) break;await Task.Delay(100);} while(timeout.Elapsed.TotalSeconds<8);
            Check(current.Execution?.Verified==true && current.Execution.VerifiedDeniedIdentities.Length==2,"native readback contains both selected PE identities");
            Check(await WaitLaunch(true,"block-transition"),"selected fixture launch is denied by AppLocker with error 1260");
            Check(Launch(@"Targets\AllowedControl.exe","AICT_CONTROL_OK","control").Allowed,"different-content control remains allowed");
            timeout.Restart();while(!demo.HasExited && timeout.Elapsed.TotalSeconds<5) await Task.Delay(100);
            Check(demo.HasExited,"the already-running inert application exits after its identity is blocked");
            state=await Status();var disabled=policy with {Groups=[]};var pending=await Call(new() {Command=PreviewCommand.SubmitPolicy,EditToken=state.EditToken,Policy=disabled});
            Check(pending.Ok && pending.Status?.Pending is not null,"disable is frozen while the old block cycle is locked");Guid id=pending.Status!.Pending!.Id;
            var repeated=await Call(new() {Command=PreviewCommand.SubmitPolicy,EditToken=pending.Status.EditToken,Policy=disabled});Check(!repeated.Ok && repeated.Code=="PENDING_EXISTS","waiting payload cannot be replaced after submission");
            var injected=await Raw("{\"command\":\"Status\",\"clock\":{\"trusted\":true}}");Check(!injected.Ok,"client cannot inject authority time");
            var stop=await Raw("{\"command\":\"Stop\"}");Check(!stop.Ok,"client has no arbitrary service-stop command");
            timeout.Restart();do {current=await Status();if(current.Pending is null && current.Policy.Groups.IsEmpty && current.Execution?.Verified==true && current.Execution.VerifiedDeniedIdentities.IsEmpty) break;await Task.Delay(100);}while(timeout.Elapsed.TotalSeconds<45);
            Check(current.Pending is null && current.Policy.Groups.IsEmpty && current.Execution?.Verified==true && current.Execution.VerifiedDeniedIdentities.IsEmpty,"old cycle ends and the frozen disable applies with native readback");
            Check(await WaitLaunch(false,"release-transition"),"fixture launch is restored after policy release");
            LabEnvironment.Save(Path.Combine(LabEnvironment.Root,"probe","workspace-smoke.json"),new {runId=config.RunId,passed=true,limitedToken=true,checks,launches,pendingIdObserved=id,manualUiAndTaskManagerChecksAwarded=false});return 0;
        } catch(Exception e) {
            LabEnvironment.Save(Path.Combine(LabEnvironment.Root,"probe","workspace-smoke.json"),new {runId=config.RunId,passed=false,limitedToken=true,checks,launches,error=e.ToString(),manualUiAndTaskManagerChecksAwarded=false});Console.Error.WriteLine(e.Message);return 1;
        } finally {if(demo is not null) {if(!demo.HasExited) {demo.Kill();await demo.WaitForExitAsync();}demo.Dispose();}}
    }
}
