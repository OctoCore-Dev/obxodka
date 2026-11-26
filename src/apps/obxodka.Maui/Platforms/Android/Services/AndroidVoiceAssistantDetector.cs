using Android.Content;
using Android.Content.PM;
using Android.Provider;

namespace obxodka;

//тип обнаруженного голосового ассистента на android-устройстве
internal enum DetectedVoiceAssistantKind
{
    None = 0,
    GoogleGemini = 1,
    GoogleAssistant = 2,
    SamsungBixby = 3
}

//результат автоопределения системного голосового помощника
internal sealed record VoiceAssistantDetectionResult(
    DetectedVoiceAssistantKind Kind,
    string BadgeTitle,
    bool IsDefaultSystemAssistant,
    bool HasBixbyRoutines);

//детектор установленного и системного голосового помощника через packagemanager и settings.secure
[SupportedOSPlatform("android30.0")]
internal static class AndroidVoiceAssistantDetector
{
    private const string GeminiPackage = "com.google.android.apps.bard";
    private const string GoogleAppPackage = "com.google.android.googlequicksearchbox";
    private const string BixbyAgentPackage = "com.samsung.android.bixby.agent";
    private const string BixbyRoutinesPackage = "com.samsung.android.app.routines";

    //определение активного или доступного голосового ассистента на телефоне
    public static VoiceAssistantDetectionResult Detect(Context context)
    {
        var appContext = context.ApplicationContext ?? context;
        var defaultComponent = ReadDefaultAssistantComponent(appContext);
        var defaultLower = defaultComponent?.ToLowerInvariant() ?? string.Empty;

        var hasGemini = IsPackageEnabled(appContext, GeminiPackage);
        var hasGoogleApp = IsPackageEnabled(appContext, GoogleAppPackage);
        var hasBixby = IsPackageEnabled(appContext, BixbyAgentPackage);
        var hasBixbyRoutines = IsPackageEnabled(appContext, BixbyRoutinesPackage);

        //сначала учитываем системного ассистента по умолчанию в настройках android
        if ((defaultLower.Contains(BixbyAgentPackage, StringComparison.Ordinal) ||
             defaultLower.Contains("bixby", StringComparison.Ordinal)) &&
            hasBixby)
        {
            return new VoiceAssistantDetectionResult(
                DetectedVoiceAssistantKind.SamsungBixby,
                "Samsung Bixby",
                IsDefaultSystemAssistant: true,
                HasBixbyRoutines: hasBixbyRoutines);
        }

        if ((defaultLower.Contains(GeminiPackage, StringComparison.Ordinal) ||
             defaultLower.Contains("gemini", StringComparison.Ordinal)) &&
            (hasGemini || hasGoogleApp))
        {
            return new VoiceAssistantDetectionResult(
                DetectedVoiceAssistantKind.GoogleGemini,
                "Google Gemini",
                IsDefaultSystemAssistant: true,
                HasBixbyRoutines: hasBixbyRoutines);
        }

        if (defaultLower.Contains(GoogleAppPackage, StringComparison.Ordinal) ||
            defaultLower.Contains("google", StringComparison.Ordinal))
        {
            if (hasGemini)
            {
                return new VoiceAssistantDetectionResult(
                    DetectedVoiceAssistantKind.GoogleGemini,
                    "Google Gemini",
                    IsDefaultSystemAssistant: true,
                    HasBixbyRoutines: hasBixbyRoutines);
            }

            if (hasGoogleApp)
            {
                return new VoiceAssistantDetectionResult(
                    DetectedVoiceAssistantKind.GoogleAssistant,
                    "Google Gemini / Assistant",
                    IsDefaultSystemAssistant: true,
                    HasBixbyRoutines: hasBixbyRoutines);
            }
        }

        //если ассистент по умолчанию не выбран, считаем что он не настроен
        if (string.IsNullOrWhiteSpace(defaultComponent))
        {
            return new VoiceAssistantDetectionResult(
                DetectedVoiceAssistantKind.None,
                "Не настроен",
                IsDefaultSystemAssistant: false,
                HasBixbyRoutines: hasBixbyRoutines);
        }

        //если выбран сторонний или нестандартный компонент, определяем по установленным пакетам
        return hasGemini
            ? new VoiceAssistantDetectionResult(
                DetectedVoiceAssistantKind.GoogleGemini,
                "Google Gemini",
                IsDefaultSystemAssistant: false,
                HasBixbyRoutines: hasBixbyRoutines)
            : hasBixby
                ? new VoiceAssistantDetectionResult(
                    DetectedVoiceAssistantKind.SamsungBixby,
                    "Samsung Bixby",
                    IsDefaultSystemAssistant: false,
                    HasBixbyRoutines: hasBixbyRoutines)
                : hasGoogleApp
                    ? new VoiceAssistantDetectionResult(
                        DetectedVoiceAssistantKind.GoogleAssistant,
                        "Google Gemini / Assistant",
                        IsDefaultSystemAssistant: false,
                        HasBixbyRoutines: hasBixbyRoutines)
                    : new VoiceAssistantDetectionResult(
                        DetectedVoiceAssistantKind.None,
                        "Не настроен",
                        IsDefaultSystemAssistant: false,
                        HasBixbyRoutines: hasBixbyRoutines);
    }

    //открытие системного экрана выбора голосового помощника android
    public static void OpenAndroidAssistantSettings(Context context)
    {
        var appContext = context.ApplicationContext ?? context;
        string[] actions =
        [
            Settings.ActionVoiceInputSettings,
            Settings.ActionManageDefaultAppsSettings,
            Settings.ActionSettings
        ];

        foreach (var action in actions)
        {
            try
            {
                using var intent = new Intent(action);
                _ = intent.AddFlags(ActivityFlags.NewTask);
                appContext.StartActivity(intent);
                return;
            }
            catch
            {
                //пробуем следующий системный экран настроек
            }
        }
    }

    //чтение компонента системного ассистента из settings.secure
    private static string? ReadDefaultAssistantComponent(Context context)
    {
        try
        {
            var cr = context.ContentResolver;
            if (cr is null)
            {
                return null;
            }

            var assistant = Settings.Secure.GetString(cr, "assistant");
            if (!string.IsNullOrWhiteSpace(assistant))
            {
                return assistant;
            }

            var voiceService = Settings.Secure.GetString(cr, "voice_interaction_service");
            return !string.IsNullOrWhiteSpace(voiceService) ? voiceService : null;
        }
        catch
        {
            return null;
        }
    }

    //проверка что пакет ассистента установлен и включен на устройстве
    private static bool IsPackageEnabled(Context context, string packageName)
    {
        try
        {
            var pm = context.PackageManager;
            if (pm is null)
            {
                return false;
            }

            var info = OperatingSystem.IsAndroidVersionAtLeast(33)
                ? pm.GetApplicationInfo(packageName, PackageManager.ApplicationInfoFlags.Of(0))
#pragma warning disable CA1422
                : pm.GetApplicationInfo(packageName, 0);
#pragma warning restore CA1422

            return info is { Enabled: true };
        }
        catch
        {
            return false;
        }
    }
}
