using System.Diagnostics;
using Aict.Core;
using Aict.Enforcement;

namespace Aict.Service;

internal sealed class FixturePolicyAdapter(LabConfiguration config) : IApplicationPolicyAdapter {
    public Task<PolicyObservation> ReadAsync(CancellationToken cancellation) => Execute("--policy-read",cancellation);
    public Task<PolicyObservation> ApplyAsync(ApplicationPlan plan,CancellationToken cancellation) {
        string allowed=ApplicationPlanner.HashIdentity(config.TargetSipHash[2..]);
        if(plan.DeniedIdentities.Any(id=>id!=allowed) || plan.DeniedIdentities.Length>1) throw new InvalidOperationException("Only the frozen inert fixture can be denied in this integration build");
        return Execute(plan.DeniedIdentities.IsEmpty?"--policy-allow":"--policy-block",cancellation);
    }
    async Task<PolicyObservation> Execute(string mode,CancellationToken cancellation) {
        using var child=new Process {StartInfo=new ProcessStartInfo(Path.Combine(LabEnvironment.Root,"AictPolicyBridge.exe")) {
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true
        }};
        child.StartInfo.ArgumentList.Add(mode);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);timeout.CancelAfter(TimeSpan.FromSeconds(35));
        child.Start();var stdout=child.StandardOutput.ReadToEndAsync(timeout.Token);var stderr=child.StandardError.ReadToEndAsync(timeout.Token);
        try {
            await child.WaitForExitAsync(timeout.Token);string output=await stdout,error=await stderr;
            if(child.ExitCode!=0) throw new InvalidOperationException("Fixed policy bridge rejected operation: "+error.Trim());
            var result=PolicyJson.Parse<BridgeObservation>(output);
            if(result.RunId!=config.RunId || !result.FixedFixtureOnly) throw new InvalidDataException("Bridge ownership/readback mismatch");
            return new(result.ExePresent,result.CompanionPresent,result.ReadbackBlocked);
        } finally {if(!child.HasExited) {child.Kill(entireProcessTree:true);await child.WaitForExitAsync();}}
    }
    sealed record BridgeObservation(string RunId,bool ExePresent,bool CompanionPresent,bool ReadbackBlocked,bool FixedFixtureOnly);
}
