using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Aict.Contracts;
using Aict.Core;

namespace Aict.Service;

internal sealed record ServiceQuery(int Version=1,string Command="Status");
internal sealed record LabServiceStatus(string RunId,string InstanceId,int ProcessId,bool ClockTrusted,string ClockState,
    bool DesiredBlocked,bool? PolicyReadbackBlocked,DateTimeOffset? LastVerifiedUtc,string? Fault,long StorageRevision,
    string PolicySha256,Guid? PendingId,string? PendingTargetSha256,DateTimeOffset? PendingNotBeforeUtc,
    DateTimeOffset EffectiveUtc,long? CycleStartTick,long ClockFrequency) {
    public string Mode => "VM_FIXTURE_INTEGRATION_NOT_PRODUCTION";
    public bool TelemetryConnected => false;
}
internal sealed record ServiceReply(bool Ok,string Code,LabServiceStatus? Status=null);
internal static class ServiceStatusPipe {
    internal static string Name(LabConfiguration config) => "AICT-service-policy-"+config.RunId;
    internal static PipeSecurity Security(string sid) {
        var security=new PipeSecurity();security.SetAccessRuleProtection(true,false);
        var system=new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null);security.SetOwner(system);security.SetGroup(system);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid,null),PipeAccessRights.FullControl,AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(system,PipeAccessRights.FullControl,AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid),PipeAccessRights.ReadWrite,AccessControlType.Allow));
        return security;
    }
    internal static async Task RunAsync(LabConfiguration config,Func<LabServiceStatus> getStatus,CancellationToken cancellation) {
        var clients=new List<Task>();
        NamedPipeServerStream Create(bool first) => NamedPipeServerStreamAcl.Create(Name(config),PipeDirection.InOut,5,PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous|(first?PipeOptions.FirstPipeInstance:PipeOptions.None),8192,8192,Security(config.ProbeSid));
        NamedPipeServerStream? waiting=null;
        try {
            waiting=Create(true);
            while(!cancellation.IsCancellationRequested) {
                clients.RemoveAll(t=>t.IsCompleted);if(clients.Count>=4) {await Task.WhenAny(clients);continue;}
                await waiting.WaitForConnectionAsync(cancellation);
                var pipe=waiting;
                // Keep an owned instance open throughout replacement, so an ordinary client cannot squat the pipe name.
                waiting=Create(false);
                clients.Add(Serve(pipe));
            }
        } catch(OperationCanceledException) when(cancellation.IsCancellationRequested) { }
        finally {waiting?.Dispose();await Task.WhenAll(clients);}
        async Task Serve(NamedPipeServerStream pipe) {
            using(pipe) using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation)) {
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                try {
                    string json=await PipeFrames.ReadAsync(pipe,8192,timeout.Token);string? sid=null;
                    pipe.RunAsClient(()=>{using var identity=WindowsIdentity.GetCurrent();sid=identity.User?.Value;});
                    ServiceReply reply;
                    if(sid!=config.ProbeSid && sid!="S-1-5-18") reply=new(false,"UNAUTHORIZED");
                    else {try {
                        var query=PolicyJson.Parse<ServiceQuery>(json);
                        reply=query.Version==1 && query.Command=="Status"?new(true,"OK",getStatus()):new(false,"UNSUPPORTED_REQUEST");
                    } catch(Exception e) when(e is ArgumentException or System.Text.Json.JsonException or InvalidDataException) {reply=new(false,"INVALID_REQUEST");}}
                    await PipeFrames.WriteAsync(pipe,PolicyJson.Serialize(reply),512*1024,timeout.Token);
                } catch(Exception e) when(e is IOException or InvalidDataException or OperationCanceledException or UnauthorizedAccessException or System.Text.DecoderFallbackException) { }
            }
        }
    }
}
