namespace obxodka.Config;

public static class AppConfig
{
    public const string ApiBaseUrl = "https://obxodka.one/";
    public static readonly string BaseUrl = ApiBaseUrl;

    public static string ApiUrl(string endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return $"{ApiBaseUrl.AsSpan().TrimEnd('/')}/{endpoint.AsSpan().TrimStart('/')}";
    }
}
