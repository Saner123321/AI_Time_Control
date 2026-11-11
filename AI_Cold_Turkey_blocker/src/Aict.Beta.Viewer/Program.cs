using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Aict.Beta;
using Aict.Contracts;
using Aict.Core;

internal static class ViewerProgram {
    internal static string Root,RunId;
    static void Guard(){
        Root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,".."));string name=Path.GetFileName(Root);
        if(!name.StartsWith("AICT-Beta-",StringComparison.Ordinal)||!Guid.TryParseExact(name.Substring(10),"N",out _)||!Root.Equals(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),name),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Protected viewer installation required");
        var c=PolicyJson.Parse<System.Text.Json.JsonElement>(File.ReadAllText(Path.Combine(Root,"config.json")));RunId=name.Substring(10);
        if(c.GetProperty("runId").GetString()!=RunId||c.GetProperty("probeSid").GetString()!=WindowsIdentity.GetCurrent().User?.Value)throw new UnauthorizedAccessException("Viewer user mismatch");
        if(BuildMode.HostTrial)HostInstallation.Validate(c.GetProperty("hostTrial").GetBoolean(),c.TryGetProperty("continuousUse",out var continuous)&&continuous.GetBoolean(),c.GetProperty("hostTrialEndsUtc").ValueKind==System.Text.Json.JsonValueKind.Null?null:c.GetProperty("hostTrialEndsUtc").GetDateTimeOffset());
    }
    [STAThread] static int Main(string[] args){
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        try{
            if(args.SequenceEqual(new[]{"--build-info"})){Console.Write(PolicyJson.Serialize(new{hostTrial=BuildMode.HostTrial,revision="manual-view-v14"}));return 0;}
            if(args.Length==2&&args[0]=="--self-test"){ViewerSelfTest.Run(args[1]);return 0;}
            if(args.SequenceEqual(new[]{"--security-fixture-wait"})){Thread.Sleep(15000);return 0;}
            if(args.SequenceEqual(new[]{"--fixture-probe"})){Console.Write(ViewerSelfTest.FixtureJson());return 0;}
            Guard();
            if(args.SequenceEqual(new[]{"--probe"})){
                var watchdog=new Thread(()=>{Thread.Sleep(1900);Environment.Exit(3);}){IsBackground=true};watchdog.Start();
                Console.Write(PolicyJson.Serialize(ManualBrowserProbe.ReadForeground()));return 0;
            }
            if(!args.SequenceEqual(new[]{"--session-viewer"}))return 2;
            using var instance=new Mutex(false,"Local\\AICT-Viewer-"+RunId);bool held;try{held=instance.WaitOne(0);}catch(AbandonedMutexException){held=true;}if(!held)return 0;
            try{Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);using var context=new ViewerContext();Application.Run(context);}finally{instance.ReleaseMutex();}return 0;
        }catch{return 1;}
    }
    internal static async Task<Response> Call(ManualViewingSample sample,CancellationToken cancel){
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancel);timeout.CancelAfter(3000);
        using var pipe=new NamedPipeClientStream(".","AICT-beta-"+RunId,PipeDirection.InOut,PipeOptions.Asynchronous,TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(timeout.Token);Aict.Service.ServicePeer.Verify(pipe,"AICT-Beta-"+RunId);
        await PipeFrames.WriteAsync(pipe,PolicyJson.Serialize(new Request{Command=Command.ManualViewingPulse,ManualViewing=sample}),PipeFrames.MaxRequestBytes,timeout.Token);
        return PolicyJson.Parse<Response>(await PipeFrames.ReadAsync(pipe,PipeFrames.MaxResponseBytes,timeout.Token));
    }
}
internal sealed class ViewerContext:ApplicationContext {
    readonly object gate=new();readonly CancellationTokenSource stop=new();
    readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer{Interval=100};
    readonly List<ViewMask> masks=new();readonly ManualViewConfirmation confirmation=new();
    BrowserProbeResult raw=new(){State="waiting"};long captured=-100000,sequence,seen=-1,replyAt=-100000,uiAt=Environment.TickCount64;
    ManualViewingPlan plan;string applied="";ImmutableArray<string> rendered=[];bool completed,uiFault;
    internal ViewerContext(){timer.Tick+=(s,e)=>Tick();timer.Start();_ = Task.Run(Observe);}
    async Task Observe(){
        while(!stop.IsCancellationRequested){
            try{
                var next=await Probe(stop.Token);long now=Environment.TickCount64;ManualViewingSample sample;
                lock(gate){raw=next;captured=now;sequence++;var observation=next.State=="other_application_foreground"||next.State=="client_not_visible"?ManualObservation.OutsideClient:next.State=="committed_document_urls_observed"&&!next.Truncated?ManualObservation.Confirmed:ManualObservation.Unavailable;
                    sample=new(observation,observation==ManualObservation.Confirmed?next.Pages.Select(p=>p.Url).ToImmutableArray():[],Math.Max(0,next.DurationMs),Math.Max(0,now-uiAt),applied,rendered,uiFault?"mask_configuration_failed":SafeDiagnostic(next.State));}
                var response=await ViewerProgram.Call(sample,stop.Token);
                if(!response.Ok||response.ManualViewingPlan==null)throw new IOException("Viewer plan unavailable");
                lock(gate){plan=response.ManualViewingPlan;replyAt=Environment.TickCount64;if(plan.Uninstall)completed=true;}
            }catch(OperationCanceledException)when(stop.IsCancellationRequested){return;}
            catch{
                try{var file=Path.Combine(ViewerProgram.Root,"state","cleanup-result.json");if(File.Exists(file)&&PolicyJson.Parse<System.Text.Json.JsonElement>(File.ReadAllText(file)).GetProperty("completed").GetBoolean()){lock(gate)completed=true;}}catch{}
            }
            try{await Task.Delay(500,stop.Token);}catch(OperationCanceledException){return;}
        }
    }
    static string SafeDiagnostic(string value)=>value.Length<=100&&value.All(c=>char.IsAsciiLetterOrDigit(c)||c=='_')?value:"probe_unavailable";
    internal static async Task<BrowserProbeResult> Probe(CancellationToken cancel,string mode="--probe"){
        using var p=new Process{StartInfo=new ProcessStartInfo(Environment.ProcessPath,mode){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true}};
        p.Start();using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancel);timeout.CancelAfter(1700);var output=p.StandardOutput.ReadToEndAsync(timeout.Token);
        try{await p.WaitForExitAsync(timeout.Token);if(p.ExitCode!=0)return new(){State="probe_failed"};return PolicyJson.Parse<BrowserProbeResult>(await output);}catch(OperationCanceledException){return new(){State="probe_timeout"};}finally{if(!p.HasExited){try{p.Kill();await p.WaitForExitAsync(CancellationToken.None);}catch{}}}
    }
    void Tick(){
        long now=Environment.TickCount64;BrowserProbeResult snapshot;ManualViewingPlan current;long stamp,received;bool done;
        lock(gate){uiAt=now;snapshot=raw;current=plan;stamp=captured;received=replyAt;done=completed;if(seen!=sequence){seen=sequence;confirmation.Observe(snapshot,stamp-snapshot.DurationMs);}}
        if(done){ExitThread();return;}
        var foreground=ManualBrowserProbe.GetAncestor(ManualBrowserProbe.GetForegroundWindow(),2);ManualBrowserProbe.Rect owner;var bounds=ManualBrowserProbe.GetWindowRect(foreground,out owner)?owner.Box:Rectangle.Empty;string reason;
        var selected=confirmation.Select(now,foreground.ToInt64(),bounds,out reason);var shown=ImmutableArray.CreateBuilder<string>();bool success=true;
        try{
            var wanted=new List<(VisibleBrowserPage Page,string Text)>();
            if(selected!=null&&current!=null){
                bool serviceFresh=now-received<=4000;
                foreach(var page in selected.Pages){string key=ManualViewingRules.Key(page.Url);var decision=current.Pages.FirstOrDefault(d=>d.Key==key);bool deny=serviceFresh?decision?.Denied??false:current.FailureScope.Groups.Any(g=>Plans.Denies(g,page.Url));
                    if(deny)wanted.Add((page,serviceFresh?decision.Explanation:"后台连接暂未恢复，当前规则范围继续保护。"));
                }
            }
            while(masks.Count<wanted.Count)masks.Add(new ViewMask());
            for(int i=0;i<masks.Count;i++){
                if(i<wanted.Count){var target=wanted[i];masks[i].Message("当前网页已受限制",target.Text+"\nAI仍可读取和操作页面；切换到其他网页或软件可继续工作。");masks[i].Place(target.Page.Rectangle);shown.Add(ManualViewingRules.Key(target.Page.Url));}else masks[i].Hide();
            }
        }catch{success=false;foreach(var mask in masks)mask.Hide();for(int i=masks.Count-1;i>=0;i--)if(masks[i].IsDisposed)masks.RemoveAt(i);}
        lock(gate){uiFault=!success;rendered=shown.ToImmutable();applied=success&&current!=null&&now-received<=4000?current.Fingerprint:"";}
    }
    protected override void Dispose(bool disposing){if(disposing){stop.Cancel();timer.Stop();timer.Dispose();foreach(var mask in masks)mask.Dispose();stop.Dispose();}base.Dispose(disposing);}
}
