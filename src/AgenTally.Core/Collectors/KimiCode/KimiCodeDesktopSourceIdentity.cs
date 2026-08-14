namespace AgenTally.Core.Collectors.KimiCode;

public static class KimiCodeDesktopSourceIdentity
{
    public static string? DefaultHome()
    {
        string roamingAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData);
        return string.IsNullOrWhiteSpace(roamingAppData)
            ? null
            : KimiCodeSourceIdentity.NormalizePath(Path.Combine(
                roamingAppData,
                "kimi-desktop",
                "daimon-share",
                "daimon",
                "runtime",
                "kimi-code",
                "home"));
    }

    public static string InstanceId(string kimiHome) =>
        KimiCodeSourceIdentity.InstanceId(kimiHome, "desktop-work");
}
