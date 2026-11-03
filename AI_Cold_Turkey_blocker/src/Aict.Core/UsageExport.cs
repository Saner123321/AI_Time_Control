using System.Globalization;
using System.Text;

namespace Aict.Core;

public static class UsageExport {
    static string Csv(string value) {
        string leading=value.TrimStart();
        if((leading.Length>0 && "=+-@".Contains(leading[0])) || (value.Length>0 && "\t\r\n".Contains(value[0]))) value="'"+value;
        return "\""+value.Replace("\"","\"\"")+"\"";
    }
    public static string ToCsv(PolicyConfiguration policy,UsageLedger ledger) {
        PolicyValidation.Validate(policy);
        var names=policy.Groups.ToDictionary(g=>g.Id,g=>g.Name);
        var text=new StringBuilder("group_id,current_group_name,local_date,quota_slot_id,current_quota_window,known_seconds,has_usage_gap\r\n");
        foreach(var pair in ledger.Accounts.OrderBy(p=>p.Key,StringComparer.Ordinal)) {
            var parts=pair.Key.Split('/');
            if(parts.Length is not (2 or 3) || !Guid.TryParseExact(parts[0],"N",out var group) || !DateOnly.TryParseExact(parts[1],"yyyy-MM-dd",out var day) || pair.Value.KnownTicks<0 || parts.Length==3 && !Guid.TryParseExact(parts[2],"N",out _)) throw new InvalidDataException("Invalid usage export account");
            Guid? slot=parts.Length==3?Guid.ParseExact(parts[2],"N"):null;var current=policy.Groups.FirstOrDefault(g=>g.Id==group);var spec=current is null?null:QuotaSchedules.Specs(current).FirstOrDefault(s=>s.Id==slot);
            text.Append(Csv(group.ToString())).Append(',').Append(Csv(names.GetValueOrDefault(group) ?? "")).Append(',').Append(day.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)).Append(',').Append(Csv(slot?.ToString("N")??"")).Append(',').Append(Csv(spec is null?"已删除/更改的额度时段":QuotaSchedules.Label(spec))).Append(',')
                .Append((pair.Value.KnownTicks/(decimal)TimeSpan.TicksPerSecond).ToString("0.#######",CultureInfo.InvariantCulture)).Append(',')
                .Append(pair.Value.HasGap?"true":"false").Append("\r\n");
        }
        return text.ToString();
    }
    public static string ToJson(EngineSnapshot snapshot) => PolicyJson.Serialize(snapshot);
}
