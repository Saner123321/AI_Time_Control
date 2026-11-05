using Aict.Beta;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace Aict.Core.Tests;
[TestClass] public class BrowserReadinessTests {
    static BrowserProof P(long tick,bool focus=true,string hash="plan")=>new("profile",hash,focus,tick);
    [TestMethod] public void FocusTransitionDoesNotImmediatelyDisconnectAConfirmedBrowser(){
        var t=new BrowserReadinessTracker();Assert.IsTrue(t.Evaluate("Edge",true,"plan",[P(0)],0,true,"Edge").Ready);
        Assert.IsTrue(t.Evaluate("Edge",true,"plan",[P(1000,false)],1000,true,"Edge").Ready);
        Assert.IsTrue(t.Evaluate("Edge",true,"plan",[P(1500,false)],1500,true,"Edge").InGrace);
        Assert.IsFalse(t.Evaluate("Edge",true,"plan",[P(3100,false)],3100,true,"Edge").Ready);
        Assert.IsTrue(t.Evaluate("Edge",true,"plan",[P(3200)],3200,true,"Edge").Ready);
    }
    [TestMethod] public void FreshBackgroundPulsesCannotIndefinitelyExtendUnmanagedForegroundGrace(){
        var t=new BrowserReadinessTracker();t.Evaluate("Edge",true,"plan",[P(0)],0,true,"Edge");
        t.Evaluate("Edge",true,"plan",[P(1000,false)],1000,true,"Edge");for(long now=3100;now<10000;now+=500)Assert.IsFalse(t.Evaluate("Edge",true,"plan",[P(now,false)],now,true,"Edge").Ready);
    }
    [TestMethod] public void HeartbeatLeaseIsBoundedAndDoesNotRenewItselfOnPolling(){
        var t=new BrowserReadinessTracker();t.Evaluate("Edge",true,"plan",[P(0)],0,true,"Edge");
        Assert.IsTrue(t.Evaluate("Edge",true,"plan",[P(0)],4000,false,null).Ready);Assert.IsFalse(t.Evaluate("Edge",true,"plan",[P(0)],5001,false,null).Ready);
    }
    [TestMethod] public void NewPlanOrExplicitFailedAcknowledgementNeverReusesOldLease(){
        var t=new BrowserReadinessTracker();t.Evaluate("Edge",true,"plan",[P(0)],0,true,"Edge");Assert.IsFalse(t.Evaluate("Edge",true,"new-plan",[P(0)],100,true,"Edge").Ready);
        t.Evaluate("Edge",true,"plan",[P(200)],200,true,"Edge");Assert.IsFalse(t.Evaluate("Edge",true,"plan",[P(300,true,"")],300,true,"Edge").Ready);
    }
    [TestMethod] public void SleepDoesNotConsumeAwakeLeaseAndNewWakeScheduleStillRequiresAck(){
        var t=new BrowserReadinessTracker();t.Evaluate("Edge",true,"plan",[P(100)],100,true,"Edge");Assert.IsTrue(t.Evaluate("Edge",true,"plan",[P(100)],200,false,null).Ready);Assert.IsFalse(t.Evaluate("Edge",true,"wake-plan",[P(100)],200,false,null).Ready);
    }
    [TestMethod] public void NoProtectionRequiredReleasesGateButDoesNotCreateAnAcknowledgement(){
        var t=new BrowserReadinessTracker();Assert.IsTrue(t.Evaluate("Edge",false,"plan",[],100,true,null).Ready);Assert.IsFalse(t.Evaluate("Edge",true,"plan",[],101,true,"Edge").Ready);
    }
    [TestMethod] public void BackgroundManagedBrowserCanDownloadWithoutClaimingForegroundUsage(){
        var t=new BrowserReadinessTracker();Assert.IsTrue(t.Evaluate("Edge",true,"plan",[P(100,false)],100,true,null).Ready);Assert.IsFalse(t.Evaluate("Chrome",true,"plan",[],100,true,null).Ready);
    }
    [TestMethod] public void ExplicitFailureRevokesLeaseEvenAfterTheFailedSampleBecomesStale(){
        var t=new BrowserReadinessTracker();t.Evaluate("Edge",true,"plan",[P(0)],0,true,"Edge");Assert.IsFalse(t.Evaluate("Edge",true,"plan",[P(100,true,"")],100,true,"Edge").Ready);Assert.IsFalse(t.Evaluate("Edge",true,"plan",[P(100,true,"")],4000,false,null).Ready);
    }
    [TestMethod] public void AFailedFocusedProfileCannotBorrowAnotherProfilesAcknowledgement(){
        var t=new BrowserReadinessTracker();t.Evaluate("Edge",true,"plan",[P(0)],0,true,"Edge");var other=new BrowserProof("other","",true,100);Assert.IsFalse(t.Evaluate("Edge",true,"plan",[P(0,false),other],100,true,"Edge").Ready);
    }
}
