using System.Collections.Immutable;
namespace Aict.Core;
// A proof of containment, not a comparison at the current instant. Unproven changes wait.
public static class PolicyStrength {
    public static bool IsAtLeastAsRestrictive(PolicyConfiguration before,PolicyConfiguration after,DateTimeOffset now){
        PolicyValidation.Validate(before);PolicyValidation.Validate(after);
        if(PolicyValidation.Zone(before).BaseUtcOffset!=PolicyValidation.Zone(after).BaseUtcOffset || after.ChangeDelay<before.ChangeDelay)return false;
        foreach(var old in before.Groups.Where(g=>g.Enabled)){
            var next=after.Groups.FirstOrDefault(g=>g.Id==old.Id);
            if(next is null || !next.Enabled || old.LockWhileBlocking&&!next.LockWhileBlocking || !ScopeContains(old,next) || !ActivationContains(old,next,now))return false;
        }return true;
    }
    static bool ScopeContains(RuleGroup old,RuleGroup next){
        var apps=old.ApplicationIds.Concat(old.ApplicationExceptions).Concat(next.ApplicationIds).Concat(next.ApplicationExceptions).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string other="unlisted-app";while(apps.Contains(other,StringComparer.OrdinalIgnoreCase))other+="x";apps.Add(other);
        static bool App(RuleGroup g,string id){bool selected=g.ApplicationIds.Contains(id,StringComparer.OrdinalIgnoreCase);return g.ApplicationMode!=TargetMode.None && !g.ApplicationExceptions.Contains(id,StringComparer.OrdinalIgnoreCase) && (g.ApplicationMode==TargetMode.AllowSelected?!selected:selected);}
        if(apps.Any(id=>App(old,id)&&!App(next,id)))return false;
        if(old.WebsiteDomains.Concat(old.WebsiteExceptions).Concat(next.WebsiteDomains).Concat(next.WebsiteExceptions).Any(v=>!WebsitePattern.IsDomain(v))) {
            // Prove literal set inclusion only for page/glob languages. Other changes wait.
            var a=old.WebsiteDomains.Select(WebsitePattern.Normalize).ToHashSet();var b=next.WebsiteDomains.Select(WebsitePattern.Normalize).ToHashSet();
            var x=old.WebsiteExceptions.Select(WebsitePattern.Normalize).ToHashSet();var y=next.WebsiteExceptions.Select(WebsitePattern.Normalize).ToHashSet();
            return old.WebsiteMode==TargetMode.None || old.WebsiteMode==next.WebsiteMode && y.IsSubsetOf(x) && (old.WebsiteMode==TargetMode.AllowSelected?b.IsSubsetOf(a):a.IsSubsetOf(b));
        }
        var domains=old.WebsiteDomains.Concat(old.WebsiteExceptions).Concat(next.WebsiteDomains).Concat(next.WebsiteExceptions).Select(PolicyValidation.Domain).Distinct().ToList();
        // Every suffix predicate is constant between listed domain boundaries. Include the outside region.
        other="unlisted-host";while(domains.Any(d=>other==d||other.EndsWith('.'+d,StringComparison.Ordinal)))other+="x";domains.Add(other);
        static bool Site(RuleGroup g,string d){bool In(ImmutableArray<string> values)=>values.Any(v=>{var root=PolicyValidation.Domain(v);return d==root||d.EndsWith('.'+root,StringComparison.Ordinal);});return g.WebsiteMode!=TargetMode.None && !In(g.WebsiteExceptions) && (g.WebsiteMode==TargetMode.AllowSelected?!In(g.WebsiteDomains):In(g.WebsiteDomains));}
        return !domains.Any(d=>Site(old,d)&&!Site(next,d));
    }
    static bool ActivationContains(RuleGroup old,RuleGroup next,DateTimeOffset now){
        if(next.AlwaysBlock)return true;if(old.AlwaysBlock)return false;
        var a=Week(old.Windows);var b=Week(next.Windows);bool full=b.All(v=>v);
        if(full)return true;
        if(a.Where((v,i)=>v&&!b[i]).Any())return false;
        if(old.BlockUntilUtc is {} until && until>now && (next.BlockUntilUtc is not {} end || end<until))return false;
        if(old.Cycle is {} c){if(next.Cycle is not {} n || n.AnchorUtc!=c.AnchorUtc || n.BlockFor+n.RestFor!=c.BlockFor+c.RestFor || n.BlockFor<c.BlockFor)return false;}
        if(QuotaSchedules.HasQuota(old) && !QuotaSchedules.ContainsPrevious(old,next))return false;
        return true;
    }
    static bool[] Week(ImmutableArray<WeeklyWindow> windows){var result=new bool[7*1440];foreach(var w in windows)for(int day=0;day<7;day++)if(((int)w.Days&(1<<day))!=0){int length=w.EndMinute>w.StartMinute?w.EndMinute-w.StartMinute:1440-w.StartMinute+w.EndMinute;for(int m=0;m<length;m++)result[(day*1440+w.StartMinute+m)%result.Length]=true;}return result;}
}
