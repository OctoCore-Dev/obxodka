namespace obxodka.Client.Tests.UI;

public static class UiTestHelper
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
            Path.Join(baseDir, "..", "..", "..", "..", "bin", "Release", "net11.0-windows10.0.19041.0", "win-x64", "obxodka.exe"),
            Path.Join(baseDir, "..", "..", "..", "..", "bin", "Debug", "net11.0-windows10.0.19041.0", "win-x64", "obxodka.exe"),
            Path.Join(baseDir, "obxodka.exe")
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
            catch (IOException)
            {
                //путь кандидата недоступен при проверке
            }
            catch (ArgumentException)
            {
                //некорректный шаблон пути кандидата
            }
            catch (NotSupportedException)
            {
                //формат пути не поддерживается платформой
            }
        }

        return "obxodka.exe";
    }
}
