using System.Collections.Immutable;
namespace Aict.Core;
public sealed record WebsiteExceptionDetail(string Pattern,string Note="",string Group="");
public static class ExceptionCatalog {
    public static void ValidateEntries(ImmutableArray<WebsiteExceptionDetail> entries) {
        if(entries.IsDefault || entries.Length>512 || entries.Any(e=>e is null))throw new ArgumentException("网站例外最多512条。");
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var e in entries){if(!seen.Add(WebsitePattern.Normalize(e.Pattern)))throw new ArgumentException("网站例外有重复网址。");if(e.Note is null || e.Group is null || e.Note.Length>500 || e.Group.Length>60 || e.Note.Any(char.IsControl) || e.Group.Any(char.IsControl))throw new ArgumentException("备注最多500字，分组最多60字，均使用单行文字。");}
    }
    public static void Validate(RuleGroup group) {
        if(group.WebsiteExceptionDetails is not {} details)return;ValidateEntries(details);
        var patterns=group.WebsiteExceptions.Select(WebsitePattern.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(details.Any(e=>!patterns.Contains(WebsitePattern.Normalize(e.Pattern))))throw new ArgumentException("备注必须属于已有的网站例外。");
    }
    public static ImmutableArray<WebsiteExceptionDetail> Rows(RuleGroup group) {
        var notes=(group.WebsiteExceptionDetails??[]).ToDictionary(e=>WebsitePattern.Normalize(e.Pattern),StringComparer.OrdinalIgnoreCase);
        return group.WebsiteExceptions.Distinct().Select(p=>notes.GetValueOrDefault(WebsitePattern.Normalize(p))??new(p)).ToImmutableArray();
    }
    public static RuleGroup SetRows(RuleGroup group,ImmutableArray<WebsiteExceptionDetail> rows) {
        ValidateEntries(rows);var clean=rows.Select(e=>e with{Pattern=WebsitePattern.Normalize(e.Pattern),Note=e.Note.Trim(),Group=e.Group.Trim()}).ToImmutableArray();
        var details=clean.Where(e=>e.Note.Length>0 || e.Group.Length>0).ToImmutableArray();
        return group with{WebsiteExceptions=clean.Select(e=>e.Pattern).ToImmutableArray(),WebsiteExceptionDetails=details.IsEmpty?null:details};
    }
    public static bool SameEnforcement(PolicyConfiguration a,PolicyConfiguration b) {
        static PolicyConfiguration Strip(PolicyConfiguration p)=>p with{Groups=p.Groups.OrderBy(g=>g.Id).Select(g=>g with{WebsiteExceptionDetails=null,WebsiteExceptions=g.WebsiteExceptions.Select(WebsitePattern.Normalize).Order(StringComparer.Ordinal).ToImmutableArray()}).ToImmutableArray()};
        return PolicyJson.Fingerprint(Strip(a))==PolicyJson.Fingerprint(Strip(b));
    }
    public static EngineSnapshot UpdateAnnotations(EngineSnapshot state,Guid groupId,ImmutableArray<WebsiteExceptionDetail> entries) {
        ValidateEntries(entries);PolicyConfiguration? queued=state.Pending is {} pending?PolicyJson.Parse<PolicyConfiguration>(pending.TargetJson):null;
        if(state.Pending is {} frozen && (frozen.BasePolicySha256!=PolicyJson.Fingerprint(state.Policy) || frozen.TargetSha256!=PolicyJson.Sha256(frozen.TargetJson)))throw new InvalidDataException("待生效规则校验不一致，未保存备注。");
        var eligible=state.Policy.Groups.Concat(queued?.Groups??[]).Where(g=>g.Id==groupId).SelectMany(g=>g.WebsiteExceptions).Select(WebsitePattern.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(eligible.Count==0 || entries.Any(e=>!eligible.Contains(WebsitePattern.Normalize(e.Pattern))))throw new ArgumentException("仅能保存已提交网址的备注。新增或修改网址请提交完整修改。");
        var edits=entries.ToDictionary(e=>WebsitePattern.Normalize(e.Pattern),StringComparer.OrdinalIgnoreCase);
        PolicyConfiguration Apply(PolicyConfiguration p)=>p with{Groups=p.Groups.Select(g=>g.Id!=groupId?g:SetRows(g,Rows(g).Select(e=>edits.GetValueOrDefault(WebsitePattern.Normalize(e.Pattern))??e).ToImmutableArray())).ToImmutableArray()};
        var policy=Apply(state.Policy);PendingChange? next=state.Pending;
        if(next is not null){string json=PolicyJson.Serialize(Apply(queued!));next=next with{BasePolicySha256=PolicyJson.Fingerprint(policy),TargetJson=json,TargetSha256=PolicyJson.Sha256(json)};}
        return state with{Policy=policy,Pending=next};
    }
}
