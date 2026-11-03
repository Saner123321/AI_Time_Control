namespace Aict.Core;
public static class BuildMode {
#if HOST_TRIAL
    public const bool HostTrial=true;
#else
    public const bool HostTrial=false;
#endif
    public const int TrialMinutes=30;
}
