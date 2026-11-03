using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using Aict.Contracts;
using Aict.Core;

if(args.Length!=2) return 2;
string backend=Path.GetFullPath(args[0]),output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);
string pipeName="AICT-preview-smoke-"+Guid.NewGuid().ToString("N");
Process? process=null;var observations=new List<string>();
try {
    await Start();
    var first=await Call(new());Check(first.Status?.SystemEnforcementActive==false,"live status is explicitly unenforced");
    var applied=await Call(new() {Command=PreviewCommand.SubmitPolicy,EditToken=first.Status!.EditToken,Policy=new()});
    Check(applied.Ok && applied.Code=="APPLIED","first UI-like full submission persisted");
    var queued=await Call(new() {Command=PreviewCommand.SubmitPolicy,EditToken=applied.Status!.EditToken,Policy=new() {ChangeDelay=TimeSpan.Zero}});
    Check(queued.Ok && queued.Code=="QUEUED" && queued.Status!.Pending is not null,"next modification is frozen for old 24h delay");
    Guid frozen=queued.Status!.Pending!.Id;string token=queued.Status.EditToken;
    var stale=await Call(new() {Command=PreviewCommand.CancelPending,EditToken=first.Status.EditToken,PendingId=frozen});Check(!stale.Ok && stale.Code=="STALE_EDIT","live pipe rejects stale cancellation");
    var injected=await Raw("{\"command\":\"Status\",\"clock\":{\"trusted\":true},\"usage\":{}}");Check(!injected.Ok && injected.Code=="INVALID_REQUEST","live pipe rejects injected clock and usage");
    var malformed=await Raw("not-json");Check(!malformed.Ok,"malformed JSON rejected without killing server");
    using(var client=await Connect()) {
        byte[] header=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(header,PipeFrames.MaxRequestBytes+1);await client.WriteAsync(header);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));byte[] one=new byte[1];
        bool closed=false;try {closed=await client.ReadAsync(one,timeout.Token)==0;} catch(IOException) {closed=true;}
        Check(closed,"oversized frame disconnected before body allocation");
    }
    Check((await Call(new())).Status!.EditToken==token,"invalid clients did not mutate rules or pending payload");
    var exported=await Call(new() {Command=PreviewCommand.ExportJson});Check(exported.Ok && PolicyJson.Parse<PreviewExport>(exported.ExportText!).State.Pending?.Id==frozen,"export reflects frozen payload");
    process!.Kill();await process.WaitForExitAsync();process.Dispose();process=null;
    await Start();var restored=await Call(new());Check(restored.Status!.Pending?.Id==frozen,"process restart retains pending identity and rules");
    var cancelled=await Call(new() {Command=PreviewCommand.CancelPending,EditToken=restored.Status.EditToken,PendingId=frozen});
    Check(cancelled.Ok && cancelled.Status!.Pending is null && cancelled.Status.Policy.ChangeDelay==TimeSpan.FromHours(24),"cancel retains original rules");
    File.WriteAllText(Path.Combine(output,"result.json"),PolicyJson.Serialize(new {passed=true,mode="DEVELOPMENT_PREVIEW_NO_SYSTEM_ENFORCEMENT",observations}));
    Console.WriteLine($"Named-pipe smoke: {observations.Count} observations passed. Evidence: {output}");return 0;
} catch(Exception e) {
    File.WriteAllText(Path.Combine(output,"failure.json"),PolicyJson.Serialize(new {passed=false,error=e.ToString(),observations}));Console.Error.WriteLine(e);return 1;
} finally {if(process is not null) {if(!process.HasExited) {process.Kill();await process.WaitForExitAsync();}process.Dispose();}}

void Check(bool condition,string observation) {if(!condition) throw new InvalidOperationException(observation);observations.Add(observation);}
async Task Start() {
    var start=new ProcessStartInfo(backend) {UseShellExecute=false,CreateNoWindow=true};start.ArgumentList.Add("--preview");start.ArgumentList.Add(Path.Combine(output,"data"));start.ArgumentList.Add(pipeName);
    process=Process.Start(start) ?? throw new IOException("Backend did not launch");
    for(int i=0;i<30;i++) {try {using var ready=await Connect();return;} catch(Exception e) when(e is TimeoutException or IOException or OperationCanceledException) {if(process.HasExited) throw new IOException("Backend exited before readiness");await Task.Delay(100);}}
    throw new TimeoutException("Backend readiness timeout");
}
async Task<NamedPipeClientStream> Connect() {
    var client=new NamedPipeClientStream(".",pipeName,PipeDirection.InOut,PipeOptions.Asynchronous);
    try {await client.ConnectAsync(400);return client;} catch {client.Dispose();throw;}
}
Task<PreviewResponse> Call(PreviewRequest request) => Raw(PolicyJson.Serialize(request));
async Task<PreviewResponse> Raw(string request) {
    using var client=await Connect();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await PipeFrames.WriteAsync(client,request,PipeFrames.MaxRequestBytes,timeout.Token);
    return PolicyJson.Parse<PreviewResponse>(await PipeFrames.ReadAsync(client,PipeFrames.MaxResponseBytes,timeout.Token));
}
