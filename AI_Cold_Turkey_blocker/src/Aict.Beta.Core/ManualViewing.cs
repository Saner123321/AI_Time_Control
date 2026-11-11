using System.Collections.Immutable;
using Aict.Core;
namespace Aict.Beta;
public enum ManualObservation {OutsideClient,Confirmed,Unavailable}
public sealed record ManualViewingSample(ManualObservation Observation,ImmutableArray<string> VisibleUrls,long CaptureAgeMs,long UiAgeMs,string AppliedPlan,ImmutableArray<string> RenderedKeys,string Diagnostic);
public sealed record ManualPageDecision(string Key,bool Denied,string Explanation);
public sealed record ManualViewingPlan(string Fingerprint,bool Required,ImmutableArray<ManualPageDecision> Pages,WebPlan FailureScope,bool Uninstall=false);
public sealed record ManualViewingExecution(bool Connected,string Observation,int VisiblePages,int RequiredMasks,int RenderedMasks,bool Applied,bool ProcessProtected,string? Fault);
public static class ManualViewingRules {
    public static string Key(string url)=>PolicyJson.Sha256(WebsitePattern.WebUrl(url));
    public static WebPlan Scope(PolicyConfiguration policy)=>new(policy.Groups.Where(Plans.HasWebsiteScope).Select(g=>new WebGroup(g.Id,g.WebsiteMode,g.WebsiteDomains,g.WebsiteExceptions,[],g.Name,[])).ToImmutableArray(),policy.Groups.Any(Plans.HasWebsiteScope));
    public static ManualViewingPlan Decide(WebPlan active,PolicyConfiguration policy,IEnumerable<string> urls,bool uninstall=false){
        var pages=urls.Distinct(StringComparer.Ordinal).Select(url=>{
            var groups=active.Groups.Where(g=>Plans.Denies(g,url)).ToArray();
            return new ManualPageDecision(Key(url),groups.Length>0,string.Join("\n",groups.Select(g=>g.Name+"："+string.Join("；",g.Details??[]))));
        }).ToImmutableArray();
        string fingerprint=PolicyJson.Sha256(PolicyJson.Serialize(new{web=active.Fingerprint,pages}));
        return new(fingerprint,active.Required,pages,Scope(policy),uninstall);
    }
}
