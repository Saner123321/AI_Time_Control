namespace Aict.Core;
public static class GapAccounting {
    // Carry both kinds of charged time across edits. Unknown legacy time stays unresolved.
    public static DailyUsage Carry(DailyUsage target,IEnumerable<DailyUsage> sources){
        var rows=sources.ToArray();long known=0,gap=0;foreach(var r in rows){known=checked(known+r.KnownTicks);gap=checked(gap+r.GapChargedTicks);}
        bool hasGap=target.HasGap||rows.Any(r=>r.HasGap),unresolved=target.UnresolvedGap||rows.Any(r=>r.UnresolvedGap);
        return new(Math.Max(known,target.KnownTicks),hasGap){GapChargedTicks=Math.Max(gap,target.GapChargedTicks),GapChargeComplete=hasGap&&!unresolved,GapEstimated=target.GapEstimated||rows.Any(r=>r.GapEstimated)};
    }
    public static UsageLedger MigrateLegacy(PolicyConfiguration policy,UsageLedger ledger,IEnumerable<ActivityCounter> activity,Action<string,object>? log=null){
        var totals=activity.Where(r=>r.Kind=="gap"&&r.Zone==policy.TimeZoneId).GroupBy(r=>r.Day).ToDictionary(g=>g.Key,g=>g.Sum(r=>r.UnknownTicks));
        foreach(var pair in ledger.Accounts.Where(p=>p.Value.UnresolvedGap).ToArray()){
            var parts=pair.Key.Split('/');var id=Guid.ParseExact(parts[0],"N");var day=DateOnly.ParseExact(parts[1],"yyyy-MM-dd");Guid? slot=parts.Length==3?Guid.ParseExact(parts[2],"N"):null;
            var group=policy.Groups.FirstOrDefault(g=>g.Id==id);var quota=group is null?null:QuotaSchedules.Specs(group).FirstOrDefault(q=>q.Id==slot);if(quota is null)continue;
            long estimate=totals.GetValueOrDefault(day.ToString("yyyy-MM-dd"));if(quota.Window is {} w && w.EndMinute<w.StartMinute)estimate=checked(estimate+totals.GetValueOrDefault(day.AddDays(1).ToString("yyyy-MM-dd")));
            // Old snapshots kept only a flag. Daily aggregate is a conservative estimate, not an exact slot duration.
            bool available=estimate>0;if(!available)estimate=quota.Limit.Ticks;
            ledger=ledger.Put(id,day,pair.Value with{GapChargedTicks=checked(pair.Value.GapChargedTicks+estimate),GapChargeComplete=true,GapEstimated=true},slot);
            log?.Invoke("LegacyGapChargeEstimated",new{account=pair.Key,estimatedSeconds=estimate/(double)TimeSpan.TicksPerSecond,source=available?"daily_unknown_aggregate_estimate":"missing_legacy_duration_quota_bound",knownTicksPreserved=pair.Value.KnownTicks});
        }return ledger;
    }
}
