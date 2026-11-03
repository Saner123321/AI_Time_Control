using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using Aict.Core;
using Aict.Storage;
using Aict.Contracts;
namespace Aict.Beta.Desktop;
public sealed class BetaConnection {
    readonly Authority? simulation;
    public string BundleRoot {get;}
    public string? Root {get;}
    public bool Simulated=>simulation is not null;
    public bool IsService=>true;
    public string DataDirectory=>Root??Path.Combine(BundleRoot,"ui-self-test-data");
    public string? DemoPath=>Root is null?null:Path.Combine(Root,@"Targets\AictDemo.exe");
    public BetaConnection(string root){Root=root;BundleRoot=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,".."));}
    public BetaConnection() {BundleRoot=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,".."));string path=Path.Combine(BundleRoot,"ui-self-test-data");Directory.CreateDirectory(path);var clock=new TestClock();simulation=new Authority(StateRepository.CreateNew(Path.Combine(path,"engine.sqlite"),Authority.Initial(clock)),clock,[]);}
    public async Task<Response> CallAsync(Request request){if(simulation is not null)return await simulation.Handle(request);string id=Path.GetFileName(Root!)[10..];using var timeout=new CancellationTokenSource(5000);using var pipe=new NamedPipeClientStream(".","AICT-beta-"+id,PipeDirection.InOut,PipeOptions.Asynchronous,TokenImpersonationLevel.Identification);await pipe.ConnectAsync(timeout.Token);Aict.Service.ServicePeer.Verify(pipe,"AICT-Beta-"+id);await PipeFrames.WriteAsync(pipe,PolicyJson.Serialize(request),PipeFrames.MaxRequestBytes,timeout.Token);return PolicyJson.Parse<Response>(await PipeFrames.ReadAsync(pipe,PipeFrames.MaxResponseBytes,timeout.Token));}
    public async Task EnsureStartedAsync(){var r=await CallAsync(new());if(!r.Ok)throw new IOException(r.Message);}
    sealed class TestClock:IClock {public string BootId=>"ui-self-test";public long Uptime=>Environment.TickCount64;public long AwakeMilliseconds=>Environment.TickCount64;public DateTimeOffset UtcNow=>DateTimeOffset.UtcNow;}
}
