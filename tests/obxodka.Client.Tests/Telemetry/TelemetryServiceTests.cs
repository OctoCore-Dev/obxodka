namespace obxodka.Client.Tests.Telemetry;

[Trait("Category", "Unit")]
public sealed class TelemetryServiceTests
{
    [Fact]
    public void IsTelemetryEnabledRespectsPreferences()
    {
        try
        {
            PlatformServices.Preferences.Set(AppPreferenceKeys.UseTelemetry, true);
            Assert.True(TelemetryService.Instance.IsTelemetryEnabled);

            PlatformServices.Preferences.Set(AppPreferenceKeys.UseTelemetry, false);
            Assert.False(TelemetryService.Instance.IsTelemetryEnabled);
        }
        finally
        {
            PlatformServices.Preferences.Set(AppPreferenceKeys.UseTelemetry, true);
        }
    }

    [Fact]
    public async Task TrackFatalCrashAsyncWritesToCrashLogFile()
    {
        var crashPath = AppLogger.CrashLogFilePath;
        try
        {
            await TelemetryService.Instance.TrackFatalCrashAsync("Test fatal incident", "at UnitTests.Execute()").ConfigureAwait(false);
            Assert.True(File.Exists(crashPath));

            var content = await File.ReadAllTextAsync(crashPath).ConfigureAwait(false);
            Assert.Contains("Test fatal incident", content);
        }
        finally
        {
            if (File.Exists(crashPath))
            {
                File.Delete(crashPath);
            }
        }
    }

    [Fact]
    public void AppLogger_LogMethodsWriteToRecentLogs()
    {
        AppLogger.LogPass("Unit test pass message");
        AppLogger.LogWarning("Unit test warning message");
        AppLogger.LogError("Unit test error message");

        var recent = AppLogger.GetRecentLogs(50);
        Assert.Contains("Unit test pass message", recent);
        Assert.Contains("Unit test warning message", recent);
        Assert.Contains("Unit test error message", recent);
    }
}
