using System.Collections.Immutable;
using System.Globalization;
namespace Aict.Core;
public static class BlockExplanations {
    public static ImmutableArray<string> Describe(PolicyConfiguration policy,RuleGroup group,UsageLedger usage,ClockReading clock,bool telemetry=true) {
        var reasons=RuleEngine.ActivationReasons(policy,group,usage,clock,telemetry);var lines=ImmutableArray.CreateBuilder<string>();
        string Local(DateTimeOffset utc)=>TimeZoneInfo.ConvertTime(utc,PolicyValidation.Zone(policy)).ToString("MM-dd HH:mm:ss",CultureInfo.InvariantCulture);
        static string Minute(int m)=>$"{m/60:00}:{m%60:00}";
        foreach(var r in reasons)switch(r){
            case BlockReason.Always:lines.Add("已开启全天封锁。");break;
            case BlockReason.WeeklySchedule:
                var local=TimeZoneInfo.ConvertTime(clock.EffectiveUtc,PolicyValidation.Zone(policy)).DateTime;
                foreach(var w in group.Windows.Where(w=>RuleEngine.WeeklyActive(w,local)))lines.Add("处于每周硬封锁时段："+Minute(w.StartMinute)+"—"+(w.EndMinute<w.StartMinute?"次日 ":"")+Minute(w.EndMinute)+"。");break;
            case BlockReason.Cycle:
                if(group.Cycle is {} cycle){var period=cycle.BlockFor+cycle.RestFor;long n=(clock.EffectiveUtc-cycle.AnchorUtc).Ticks/period.Ticks;var end=cycle.AnchorUtc+TimeSpan.FromTicks(n*period.Ticks)+cycle.BlockFor;lines.Add("循环计划处于封锁阶段，本轮至 "+Local(end)+"。");}break;
            case BlockReason.TimedBlock:if(group.BlockUntilUtc is {} until)lines.Add("倒计时封锁尚未结束，将于 "+Local(until)+" 结束。");break;
            case BlockReason.QuotaExhausted:
            case BlockReason.UsageGap:
                foreach(var q in QuotaSchedules.Specs(group)){var p=QuotaPeriods.At(policy,q,clock.EffectiveUtc);if(!p.Contains(clock.EffectiveUtc))continue;var account=usage.Get(group.Id,p.Day,q.Id);
                    if(r==BlockReason.QuotaExhausted && !account.UnresolvedGap && account.ChargedTicks>=q.Limit.Ticks)lines.Add(QuotaSchedules.Label(q)+" 的 "+q.Limit.TotalMinutes.ToString("0.##",CultureInfo.InvariantCulture)+" 分钟额度已用完"+(account.GapChargedTicks>0?"（包含采集失联扣时）":"")+"，本时段至 "+Local(p.EndUtc)+"。");
                    if(r==BlockReason.UsageGap && account.UnresolvedGap)lines.Add(QuotaSchedules.Label(q)+" 的旧版使用记录不完整，缺失时长尚待换算；请在软件中检查迁移状态。");
                }break;
            case BlockReason.ClockUntrusted:lines.Add("系统时间校验异常，为保护当前规则暂时封锁；请在软件中查看时间状态。");break;
        }return lines.ToImmutable();
    }
}
