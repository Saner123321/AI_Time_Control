using Aict.Enforcement;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace Aict.Core.Tests;
[TestClass] public sealed class BrowserPolicySlotTests {
    [TestMethod] public void PreservesExistingSlotsAndBoundsPolicyList() {
        Assert.AreEqual("3",BrowserPolicySlot.Available(["1","2","4"]));
        Assert.ThrowsExactly<InvalidOperationException>(()=>BrowserPolicySlot.Available(Enumerable.Range(1,1000).Select(i=>i.ToString())));
    }
    [TestMethod] public void CleanupOnlyMatchesExactOwnedDomainAndType() {
        Assert.IsTrue(BrowserPolicySlot.OwnValue("example.com","example.com",true));
        Assert.IsFalse(BrowserPolicySlot.OwnValue("example.com","different.com",true));
        Assert.IsFalse(BrowserPolicySlot.OwnValue("example.com","example.com",false));
        Assert.IsFalse(BrowserPolicySlot.OwnValue("different.com","different.com",true));
    }
    [TestMethod] public void CannotTurnBrowserSelectionIntoArbitraryRegistryPath() {
        Assert.AreEqual(@"SOFTWARE\Policies\Microsoft\Edge",BrowserPolicySlot.PathFor("Edge"));
        Assert.AreEqual(@"SOFTWARE\Policies\Google\Chrome",BrowserPolicySlot.PathFor("Chrome"));
        Assert.ThrowsExactly<ArgumentException>(()=>BrowserPolicySlot.PathFor(@"..\Windows"));
    }
}
