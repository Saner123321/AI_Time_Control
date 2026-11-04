namespace Aict.Core;
public static class BuildMode {
// Historical symbol denotes host-capable binaries, not an automatic deadline.
// The protected installation configuration selects persistent use or a finite trial.
#if HOST_TRIAL
    public const bool HostTrial=true;
#else
    public const bool HostTrial=false;
#endif
    public const int TrialMinutes=30;
}
