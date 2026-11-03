namespace Aict.Beta;
public static class HostTrialSafety {
    public static bool NeedsIndependentCleanup(Status? status,DateTimeOffset now,DateTimeOffset lastStatusWrite,bool serviceRunning)=>
        !(serviceRunning && now>=lastStatusWrite && now-lastStatusWrite<TimeSpan.FromSeconds(5) && status is not null && status.TrialExpired && !status.Policy.Groups.Any(g=>g.Enabled) && status.Pending is null && status.Execution.Confirmed && status.Execution.Denied.IsEmpty && status.Execution.AllowGroups==0 && !status.Websites.Required && !status.Websites.FirewallClosed && status.Websites.GateConfirmed && status.Websites.Fault is null);
}
