using System.IO;
using System.IO.Pipes;
using Aict.Contracts;
namespace Aict.Beta.Desktop;
internal sealed class DesktopInstance:IDisposable {
    readonly Mutex mutex;readonly string name;readonly CancellationTokenSource stop=new();Task? worker;int disposed;
    public bool IsPrimary {get;}
    public DesktopInstance(string name){this.name=name;mutex=new(false,"Local\\"+name);try{IsPrimary=mutex.WaitOne(0);}catch(AbandonedMutexException){IsPrimary=true;}}
    public void Listen(Func<string,Task> activate){if(!IsPrimary||worker is not null)throw new InvalidOperationException("Activation owner mismatch");worker=Task.Run(async()=>{
        while(!stop.IsCancellationRequested)try{
            using var server=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
            await server.WaitForConnectionAsync(stop.Token);using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);timeout.CancelAfter(3000);
            string command=await PipeFrames.ReadAsync(server,32,timeout.Token);if(command is not ("show" or "uninstall"))continue;
            await activate(command);await PipeFrames.WriteAsync(server,"OK",32,timeout.Token);
        }catch(OperationCanceledException)when(stop.IsCancellationRequested){break;}catch(OperationCanceledException){}catch(Exception){try{await Task.Delay(100,stop.Token);}catch(OperationCanceledException){break;}}
    });}
    public async Task Activate(string command){using var timeout=new CancellationTokenSource(3000);using var client=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);await client.ConnectAsync(timeout.Token);await PipeFrames.WriteAsync(client,command,32,timeout.Token);if(await PipeFrames.ReadAsync(client,32,timeout.Token)!="OK")throw new IOException("Activation was not confirmed");}
    public void Dispose(){if(Interlocked.Exchange(ref disposed,1)!=0)return;stop.Cancel();if(IsPrimary)mutex.ReleaseMutex();mutex.Dispose();if(worker is null)stop.Dispose();else _=worker.ContinueWith(_=>stop.Dispose(),TaskScheduler.Default);}
}
