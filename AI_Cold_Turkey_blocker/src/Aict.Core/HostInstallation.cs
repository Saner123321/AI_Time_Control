namespace Aict.Core;

// This mode comes only from the administrator-protected installation configuration.
// Normal rule editing has no API for changing it.
public static class HostInstallation {
    public static void Validate(bool hostEnabled,bool continuousUse,DateTimeOffset? trialEndsUtc) {
        if(!hostEnabled || continuousUse==(trialEndsUtc is not null))
            throw new InvalidDataException("主机安装模式无效：持续使用没有测试截止时间；短时测试必须有截止时间。");
    }
    public static bool ShouldRunTrialSafety(bool continuousUse,DateTimeOffset? trialEndsUtc,DateTimeOffset now)
        =>!continuousUse && trialEndsUtc is {} end && now>=end;
}
