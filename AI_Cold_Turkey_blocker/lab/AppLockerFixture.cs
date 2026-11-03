// Inert phase-0 sample. No registry, network, policy or user-document access.
using System;
using System.Threading;

static class AppLockerFixture {
    static int Main(string[] args) {
        if(args.Length==1 && args[0]=="--hold") {
            Console.WriteLine("AICT_HOLD_READY"); Console.Out.Flush();
            Thread.Sleep(TimeSpan.FromSeconds(100));
            return 0;
        }
        if(args.Length!=0)return 2;
        Console.WriteLine("AICT_FIXTURE_OK");return 0;
    }
}
