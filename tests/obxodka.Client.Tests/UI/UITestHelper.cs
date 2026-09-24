namespace obxodka.Client.Tests.UI;

public static class UITestHelper
{
    public static string ResolveAppPath()
    {
        var envPath = Environment.GetEnvironmentVariable("OBXODKA_APP_PATH");
        if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath))
        {
            return envPath;
        }

        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "..", "..", "..", "..", "bin", "Release", "net10.0-windows10.0.19041.0", "win-x64", "obxodka.exe"),
            Path.Combine(baseDir, "..", "..", "..", "..", "bin", "Debug", "net10.0-windows10.0.19041.0", "win-x64", "obxodka.exe"),
            Path.Combine(baseDir, "obxodka.exe")
        };

        foreach (var candidate in candidates)
        {
            try
            {
                var full = Path.GetFullPath(candidate);
                if (File.Exists(full))
                {
                    return full;
                }
            }
            catch
            {

            }
        }

        return "obxodka.exe";
    }
}
