namespace Aict.Core;
public sealed record QuotaPeriod(DateOnly Day,DateTimeOffset StartUtc,DateTimeOffset EndUtc) {
    public bool Contains(DateTimeOffset utc)=>utc>=StartUtc && utc<EndUtc;
}
public static class QuotaPeriods {
    static QuotaPeriod ForDay(PolicyConfiguration policy,QuotaWindow? window,DateOnly day) {
        int start=window?.StartMinute??0,end=window?.EndMinute??1440;
        var local=day.ToDateTime(TimeOnly.MinValue,DateTimeKind.Unspecified);
        var zone=PolicyValidation.Zone(policy);
        return new(day,new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local.AddMinutes(start),zone)),new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local.AddMinutes(end+(end<start?1440:0)),zone)));
    }
    public static QuotaPeriod At(PolicyConfiguration policy,RuleGroup group,DateTimeOffset utc)=>AtWindow(policy,group.DailyQuotaWindow,utc);
    public static QuotaPeriod At(PolicyConfiguration policy,QuotaSpec quota,DateTimeOffset utc)=>AtWindow(policy,quota.Window,utc);
    static QuotaPeriod AtWindow(PolicyConfiguration policy,QuotaWindow? window,DateTimeOffset utc) {
        var local=TimeZoneInfo.ConvertTime(utc,PolicyValidation.Zone(policy));var day=DateOnly.FromDateTime(local.DateTime);
        if(window is {} w && w.EndMinute<w.StartMinute && local.TimeOfDay.Ticks<w.EndMinute*TimeSpan.TicksPerMinute)day=day.AddDays(-1);
        return ForDay(policy,window,day);
    }
    public static IEnumerable<QuotaPeriod> Intersections(PolicyConfiguration policy,RuleGroup group,DateTimeOffset from,DateTimeOffset until)=>WindowIntersections(policy,group.DailyQuotaWindow,from,until);
    public static IEnumerable<QuotaPeriod> Intersections(PolicyConfiguration policy,QuotaSpec quota,DateTimeOffset from,DateTimeOffset until)=>WindowIntersections(policy,quota.Window,from,until);
    static IEnumerable<QuotaPeriod> WindowIntersections(PolicyConfiguration policy,QuotaWindow? window,DateTimeOffset from,DateTimeOffset until) {
        if(until<=from)yield break;
        var first=RuleEngine.Day(policy,from);var last=RuleEngine.Day(policy,until.AddTicks(-1));
        if(window is {} w && w.EndMinute<w.StartMinute)first=first.AddDays(-1);
        for(var day=first;day<=last;day=day.AddDays(1)){var p=ForDay(policy,window,day);var a=from>p.StartUtc?from:p.StartUtc;var b=until<p.EndUtc?until:p.EndUtc;if(b>a)yield return p with{StartUtc=a,EndUtc=b};}
    }
    // Editing a window/time zone never creates a fresh allowance for the same rule.
    public static UsageLedger CarryForward(PolicyConfiguration before,PolicyConfiguration after,UsageLedger ledger,DateTimeOffset now) {
        foreach(var next in after.Groups.Where(QuotaSchedules.HasQuota)) {
            var old=before.Groups.FirstOrDefault(g=>g.Id==next.Id);if(old is null)continue;
            if(old.QuotaSlots is null && next.QuotaSlots is null){if(old.DailyQuotaWindow==next.DailyQuotaWindow && before.TimeZoneId==after.TimeZoneId)continue;var source=ledger.Get(old.Id,At(before,old,now).Day);var legacyDay=At(after,next,now).Day;var prior=ledger.Get(next.Id,legacyDay);ledger=ledger.Put(next.Id,legacyDay,GapAccounting.Carry(prior,[source]));continue;}
            foreach(var n in QuotaSchedules.Specs(next)){
                if(before.TimeZoneId==after.TimeZoneId && QuotaSchedules.Specs(old).Any(o=>o.Id==n.Id && o.Window==n.Window))continue;
                var period=At(after,n,now);var target=ledger.Get(next.Id,period.Day,n.Id);var sourceKeys=new HashSet<(Guid? Slot,DateOnly Day)>();
                foreach(var o in QuotaSchedules.Specs(old)){
                    foreach(var overlap in Intersections(before,o,period.StartUtc,period.EndUtc))sourceKeys.Add((o.Id,overlap.Day));
                    if(o.Id==n.Id)sourceKeys.Add((o.Id,At(before,o,now).Day));
                }
                ledger=ledger.Put(next.Id,period.Day,GapAccounting.Carry(target,sourceKeys.Select(key=>ledger.Get(old.Id,key.Day,key.Slot))),n.Id);
            }
        }return ledger;
    }
    public static string Label(RuleGroup group)=>group.QuotaSlots is {} slots?string.Join("；",slots.Select(s=>QuotaSchedules.Label(new(s.Id,new(s.StartMinute,s.EndMinute),s.Limit,s.Name)))):Label(group.DailyQuotaWindow);
    public static string Label(QuotaWindow? window)=>window is {} w?$"每日 {w.StartMinute/60:00}:{w.StartMinute%60:00}—{(w.EndMinute<w.StartMinute?"次日 ":"")}{w.EndMinute/60:00}:{w.EndMinute%60:00}":"每日 00:00—24:00";
}
