using System;
static class IdentityFixture {
    static int Main(string[] args) {
        if(args.Length!=0)return 2;
#if CONTROL
        Console.WriteLine("AICT_CONTROL_OK");
#else
        Console.WriteLine("AICT_FIXTURE_OK");
#endif
        return 0;
    }
}
