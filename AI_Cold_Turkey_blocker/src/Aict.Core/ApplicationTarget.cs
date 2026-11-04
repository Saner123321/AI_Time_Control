namespace Aict.Core;

// Identity/metadata only. No caller-supplied path is opened by a privileged service.
public sealed record ApplicationTarget(string Id,string Name,string FileName,long Size) {
    public const string Prefix="authenticode:sha256:";
    public void Validate() {
        if(Id is null || !Id.StartsWith(Prefix,StringComparison.Ordinal) || Id.Length!=Prefix.Length+64 || Id[Prefix.Length..].Any(c=>!char.IsAsciiHexDigit(c)) || Id[Prefix.Length..]!=Id[Prefix.Length..].ToUpperInvariant()) throw new ArgumentException("Invalid PE SIP SHA256 identity");
        if(string.IsNullOrWhiteSpace(Name) || Name.Length>200 || Name.Any(char.IsControl) || string.IsNullOrWhiteSpace(FileName) || FileName.Length>200 || FileName.Any(c=>char.IsControl(c) || "\\/:*?\"<>|".Contains(c)) || !FileName.EndsWith(".exe",StringComparison.OrdinalIgnoreCase) || Size<=0 || Size>4L*1024*1024*1024) throw new ArgumentException("Invalid application metadata");
    }
}
// Saved atomically with rules. A matching OS boot identity plus monotonic uptime verifies continuity after a service crash.
public sealed record ServiceTimeAnchor(string BootId,long UptimeMilliseconds,bool ClockWasTrusted) {
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public long? AwakeMilliseconds {get;init;}
}
