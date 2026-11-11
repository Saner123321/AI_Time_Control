using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Aict.Core;

// One owned, hidden console worker serves repeated observations. Accessibility
// providers remain isolated: a hung request is bounded and the worker replaced.
internal sealed class BrowserProbeWorker:IDisposable {
    readonly string mode;Process process;
    internal int Starts {get;private set;}
    internal int WorkerId=>process?.Id??0;
    internal BrowserProbeWorker(string mode="--probe-worker"){this.mode=mode;}
    internal async Task<BrowserProbeResult> Read(CancellationToken cancel,string command="probe"){
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancel);timeout.CancelAfter(1700);
        try{
            if(process is null||process.HasExited){Reset();process=new Process{StartInfo=new ProcessStartInfo(Environment.ProcessPath,mode){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true}};process.Start();Starts++;Trace("worker_started");}
            await process.StandardInput.WriteLineAsync(command.AsMemory(),timeout.Token);await process.StandardInput.FlushAsync(timeout.Token);
            string line=await process.StandardOutput.ReadLineAsync(timeout.Token);
            if(line is null||line.Length>262144)throw new IOException("Probe worker response unavailable");
            return PolicyJson.Parse<BrowserProbeResult>(line);
        }catch(OperationCanceledException){Trace(cancel.IsCancellationRequested?"worker_cancelled":"worker_timeout");Reset();cancel.ThrowIfCancellationRequested();return new(){State="probe_timeout"};}
        catch(Exception error)when(error is IOException or InvalidOperationException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception){Trace("worker_failed_"+error.GetType().Name);Reset();return new(){State="probe_failed"};}
    }
    void Trace(string reason){if(mode!="--probe-worker")return;try{string folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AICT");Directory.CreateDirectory(folder);string file=Path.Combine(folder,"viewer-worker-"+ViewerProgram.RunId+".log");if(File.Exists(file)&&new FileInfo(file).Length>262144)File.Move(file,file+".previous",true);File.AppendAllText(file,PolicyJson.Serialize(new{atUtc=DateTimeOffset.UtcNow,reason,pid=WorkerId,starts=Starts})+Environment.NewLine);}catch{}}
    void Reset(){if(process is null)return;try{if(!process.HasExited){process.Kill();process.WaitForExit(2000);}}catch(InvalidOperationException){}finally{process.Dispose();process=null;}}
    public void Dispose()=>Reset();
    internal static int Serve(bool fixture=false){
        string command;
        while((command=Console.ReadLine()) is not null){
            if(command!="probe"&&!(fixture&&command is "hang" or "crash"))return 2;
            using var watchdog=new Timer(_=>Environment.Exit(3),null,1900,Timeout.Infinite);
            if(fixture&&command=="hang")Thread.Sleep(Timeout.Infinite);
            if(fixture&&command=="crash")return 4;
            string response=fixture?ViewerSelfTest.FixtureJson():PolicyJson.Serialize(ManualBrowserProbe.ReadForeground());
            Console.WriteLine(response);Console.Out.Flush();
        }
        return 0;
    }
}
