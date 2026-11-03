using System.Security.Cryptography;
using System.Text;
namespace Aict.Core;
public sealed record QuotaSpec(Guid? Id,QuotaWindow? Window,TimeSpan Limit,string Name);
public static class QuotaSchedules {
    public static bool HasQuota(RuleGroup g)=>g.DailyQuota is not null || g.QuotaSlots is {Length:>0};
    public static Guid LegacySlotId(Guid group)=>new(SHA256.HashData(Encoding.UTF8.GetBytes(group.ToString("N")+"/legacy-quota"))[..16]);
    public static IEnumerable<QuotaSpec> Specs(RuleGroup g){if(g.DailyQuota is {} limit)yield return new(null,g.DailyQuotaWindow,limit,"");if(g.QuotaSlots is {} slots)foreach(var s in slots)yield return new(s.Id,new(s.StartMinute,s.EndMinute),s.Limit,s.Name);}
    static bool[] Minutes(QuotaWindow? w){int start=w?.StartMinute??0,end=w?.EndMinute??1440,length=end>start?end-start:1440-start+end;var values=new bool[1440];for(int i=0;i<length;i++)values[(start+i)%1440]=true;return values;}
    public static bool Overlaps(QuotaWindow? a,QuotaWindow? b){var left=Minutes(a);var right=Minutes(b);return left.Where((v,i)=>v&&right[i]).Any();}
    public static void Validate(RuleGroup g){if(g.QuotaSlots is not {} slots)return;if(slots.IsDefault || slots.Length is 0 or >24 || g.DailyQuota is not null || g.DailyQuotaWindow is not null)throw new ArgumentException("分时段额度需设置1—24个时段，不能同时设置旧版每日总额度。");var ids=new HashSet<Guid>();var occupied=new bool[1440];foreach(var s in slots){if(s is null || s.Id==Guid.Empty || !ids.Add(s.Id) || s.StartMinute is <0 or >=1440 || s.EndMinute is <0 or >1440 || s.StartMinute==s.EndMinute || s.Limit<TimeSpan.Zero || s.Limit>TimeSpan.FromDays(1) || s.Name is null || s.Name.Length>60 || s.Name.Any(char.IsControl))throw new ArgumentException("额度时段名称最多60字；时间须有效且起止不同，限额为0—1440分钟。");var minutes=Minutes(new(s.StartMinute,s.EndMinute));for(int i=0;i<1440;i++)if(minutes[i]){if(occupied[i])throw new ArgumentException("额度时段发生重叠（包括跨午夜部分），请调整为互不重叠的时段。");occupied[i]=true;}}}
    public static bool ContainsPrevious(RuleGroup before,RuleGroup after){var next=Specs(after).ToArray();return Specs(before).All(old=>next.Any(n=>(n.Id==old.Id || old.Id is null && n.Id==LegacySlotId(before.Id)) && n.Window==old.Window && n.Limit<=old.Limit));}
    public static string Label(QuotaSpec s)=>(s.Name.Length>0?s.Name+" · ":"")+QuotaPeriods.Label(s.Window);
}
