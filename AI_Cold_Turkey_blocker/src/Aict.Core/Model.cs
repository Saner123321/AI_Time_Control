using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aict.Core;

[Flags] public enum Weekdays { None=0, Monday=1, Tuesday=2, Wednesday=4, Thursday=8, Friday=16, Saturday=32, Sunday=64, All=127 }
public enum TargetMode { None, BlockSelected, AllowSelected }
public enum BlockReason { Always, WeeklySchedule, Cycle, QuotaExhausted, UsageGap, ClockUntrusted, TimedBlock }
public enum ChangePurpose { ReplaceConfiguration, Uninstall }
public enum ApplyStatus { NoPendingChange, WaitingForDelay, Locked, Applied, PolicyConflict }

// Minute intervals are half-open. EndMinute=1440 expresses 24:00; equal ends are rejected.
public sealed record WeeklyWindow(Weekdays Days, int StartMinute, int EndMinute);
public sealed record FixedCycle(DateTimeOffset AnchorUtc, TimeSpan BlockFor, TimeSpan RestFor);
public sealed record QuotaWindow(int StartMinute,int EndMinute);
public sealed record QuotaSlot(Guid Id,int StartMinute,int EndMinute,TimeSpan Limit,string Name="");
public sealed record RuleGroup {
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public TargetMode ApplicationMode { get; init; }
    public TargetMode WebsiteMode { get; init; }
    public ImmutableArray<string> ApplicationIds { get; init; } = [];
    public ImmutableArray<string> WebsiteDomains { get; init; } = [];
    public ImmutableArray<string> ApplicationExceptions { get; init; } = [];
    public ImmutableArray<string> WebsiteExceptions { get; init; } = [];
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public ImmutableArray<WebsiteExceptionDetail>? WebsiteExceptionDetails {get;init;}
    public bool AlwaysBlock { get; init; }
    public ImmutableArray<WeeklyWindow> Windows { get; init; } = [];
    public FixedCycle? Cycle { get; init; }
    public TimeSpan? DailyQuota { get; init; }
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public QuotaWindow? DailyQuotaWindow {get;init;}
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public ImmutableArray<QuotaSlot>? QuotaSlots {get;init;}
    public bool LockWhileBlocking { get; init; }
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public DateTimeOffset? BlockUntilUtc {get;init;}
}
public sealed record PolicyConfiguration {
    public int SchemaVersion { get; init; } = 1;
    public string TimeZoneId { get; init; } = "Asia/Shanghai";
    public TimeSpan ChangeDelay { get; init; } = TimeSpan.FromHours(24);
    public ImmutableArray<RuleGroup> Groups { get; init; } = [];
}
public sealed record ActivityContext(string? ApplicationId=null, string? WebsiteDomain=null) {
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? WebsiteUrl {get;init;}
}
public sealed record DailyUsage(long KnownTicks=0, bool HasGap=false) {
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingDefault)] public long GapChargedTicks {get;init;}
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingDefault)] public bool GapChargeComplete {get;init;}
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingDefault)] public bool GapEstimated {get;init;}
    [JsonIgnore] public long ChargedTicks=>checked(KnownTicks+GapChargedTicks);
    [JsonIgnore] public bool UnresolvedGap=>HasGap&&!GapChargeComplete;
}
public sealed record UsageLedger {
    public ImmutableDictionary<string,DailyUsage> Accounts { get; init; } = ImmutableDictionary<string,DailyUsage>.Empty;
    public DateTimeOffset? LastAccountedUntilUtc { get; init; }
    public DailyUsage Get(Guid group, DateOnly date,Guid? slot=null) => Accounts.GetValueOrDefault(Key(group,date,slot)) ?? new();
    public UsageLedger Put(Guid group, DateOnly date, DailyUsage usage,Guid? slot=null) => this with { Accounts=Accounts.SetItem(Key(group,date,slot),usage) };
    public static string Key(Guid group, DateOnly day,Guid? slot=null) => $"{group:N}/{day:yyyy-MM-dd}"+(slot is {} id?$"/{id:N}":"");
}
public sealed record UsageSlice(DateTimeOffset StartUtc, TimeSpan Duration, ActivityContext Context,
    bool Foreground=true, bool SessionLocked=false, bool Suspended=false, TimeSpan IdleAtStart=default, bool ObservationKnown=true);
public sealed record GroupDecision(Guid GroupId, ImmutableArray<BlockReason> Reasons, TimeSpan? QuotaRemaining) {
    public bool Blocked => !Reasons.IsEmpty;
}
public sealed record Evaluation(ImmutableArray<GroupDecision> Groups) { public bool Blocked => Groups.Any(g=>g.Blocked); }
public sealed record ClockReading(DateTimeOffset EffectiveUtc, bool Trusted=true);
public sealed record RuntimeClock(DateTimeOffset EffectiveUtc, bool Trusted, string? Anomaly=null) {
    // Wall time is diagnostic only while running. Repeated small edits cannot accelerate the clock.
    public RuntimeClock Advance(DateTimeOffset observedWallUtc, TimeSpan monotonicElapsed) {
        if(monotonicElapsed<TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(monotonicElapsed));
        var projected=EffectiveUtc+monotonicElapsed;
        bool anomaly=(observedWallUtc-projected).Duration()>TimeSpan.FromMinutes(2);
        return new(projected,Trusted && !anomaly,anomaly?"WALL_CLOCK_DIVERGED":Anomaly);
    }
    public RuntimeClock AfterRestartWithoutTrustedAnchor() => this with { Trusted=false, Anomaly="RESTART_TIME_UNVERIFIED" };
    // Only the backend's independently verified time source may supply this anchor.
    public RuntimeClock ReconcileVerifiedAnchor(DateTimeOffset verifiedUtc) => verifiedUtc<EffectiveUtc
        ? this with {Trusted=false,Anomaly="VERIFIED_TIME_BEHIND_HIGHWATER"}
        : new(verifiedUtc,true);
    [JsonIgnore] public ClockReading Reading => new(EffectiveUtc,Trusted);
}
public sealed record PendingChange(Guid Id, ChangePurpose Purpose, string BasePolicySha256, string TargetJson, string TargetSha256,
    DateTimeOffset SubmittedUtc, DateTimeOffset NotBeforeUtc) {
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingDefault)] public bool ApplyAtDeadline {get;init;}
}
public sealed record EngineSnapshot {
    public int SchemaVersion { get; init; } = 1;
    public PolicyConfiguration Policy { get; init; } = new();
    public UsageLedger Usage { get; init; } = new();
    public PendingChange? Pending { get; init; }
    public RuntimeClock Clock { get; init; } = new(DateTimeOffset.UnixEpoch,false);
    public System.Collections.Immutable.ImmutableArray<ApplicationTarget> Applications {get;init;}=[];
    public ServiceTimeAnchor? ServiceAnchor {get;init;}
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingDefault)] public bool UninstallReady {get;init;}
}
public sealed record ApplyResult(ApplyStatus Status, EngineSnapshot Snapshot, bool UninstallAuthorized=false);

public static class PolicyJson {
    static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy=JsonNamingPolicy.CamelCase, UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow, Converters={new JsonStringEnumConverter()} };
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value,Options);
    public static string Sha256(string json) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    public static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json,Options) ?? throw new InvalidDataException("Missing JSON value");
    public static string Fingerprint(PolicyConfiguration policy) => Sha256(Serialize(policy));
}

public static class PolicyValidation {
    public static string Domain(string domain) {
        if(string.IsNullOrWhiteSpace(domain) || domain.Any(c=>char.IsWhiteSpace(c) || char.IsControl(c)) || domain.IndexOfAny(['/',':','?','#','@','\\','*'])>=0)
            throw new ArgumentException("A domain without scheme, path, wildcard or credentials is required");
        string ascii=new IdnMapping().GetAscii(domain.TrimEnd('.')).ToLowerInvariant();
        if(ascii.Length>253 || ascii.Split('.').Any(label=>label.Length is <1 or >63 || label[0]=='-' || label[^1]=='-' || label.Any(c=>!char.IsAsciiLetterOrDigit(c) && c!='-')))
            throw new ArgumentException("Invalid domain");
        return ascii;
    }
    public static TimeZoneInfo Zone(PolicyConfiguration policy) {
        // First supported calendars: no DST transitions. Do not silently claim arbitrary-zone coverage.
        if(policy.TimeZoneId is not ("Asia/Shanghai" or "China Standard Time" or "UTC")) throw new ArgumentException("This increment supports Shanghai and UTC only");
        return TimeZoneInfo.FindSystemTimeZoneById(policy.TimeZoneId);
    }
    public static void Validate(PolicyConfiguration policy) {
        ArgumentNullException.ThrowIfNull(policy);
        if(policy.SchemaVersion!=1 || policy.Groups.IsDefault || policy.Groups.Length>128 || policy.ChangeDelay<TimeSpan.Zero || policy.ChangeDelay>TimeSpan.FromDays(365)) throw new ArgumentException("Invalid policy schema, size or delay");
        _=Zone(policy);
        if(policy.Groups.Any(g=>g is null) || policy.Groups.Select(g=>g.Id).Distinct().Count()!=policy.Groups.Length) throw new ArgumentException("Missing or duplicate group identity");
        foreach(var g in policy.Groups) {
            if(g.Id==Guid.Empty || string.IsNullOrWhiteSpace(g.Name) || g.Name.Length>200 || !Enum.IsDefined(g.ApplicationMode) || !Enum.IsDefined(g.WebsiteMode)) throw new ArgumentException("Invalid group identity or mode");
            foreach(var ids in new[]{g.ApplicationIds,g.ApplicationExceptions})
                if(ids.IsDefault || ids.Length>512 || ids.Any(s=>string.IsNullOrWhiteSpace(s) || s.Length>256 || s!=s.Trim() || s.Any(char.IsControl))) throw new ArgumentException("Invalid target collection");
            foreach(var sites in new[]{g.WebsiteDomains,g.WebsiteExceptions}) {
                if(sites.IsDefault || sites.Length>512)throw new ArgumentException("每组网站列表最多512条。");
                foreach(string d in sites)_=WebsitePattern.Normalize(d);
            }
            ExceptionCatalog.Validate(g);
            if(g.Enabled && !(g.ApplicationMode==TargetMode.AllowSelected || g.WebsiteMode==TargetMode.AllowSelected || (g.ApplicationMode==TargetMode.BlockSelected && !g.ApplicationIds.IsEmpty) || (g.WebsiteMode==TargetMode.BlockSelected && !g.WebsiteDomains.IsEmpty))) throw new ArgumentException("Enabled group needs a target scope");
            if(g.Windows.IsDefault || g.Windows.Length>128 || g.Windows.Any(w=>w is null)) throw new ArgumentException("Invalid weekly windows");
            foreach(var w in g.Windows)
                if(w.Days==Weekdays.None || (w.Days & ~Weekdays.All)!=0 || w.StartMinute is <0 or >=1440 || w.EndMinute is <0 or >1440 || w.StartMinute==w.EndMinute) throw new ArgumentException("Invalid half-open weekly interval");
            if(g.Cycle is {} c && (c.BlockFor<=TimeSpan.Zero || c.RestFor<=TimeSpan.Zero || c.BlockFor>TimeSpan.FromDays(365) || c.RestFor>TimeSpan.FromDays(365))) throw new ArgumentException("Invalid fixed cycle");
            if(g.DailyQuota is {} q && (q<TimeSpan.Zero || q>TimeSpan.FromDays(1))) throw new ArgumentException("Invalid daily quota");
            if(g.DailyQuotaWindow is {} qw && (g.DailyQuota is null || qw.StartMinute is <0 or >=1440 || qw.EndMinute is <0 or >1440 || qw.StartMinute==qw.EndMinute))throw new ArgumentException("额度时段使用有效的开始和结束时间；结束可填24:00，早于开始表示跨午夜。");
            QuotaSchedules.Validate(g);
            if(g.Enabled && !g.AlwaysBlock && g.Windows.IsEmpty && g.Cycle is null && !QuotaSchedules.HasQuota(g) && g.BlockUntilUtc is null) throw new ArgumentException("Enabled group needs an activation rule");
        }
    }
}
