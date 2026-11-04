using System.Collections.Immutable;

namespace Aict.Core;

public static class RuleEngine {
    public static readonly TimeSpan IdleThreshold=TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MaximumObservedSlice=TimeSpan.FromMinutes(5);
    public static bool HasApplicationScope(RuleGroup g)=>g.ApplicationMode==TargetMode.AllowSelected || g.ApplicationMode==TargetMode.BlockSelected&&!g.ApplicationIds.IsEmpty;
    public static bool HasWebsiteScope(RuleGroup g)=>g.WebsiteMode==TargetMode.AllowSelected || g.WebsiteMode==TargetMode.BlockSelected&&!g.WebsiteDomains.IsEmpty;
    public static bool HasScopedQuota(RuleGroup g)=>g.Enabled&&QuotaSchedules.HasQuota(g)&&(HasApplicationScope(g)||HasWebsiteScope(g));
    public static DateOnly Day(PolicyConfiguration policy,DateTimeOffset utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc,PolicyValidation.Zone(policy)).DateTime);
    public static bool WeeklyActive(WeeklyWindow window,DateTime local) {
        var today=(Weekdays)(1 << (((int)local.DayOfWeek+6)%7));
        long tick=local.TimeOfDay.Ticks,start=window.StartMinute*TimeSpan.TicksPerMinute,end=window.EndMinute*TimeSpan.TicksPerMinute;
        if(end>start) return (window.Days&today)!=0 && tick>=start && tick<end;
        var prior=(Weekdays)(1 << (((int)local.AddDays(-1).DayOfWeek+6)%7));
        return ((window.Days&today)!=0 && tick>=start) || ((window.Days&prior)!=0 && tick<end);
    }
    public static bool CycleActive(FixedCycle cycle,DateTimeOffset utc) => utc>=cycle.AnchorUtc && (utc-cycle.AnchorUtc).Ticks%(cycle.BlockFor+cycle.RestFor).Ticks<cycle.BlockFor.Ticks;
    static bool InIds(ImmutableArray<string> ids,string value) => ids.Any(id=>string.Equals(id,value,StringComparison.OrdinalIgnoreCase));
    static bool InDomains(ImmutableArray<string> domains,string domain) => domains.Any(value=>{
        string root=PolicyValidation.Domain(value);return domain==root || domain.EndsWith('.'+root,StringComparison.Ordinal);
    });
    public static bool Matches(RuleGroup group,ActivityContext context,IReadOnlySet<string>? essentialApplications=null) {
        bool app=false,site=false;
        if(context.ApplicationId is {} id && group.ApplicationMode!=TargetMode.None && !(essentialApplications?.Any(e=>string.Equals(e,id,StringComparison.OrdinalIgnoreCase)) ?? false)) {
            app=group.ApplicationMode==TargetMode.BlockSelected?InIds(group.ApplicationIds,id):!InIds(group.ApplicationIds,id);
            if(InIds(group.ApplicationExceptions,id)) app=false;
        }
        if(context.WebsiteDomain is {} raw && group.WebsiteMode!=TargetMode.None) {
            string domain=PolicyValidation.Domain(raw);
            string address=context.WebsiteUrl ?? domain;
            site=group.WebsiteMode==TargetMode.BlockSelected?WebsitePattern.In(group.WebsiteDomains,address):!WebsitePattern.In(group.WebsiteDomains,address);
            if(WebsitePattern.In(group.WebsiteExceptions,address)) site=false;
        }
        return app || site;
    }
    static ImmutableArray<BlockReason> Reasons(PolicyConfiguration policy,RuleGroup group,UsageLedger usage,ClockReading clock,bool telemetryHealthy) {
        var reasons=ImmutableArray.CreateBuilder<BlockReason>();
        if(!clock.Trusted) reasons.Add(BlockReason.ClockUntrusted);
        if(group.AlwaysBlock) reasons.Add(BlockReason.Always);
        if(group.BlockUntilUtc is {} until && clock.EffectiveUtc<until) reasons.Add(BlockReason.TimedBlock);
        var local=TimeZoneInfo.ConvertTime(clock.EffectiveUtc,PolicyValidation.Zone(policy)).DateTime;
        if(group.Windows.Any(w=>WeeklyActive(w,local))) reasons.Add(BlockReason.WeeklySchedule);
        if(group.Cycle is {} cycle && CycleActive(cycle,clock.EffectiveUtc)) reasons.Add(BlockReason.Cycle);
        foreach(var quota in QuotaSchedules.Specs(group).Where(q=>QuotaPeriods.At(policy,q,clock.EffectiveUtc).Contains(clock.EffectiveUtc))) {
            var period=QuotaPeriods.At(policy,quota,clock.EffectiveUtc);var account=usage.Get(group.Id,period.Day,quota.Id);
            if(account.UnresolvedGap) reasons.Add(BlockReason.UsageGap);
            else if(account.ChargedTicks>=quota.Limit.Ticks) reasons.Add(BlockReason.QuotaExhausted);
        }
        return reasons.ToImmutable();
    }
    public static Evaluation Evaluate(PolicyConfiguration policy,UsageLedger usage,ActivityContext context,ClockReading clock,bool telemetryHealthy=true,IReadOnlySet<string>? essentialApplications=null) {
        PolicyValidation.Validate(policy);
        var decisions=ImmutableArray.CreateBuilder<GroupDecision>();
        foreach(var g in policy.Groups.Where(g=>g.Enabled && Matches(g,context,essentialApplications))) {
            TimeSpan? remaining=null;
            foreach(var quota in QuotaSchedules.Specs(g).Where(q=>QuotaPeriods.At(policy,q,clock.EffectiveUtc).Contains(clock.EffectiveUtc))) {
                var period=QuotaPeriods.At(policy,quota,clock.EffectiveUtc);var account=usage.Get(g.Id,period.Day,quota.Id);
                if(clock.Trusted && !account.UnresolvedGap) remaining=TimeSpan.FromTicks(Math.Max(0,quota.Limit.Ticks-account.ChargedTicks));
            }
            decisions.Add(new(g.Id,Reasons(policy,g,usage,clock,telemetryHealthy),remaining));
        }
        return new(decisions.ToImmutable());
    }
    public static ImmutableArray<BlockReason> ActivationReasons(PolicyConfiguration policy,RuleGroup group,UsageLedger usage,ClockReading clock,bool telemetryHealthy=true) => Reasons(policy,group,usage,clock,telemetryHealthy);
    public static bool IsModificationLocked(PolicyConfiguration policy,UsageLedger usage,ClockReading clock,bool telemetryHealthy=true) {
        PolicyValidation.Validate(policy);
        return !clock.Trusted || policy.Groups.Any(g=>g.Enabled && g.LockWhileBlocking && !Reasons(policy,g,usage,clock,telemetryHealthy).IsEmpty);
    }
    public static UsageLedger Account(PolicyConfiguration policy,UsageLedger ledger,UsageSlice slice,IReadOnlySet<string>? essentialApplications=null) {
        PolicyValidation.Validate(policy);
        if(slice.Duration<TimeSpan.Zero || slice.Duration>TimeSpan.FromDays(31) || slice.IdleAtStart<TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(slice));
        var until=slice.StartUtc+slice.Duration;
        if(ledger.LastAccountedUntilUtc is {} last && until<=last) return ledger; // duplicate/replay is idempotent
        var begin=ledger.LastAccountedUntilUtc is {} previous && previous>slice.StartUtc?previous:slice.StartUtc;
        if(slice.ObservationKnown && (!slice.Foreground || slice.SessionLocked || slice.Suspended)) return ledger with {LastAccountedUntilUtc=until};
        if(!slice.ObservationKnown || slice.Duration>MaximumObservedSlice) {
            // Charge only the intersecting unobserved time, once per rule/slot.
            // Keep it separate from measured use; a gap is no longer an entire-period lock.
            if(until>begin) foreach(var g in policy.Groups.Where(HasScopedQuota))
                foreach(var quota in QuotaSchedules.Specs(g))foreach(var p in QuotaPeriods.Intersections(policy,quota,begin,until)){
                    var a=ledger.Get(g.Id,p.Day,quota.Id);ledger=ledger.Put(g.Id,p.Day,a with {HasGap=true,GapChargedTicks=checked(a.GapChargedTicks+(p.EndUtc-p.StartUtc).Ticks),GapChargeComplete=!a.UnresolvedGap},quota.Id);
                }
            return ledger with {LastAccountedUntilUtc=until};
        }
        var activeUntil=slice.StartUtc+TimeSpan.FromTicks(Math.Min(slice.Duration.Ticks,Math.Max(0,(IdleThreshold-slice.IdleAtStart).Ticks)));
        foreach(var g in policy.Groups.Where(g=>g.Enabled && QuotaSchedules.HasQuota(g) && Matches(g,slice.Context,essentialApplications)))
            foreach(var quota in QuotaSchedules.Specs(g))foreach(var p in QuotaPeriods.Intersections(policy,quota,begin,activeUntil)){
                var account=ledger.Get(g.Id,p.Day,quota.Id);ledger=ledger.Put(g.Id,p.Day,account with{KnownTicks=checked(account.KnownTicks+(p.EndUtc-p.StartUtc).Ticks)},quota.Id);
            }
        return ledger with {LastAccountedUntilUtc=until};
    }
}
