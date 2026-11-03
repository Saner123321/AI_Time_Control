using System.Collections.Immutable;
namespace Aict.Core;
public sealed record ActivityFrame(string TimeZoneId,UsageSlice Slice,string? ApplicationName);
public sealed record ActivityCounter(string Day,string Zone,string Kind,string Target,string Name,long KnownTicks,long UnknownTicks);
public static class ActivityAccounting {
    public static ImmutableArray<ActivityCounter> Expand(ActivityFrame frame){
        var slice=frame.Slice;if(slice.Duration<TimeSpan.Zero || slice.Duration>TimeSpan.FromDays(31))throw new ArgumentOutOfRangeException(nameof(frame));
        if(slice.ObservationKnown && (slice.SessionLocked || slice.Suspended || !slice.Foreground))return [];
        bool known=slice.ObservationKnown && (slice.Context.ApplicationId is not null || slice.Context.WebsiteDomain is not null);
        var zone=PolicyValidation.Zone(new(){TimeZoneId=frame.TimeZoneId});var begin=slice.StartUtc;
        long ticks=known?Math.Min(slice.Duration.Ticks,Math.Max(0,(RuleEngine.IdleThreshold-slice.IdleAtStart).Ticks)):slice.Duration.Ticks;var until=begin+TimeSpan.FromTicks(ticks);var rows=ImmutableArray.CreateBuilder<ActivityCounter>();
        while(begin<until){var local=TimeZoneInfo.ConvertTime(begin,zone).DateTime;var day=DateOnly.FromDateTime(local);var midnight=DateTime.SpecifyKind(day.AddDays(1).ToDateTime(TimeOnly.MinValue),DateTimeKind.Unspecified);var boundary=new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(midnight,zone),TimeSpan.Zero);var end=boundary<until?boundary:until;long duration=(end-begin).Ticks;
            if(!known)rows.Add(new(day.ToString("yyyy-MM-dd"),frame.TimeZoneId,"gap","unknown","统计缺口",0,duration));
            else{if(slice.Context.ApplicationId is {} id)rows.Add(new(day.ToString("yyyy-MM-dd"),frame.TimeZoneId,"app",id,frame.ApplicationName??"未命名应用",duration,0));if(slice.Context.WebsiteDomain is {} site)rows.Add(new(day.ToString("yyyy-MM-dd"),frame.TimeZoneId,"site",PolicyValidation.Domain(site),PolicyValidation.Domain(site),duration,0));}begin=end;
        }return rows.ToImmutable();
    }
}
