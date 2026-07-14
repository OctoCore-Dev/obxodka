using Android.App;
using Android.Content;
using Android.Content.PM;

namespace obxodka.Platforms.Android;

[Activity(NoHistory = true, LaunchMode = LaunchMode.SingleTop, Exported = true)]
[IntentFilter([Intent.ActionView],
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataScheme = "com.googleusercontent.apps.21422577555-skjvfodov46edndkr5ak36fl9e30aqi0")]
public class WebAuthenticationCallbackActivity : WebAuthenticatorCallbackActivity
{
}
