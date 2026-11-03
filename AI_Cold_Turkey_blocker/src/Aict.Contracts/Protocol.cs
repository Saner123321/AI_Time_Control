using System.Buffers.Binary;
using System.Text;
using Aict.Core;

namespace Aict.Contracts;

public enum PreviewCommand { Status, SubmitPolicy, CancelPending, ExportJson, ExportCsv, RegisterApplication }
// No clock, usage, pending payload, filesystem path, or arbitrary command input.
public sealed record PreviewRequest {
    public int Version { get; init; } = 1;
    public PreviewCommand Command { get; init; }
    public string? EditToken { get; init; }
    public PolicyConfiguration? Policy { get; init; }
    public Guid? PendingId { get; init; }
    public ApplicationTarget? Application {get;init;}
    public void Validate() {
        if(Version!=1 || !Enum.IsDefined(Command)) throw new ArgumentException("Unsupported request version or command");
        bool submit=Command==PreviewCommand.SubmitPolicy,cancel=Command==PreviewCommand.CancelPending,register=Command==PreviewCommand.RegisterApplication;
        if(submit!=(Policy is not null) || cancel!=(PendingId is not null) || register!=(Application is not null) || (submit || cancel || register)!=(EditToken is not null)) throw new ArgumentException("Unexpected or missing request fields");
        if(EditToken is {} t && (t.Length!=64 || t.Any(c=>!char.IsAsciiHexDigit(c)))) throw new ArgumentException("Invalid edit token");
        if(PendingId==Guid.Empty) throw new ArgumentException("Invalid pending identity");
        if(Policy is {} p) PolicyValidation.Validate(p);
        Application?.Validate();
    }
}
public sealed record PreviewStatus(PolicyConfiguration Policy, PendingChange? Pending, string EditToken,
    DateTimeOffset EffectiveUtc, bool ClockConsistent, string? ClockAnomaly, ApplyStatus PendingStatus,
    bool StorageHealthy, string? Fault) {
    public System.Collections.Immutable.ImmutableArray<ApplicationTarget> Applications {get;init;}=[];
    public ExecutionState? Execution {get;init;}
    public string Mode => Execution?.Mode ?? "DEVELOPMENT_PREVIEW_NO_SYSTEM_ENFORCEMENT";
    public bool SystemEnforcementActive => Execution is {Verified:true} e && !e.VerifiedDeniedIdentities.IsEmpty;
    public bool TelemetryConnected => false;
}
public sealed record ExecutionState(string Mode,string DesiredPlanSha256,string? VerifiedPlanSha256,
    System.Collections.Immutable.ImmutableArray<string> VerifiedDeniedIdentities,DateTimeOffset? VerifiedAtUtc,string? Fault) {
    public bool Verified => Fault is null && VerifiedPlanSha256==DesiredPlanSha256;
}
public sealed record PreviewResponse(bool Ok, string Code, string Message, PreviewStatus? Status=null, string? ExportText=null);
public sealed record PreviewExport(string Mode, bool TelemetryConnected, string UsageNotice, EngineSnapshot State);

public static class PipeFrames {
    public const int MaxRequestBytes=256*1024,MaxResponseBytes=8*1024*1024;
    static readonly UTF8Encoding Utf8=new(false,true);
    public static async Task WriteAsync(Stream stream,string json,int maximum,CancellationToken cancellation=default) {
        byte[] bytes=Utf8.GetBytes(json);
        if(bytes.Length==0 || bytes.Length>maximum) throw new InvalidDataException("Frame exceeds the protocol bound");
        byte[] header=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(header,bytes.Length);
        await stream.WriteAsync(header,cancellation);await stream.WriteAsync(bytes,cancellation);await stream.FlushAsync(cancellation);
    }
    public static async Task<string> ReadAsync(Stream stream,int maximum,CancellationToken cancellation=default) {
        byte[] header=new byte[4];await stream.ReadExactlyAsync(header,cancellation);
        int count=BinaryPrimitives.ReadInt32LittleEndian(header);
        if(count<=0 || count>maximum) throw new InvalidDataException("Invalid frame length");
        byte[] bytes=new byte[count];await stream.ReadExactlyAsync(bytes,cancellation);return Utf8.GetString(bytes);
    }
}
