namespace Aict.Core;

public static class ChangeQueue {
    // Caller is the trusted backend. Client APIs must never accept a serialized EngineSnapshot.
    public static EngineSnapshot Submit(EngineSnapshot current,PolicyConfiguration target,ClockReading clock,ChangePurpose purpose=ChangePurpose.ReplaceConfiguration) {
        PolicyValidation.Validate(current.Policy);PolicyValidation.Validate(target);
        if(!clock.Trusted || clock.EffectiveUtc<current.Clock.EffectiveUtc) throw new InvalidOperationException("Verified non-regressing submission time required; keep edits as a draft");
        if(!Enum.IsDefined(purpose)) throw new ArgumentException("Unknown change purpose");
        if(purpose==ChangePurpose.Uninstall && target.Groups.Any(g=>g.Enabled)) throw new ArgumentException("Uninstall target must disable all groups");
        string json=PolicyJson.Serialize(target);
        var pending=new PendingChange(Guid.NewGuid(),purpose,PolicyJson.Fingerprint(current.Policy),json,PolicyJson.Sha256(json),clock.EffectiveUtc,clock.EffectiveUtc+current.Policy.ChangeDelay);
        return current with {Pending=pending};
    }
    public static EngineSnapshot Cancel(EngineSnapshot current) => current with {Pending=null};
    public static ApplyResult TryApply(EngineSnapshot current,ClockReading clock,bool telemetryHealthy=true) {
        if(current.Pending is not {} pending) return new(ApplyStatus.NoPendingChange,current);
        if(!string.Equals(PolicyJson.Sha256(pending.TargetJson),pending.TargetSha256,StringComparison.Ordinal)) throw new InvalidDataException("Frozen change payload checksum mismatch");
        if(pending.BasePolicySha256!=PolicyJson.Fingerprint(current.Policy)) return new(ApplyStatus.PolicyConflict,current);
        if(pending.NotBeforeUtc!=pending.SubmittedUtc+current.Policy.ChangeDelay) throw new InvalidDataException("Pending change delay metadata mismatch");
        if(!clock.Trusted || clock.EffectiveUtc<current.Clock.EffectiveUtc) return new(ApplyStatus.Locked,current);
        if(clock.EffectiveUtc<pending.NotBeforeUtc) return new(ApplyStatus.WaitingForDelay,current);
        if(!pending.ApplyAtDeadline && RuleEngine.IsModificationLocked(current.Policy,current.Usage,clock,telemetryHealthy)) return new(ApplyStatus.Locked,current);
        var target=PolicyJson.Parse<PolicyConfiguration>(pending.TargetJson);PolicyValidation.Validate(target);
        if(!Enum.IsDefined(pending.Purpose) || (pending.Purpose==ChangePurpose.Uninstall && target.Groups.Any(g=>g.Enabled))) throw new InvalidDataException("Invalid uninstall/change purpose");
        var usage=QuotaPeriods.CarryForward(current.Policy,target,current.Usage,clock.EffectiveUtc);
        return new(ApplyStatus.Applied,current with {Policy=target,Usage=usage,Pending=null},pending.Purpose==ChangePurpose.Uninstall);
    }
}
