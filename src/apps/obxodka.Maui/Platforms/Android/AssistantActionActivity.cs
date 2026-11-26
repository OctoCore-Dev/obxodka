using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Widget;
using Activity = Android.App.Activity;

namespace obxodka;

//неблокирующая точка входа для голосовых команд google gemini, google assistant и samsung bixby
[Activity(
    Name = "obxodka.AssistantActionActivity",
    Exported = true,
    ExcludeFromRecents = true,
    NoHistory = true,
    LaunchMode = LaunchMode.SingleInstance,
    Theme = "@android:style/Theme.Translucent.NoTitleBar")]
[IntentFilter(
    [
        VoiceAssistantCommandParser.ActionConnectVpn,
        VoiceAssistantCommandParser.ActionDisconnectVpn,
        VoiceAssistantCommandParser.ActionStatusVpn
    ],
    Categories = [Intent.CategoryDefault])]
[IntentFilter(
    [Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = VoiceAssistantCommandParser.DeepLinkScheme,
    DataHost = VoiceAssistantCommandParser.DeepLinkHost)]
[SupportedOSPlatform("android30.0")]
public sealed class AssistantActionActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        try
        {
            var intent = Intent;
            var featureOrCommand = intent?.GetStringExtra("feature")
                                   ?? intent?.GetStringExtra("command")
                                   ?? intent?.GetStringExtra("action");

            var command = VoiceAssistantCommandParser.Parse(intent?.Action, intent?.DataString, featureOrCommand);
            var appContext = ApplicationContext ?? this;

            var feedbackText = AndroidBackgroundVpnController.DispatchAssistantCommand(
                appContext,
                command,
                actionValue =>
                {
                    try
                    {
                        using var launchIntent = AndroidBackgroundVpnController.BuildMainActivityIntent(appContext, actionValue);
                        appContext.StartActivity(launchIntent);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.LogWarning($"[ASSISTANT] Не удалось открыть MainActivity: {ex.Message}");
                    }
                });

            if (!string.IsNullOrWhiteSpace(feedbackText))
            {
                Toast.MakeText(appContext, feedbackText, ToastLength.Short)?.Show();
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[ASSISTANT ACTIVITY ERROR] {ex.Message}", ex);
        }
        finally
        {
            Finish();
        }
    }
}
