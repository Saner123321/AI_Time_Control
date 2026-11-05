namespace Aict.Beta;
public sealed record BrowserProof(string ProfileId,string AppliedPlan,bool Focused,long AwakeTick);
public sealed record BrowserReadiness(bool Ready,string Reason,long? HeartbeatAgeMs,bool InGrace);
// Network admission is separate from foreground usage accounting. Leases never extend
// on evaluation alone, never transfer across plans, and use awake time across sleep.
public sealed class BrowserReadinessTracker {
    public const long FreshMs=3500,LeaseMs=5000,FocusGraceMs=2000;
    sealed class State {public string Plan="";public long? VerifiedAt,FocusMissingAt;}
    readonly Dictionary<string,State> states=[];
    public BrowserReadiness Evaluate(string browser,bool required,string plan,IEnumerable<BrowserProof> profiles,long awake,bool collectorHealthy,string? foreground){
        if(!states.TryGetValue(browser,out var s))states[browser]=s=new();
        if(!required){s.Plan=plan;s.VerifiedAt=null;s.FocusMissingAt=null;return new(true,"not_required",null,false);}
        if(s.Plan!=plan){s.Plan=plan;s.VerifiedAt=null;s.FocusMissingAt=null;}
        var all=profiles.ToArray();
        var rows=all.Where(p=>p.AppliedPlan==plan&&awake>=p.AwakeTick&&awake-p.AwakeTick<=LeaseMs).ToArray();
        long? newest=rows.Length>0?rows.Max(p=>p.AwakeTick):null;
        bool focused=rows.Any(p=>p.Focused&&awake-p.AwakeTick<=FreshMs);
        bool needsFocus=collectorHealthy&&foreground==browser;
        var latestFocused=all.Where(p=>p.Focused&&awake>=p.AwakeTick&&awake-p.AwakeTick<=FreshMs).MaxBy(p=>p.AwakeTick);
        if((!collectorHealthy||needsFocus)&&latestFocused is {} failed&&failed.AppliedPlan!=plan){s.VerifiedAt=null;s.FocusMissingAt=null;return new(false,"explicit_plan_not_confirmed",awake-failed.AwakeTick,false);}
        if(focused || collectorHealthy&&!needsFocus&&newest is not null){
            s.VerifiedAt=focused?rows.Where(p=>p.Focused).Max(p=>p.AwakeTick):newest;s.FocusMissingAt=null;
            long age=awake-s.VerifiedAt!.Value;return new(true,age>FreshMs?"heartbeat_grace":"confirmed",age,age>FreshMs);
        }
        bool lease=s.VerifiedAt is {} at&&awake>=at&&awake-at<=LeaseMs;
        if(needsFocus&&newest is not null&&!rows.Any(p=>p.Focused)){
            s.FocusMissingAt??=awake;
            if(lease&&awake-s.FocusMissingAt.Value<FocusGraceMs)return new(true,"focus_transition_grace",awake-s.VerifiedAt!.Value,true);
            return new(false,"foreground_profile_unconfirmed",awake-newest.Value,false);
        }
        if(newest is null&&all.Any(p=>awake>=p.AwakeTick&&awake-p.AwakeTick<=FreshMs)){s.VerifiedAt=null;s.FocusMissingAt=null;return new(false,"explicit_plan_not_confirmed",null,false);}
        if(lease)return new(true,"heartbeat_grace",awake-s.VerifiedAt!.Value,true);
        return new(false,newest is null?"current_plan_not_confirmed":"collector_and_focus_unconfirmed",newest is {} n?awake-n:null,false);
    }
}
