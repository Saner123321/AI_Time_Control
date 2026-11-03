// Also compiled with the inbox Framework compiler for the VM graphical experiment.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
namespace Aict.Enforcement {
    public static class BrowserPolicySlot {
        public const string TestDomain="example.com";
        public static string PathFor(string browser) {
            if(browser=="Edge") return @"SOFTWARE\Policies\Microsoft\Edge";
            if(browser=="Chrome") return @"SOFTWARE\Policies\Google\Chrome";
            throw new ArgumentException("Unknown browser");
        }
        public static string Available(IEnumerable<string> occupied) {
            var names=new HashSet<string>(occupied,StringComparer.OrdinalIgnoreCase);
            if(names.Count>=1000) throw new InvalidOperationException("Browser policy list is full");
            for(int n=1;n<=1000;n++) {string name=n.ToString(CultureInfo.InvariantCulture);if(!names.Contains(name))return name;}
            throw new InvalidOperationException("No policy slot available");
        }
        public static bool OwnValue(string planned,string current,bool stringKind) {return planned==TestDomain && stringKind && current==TestDomain;}
    }
}
