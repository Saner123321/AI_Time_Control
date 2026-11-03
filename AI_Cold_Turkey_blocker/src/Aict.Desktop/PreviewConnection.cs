using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using Aict.Contracts;
using Aict.Core;

namespace Aict.Desktop;

public sealed class PreviewConnection {
    sealed record Descriptor(string RunId,string Mode);
    readonly Descriptor? descriptor=ReadDescriptor();
    static Descriptor? ReadDescriptor() {
        var args=Environment.GetCommandLineArgs();int index=Array.IndexOf(args,"--vm-service");if(index<0) return null;
        if(index+1>=args.Length) throw new ArgumentException("缺少虚拟机连接描述文件。");
        var file=new FileInfo(args[index+1]);if(!file.Exists || file.Length>4096) throw new InvalidDataException("连接描述文件缺失或过大。");
        var value=PolicyJson.Parse<Descriptor>(File.ReadAllText(file.FullName));
        if(value.Mode!="VM_INTERACTIVE_APPLICATION_CONTROL" || !Guid.TryParseExact(value.RunId,"N",out _)) throw new InvalidDataException("连接描述不符合协议。");return value;
    }
    public bool IsService=>descriptor is not null;
    public string? ServiceRoot=>descriptor is null?null:Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"AICT-Interactive-"+descriptor.RunId);
    public string? DemoPath=>ServiceRoot is null?null:Path.Combine(ServiceRoot,"Targets","AictDemo.exe");
    public string BundleRoot { get; }=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,".."));
    public string DataDirectory => Path.Combine(BundleRoot,Environment.GetCommandLineArgs().Contains("--self-test")?"ui-self-test-data":"data");
    public string PipeName => descriptor is null?"AICT-preview-"+PolicyJson.Sha256(DataDirectory+WindowsIdentity.GetCurrent().User?.Value)[..32]:"AICT-workspace-"+descriptor.RunId;
    public async Task<PreviewResponse> CallAsync(PreviewRequest request) {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(4));
        using var pipe=new NamedPipeClientStream(".",PipeName,PipeDirection.InOut,PipeOptions.Asynchronous|(IsService?PipeOptions.None:PipeOptions.CurrentUserOnly),TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(timeout.Token);
        if(descriptor is not null) Aict.Service.ServicePeer.Verify(pipe,"AICT-Interactive-"+descriptor.RunId);
        await PipeFrames.WriteAsync(pipe,PolicyJson.Serialize(request),PipeFrames.MaxRequestBytes,timeout.Token);
        return PolicyJson.Parse<PreviewResponse>(await PipeFrames.ReadAsync(pipe,PipeFrames.MaxResponseBytes,timeout.Token));
    }
    public async Task EnsureStartedAsync() {
        if(IsService) {var response=await CallAsync(new());if(!response.Ok) throw new IOException(response.Message);return;}
        try {await CallAsync(new());return;} catch(Exception e) when(e is IOException or OperationCanceledException) { }
        string executable=Path.Combine(BundleRoot,"Backend","Aict.Backend.exe");
        if(!File.Exists(executable)) throw new FileNotFoundException("找不到后台预览程序，请先运行 Build-Preview.ps1。",executable);
        Directory.CreateDirectory(DataDirectory);
        var start=new ProcessStartInfo(executable) {UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=BundleRoot};
        start.ArgumentList.Add("--preview");start.ArgumentList.Add(DataDirectory);start.ArgumentList.Add(PipeName);
        using var process=Process.Start(start) ?? throw new IOException("无法启动后台预览。");
        for(int attempt=0;attempt<12;attempt++) {
            try {
                await CallAsync(new());
                if(!process.HasExited) File.WriteAllText(Path.Combine(DataDirectory,"backend-process.json"),PolicyJson.Serialize(new {
                    id=process.Id,startTimeUtc=process.StartTime.ToUniversalTime(),executable
                }));
                return;
            } catch(Exception e) when(e is IOException or OperationCanceledException) {
                if(process.HasExited) throw new IOException("后台未能打开预览数据。请保留 data 文件夹；不要删除数据库后重试。");
                await Task.Delay(150);
            }
        }
        throw new IOException("后台连接超时。尚未保存规则。");
    }
}
