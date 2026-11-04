using Aict.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace Aict.Core.Tests;
[TestClass] public sealed class ContinuousUseTests {
    [TestMethod] public void ContinuousModeRequiresExplicitProtectedFlagAndNoDeadline(){
        HostInstallation.Validate(true,true,null);
        HostInstallation.Validate(true,false,DateTimeOffset.UtcNow.AddMinutes(30));
        Assert.ThrowsExactly<InvalidDataException>(()=>HostInstallation.Validate(true,false,null));
        Assert.ThrowsExactly<InvalidDataException>(()=>HostInstallation.Validate(true,true,DateTimeOffset.UtcNow));
        Assert.ThrowsExactly<InvalidDataException>(()=>HostInstallation.Validate(false,true,null));
    }
    [TestMethod] public void ObsoleteSafetyCanNeverTriggerCleanupDuringContinuousUse(){
        var now=DateTimeOffset.UtcNow;
        Assert.IsFalse(HostInstallation.ShouldRunTrialSafety(true,null,now.AddDays(365)));
        Assert.IsFalse(HostInstallation.ShouldRunTrialSafety(true,now.AddMinutes(-1),now));
        Assert.IsFalse(HostInstallation.ShouldRunTrialSafety(false,null,now));
        Assert.IsFalse(HostInstallation.ShouldRunTrialSafety(false,now.AddMinutes(1),now));
        Assert.IsTrue(HostInstallation.ShouldRunTrialSafety(false,now,now));
    }
}
