using System.Collections.Immutable;
using Aict.Core;

namespace Aict.Enforcement;

public sealed record ApplicationPlan(ImmutableArray<string> DeniedIdentities, DateTimeOffset EvaluatedUtc, bool ClockTrusted) {
    public string Fingerprint => PolicyJson.Sha256(string.Join("\n",DeniedIdentities));
}
public static class ApplicationPlanner {
    public static string HashIdentity(string hex) {
        if(hex.Length!=64 || hex.Any(c=>!char.IsAsciiHexDigit(c))) throw new ArgumentException("Only SHA256 PE SIP identities are supported");
        return "authenticode:sha256:"+hex.ToUpperInvariant();
    }
    public static string Digest(string identity) {
        const string prefix="authenticode:sha256:";
        if(!identity.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("Publisher/MSIX/process-name/path identities are not yet supported by this adapter");
        return HashIdentity(identity[prefix.Length..])[prefix.Length..];
    }
    public static ApplicationPlan Compile(EngineSnapshot state,bool telemetryHealthy=false) {
        PolicyValidation.Validate(state.Policy);
        // Refuse unsupported dimensions, including disabled groups, instead of reporting a partial policy as installed.
        foreach(var group in state.Policy.Groups) {
            if(group.WebsiteMode!=TargetMode.None || !group.WebsiteDomains.IsEmpty || !group.WebsiteExceptions.IsEmpty || group.ApplicationMode==TargetMode.AllowSelected)
                throw new NotSupportedException("Website and application allow-list enforcement is not connected");
            foreach(string identity in group.ApplicationIds.Concat(group.ApplicationExceptions)) _=Digest(identity);
        }
        var targets=state.Policy.Groups.SelectMany(g=>g.ApplicationIds).Select(id=>HashIdentity(Digest(id))).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var denied=targets.Where(id=>RuleEngine.Evaluate(state.Policy,state.Usage,new ActivityContext(id),state.Clock.Reading,telemetryHealthy).Blocked).ToImmutableArray();
        return new(denied,state.Clock.EffectiveUtc,state.Clock.Trusted);
    }
}

public sealed record PolicyObservation(bool ExePresent,bool CompanionPresent,bool ReadbackBlocked);
public interface IApplicationPolicyAdapter {
    Task<PolicyObservation> ReadAsync(CancellationToken cancellation);
    Task<PolicyObservation> ApplyAsync(ApplicationPlan plan,CancellationToken cancellation);
}

// Separates desired rule state from a verified OS readback; never returns a saved rule as proof of enforcement.
public sealed class ApplicationReconciler(IApplicationPolicyAdapter adapter,TimeProvider? time=null) {
    readonly TimeProvider clock=time ?? TimeProvider.System;
    public string? LastVerifiedFingerprint {get;private set;}
    public PolicyObservation? LastObservation {get;private set;}
    public DateTimeOffset? LastVerifiedUtc {get;private set;}
    public string? Fault {get;private set;}
    public async Task<bool> ReconcileAsync(ApplicationPlan plan,CancellationToken cancellation=default) {
        try {
            var observation=await adapter.ReadAsync(cancellation);
            bool blocked=!plan.DeniedIdentities.IsEmpty;
            if(LastVerifiedFingerprint!=plan.Fingerprint || observation.ReadbackBlocked!=blocked || (blocked && !observation.CompanionPresent))
                observation=await adapter.ApplyAsync(plan,cancellation);
            if(observation.ReadbackBlocked!=blocked || observation.ExePresent!=blocked || (blocked && !observation.CompanionPresent)) throw new InvalidDataException("OS policy readback does not match the desired application plan");
            LastVerifiedFingerprint=plan.Fingerprint;LastObservation=observation;LastVerifiedUtc=clock.GetUtcNow();Fault=null;return true;
        } catch(OperationCanceledException) when(cancellation.IsCancellationRequested) {throw;}
        catch(Exception e) {Fault=e.GetType().Name+": "+e.Message;return false;}
    }
}
