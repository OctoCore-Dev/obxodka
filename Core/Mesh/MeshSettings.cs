namespace obxodka.Core.Mesh;

public static class MeshSettings
{
    public static bool MeshEnabled
    {
        get => Preferences.Get("Mesh_Enabled", false);
        set => Preferences.Set("Mesh_Enabled", value);
    }

    public static bool RelayEnabled
    {
        get => Preferences.Get("Relay_Enabled", false);
        set => Preferences.Set("Relay_Enabled", value);
    }

    public static int RelaySpeedMbps
    {
        get => Preferences.Get("Relay_SpeedMbps", 10);
        set => Preferences.Set("Relay_SpeedMbps", Math.Clamp(value, 1, 100));
    }

    public static int RelayMonthlyLimitGb
    {
        get => Preferences.Get("Relay_MonthlyLimitGb", 10);
        set => Preferences.Set("Relay_MonthlyLimitGb", value);
    }

    public static int RelayMaxClients
    {
        get => Preferences.Get("Relay_MaxClients", 3);
        set => Preferences.Set("Relay_MaxClients", Math.Clamp(value, 1, 10));
    }

    public static string ReferralCode
    {
        get => Preferences.Get("User_ReferralCode", string.Empty);
        set => Preferences.Set("User_ReferralCode", value);
    }
}
