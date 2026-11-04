using System.Diagnostics;
namespace Aict.Beta.Service;
// Independent observer: report evidence of a stalled loop, without declaring a root cause.
internal sealed class ServiceDiagnostics:IDisposable {
    readonly System.Threading.Timer timer;string phase="starting";long since=Environment.TickCount64,lastReport=-10000;int checking;
    public ServiceDiagnostics(){timer=new(_=>Check(),null,1000,1000);}
    public void Phase(string value){Volatile.Write(ref since,Environment.TickCount64);Volatile.Write(ref phase,value);}
    void Check(){if(Interlocked.Exchange(ref checking,1)!=0)return;try{long now=Environment.TickCount64,age=now-Volatile.Read(ref since);string current=Volatile.Read(ref phase);if(current=="waiting"||age<2500||now-lastReport<10000)return;lastReport=now;using var p=Process.GetCurrentProcess();Env.Log("ServicePhaseDelayed",new{phase=current,elapsedMs=age,processCpuMs=p.TotalProcessorTime.TotalMilliseconds,threadPoolPending=ThreadPool.PendingWorkItemCount,threadPoolThreads=ThreadPool.ThreadCount});}catch{}finally{Volatile.Write(ref checking,0);}}
    public static void ObserveCollectors(){try{var rows=new List<object>();foreach(var p in Process.GetProcessesByName("Aict.Beta.Agent"))using(p)try{if(string.Equals(p.MainModule?.FileName,Path.Combine(Env.Root,@"Agent\Aict.Beta.Agent.exe"),StringComparison.OrdinalIgnoreCase))rows.Add(new{pid=p.Id,sessionId=p.SessionId,cpuMs=p.TotalProcessorTime.TotalMilliseconds});}catch{}Env.Log("CollectorProcessesObserved",new{processes=rows,roles="collector_or_browser_native_host",causeConfirmed=false});}catch{}}
    public void Dispose(){timer.Dispose();}
}
