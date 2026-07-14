namespace obxodka.Config;

public static class AppConfig
{
    public static string ApiBaseUrl { get; set; } = "https://obxodka.one/";
    public static string BaseUrl => ApiBaseUrl;
    public static string ApiUrl(string endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return $"{ApiBaseUrl.AsSpan().TrimEnd('/')}/{endpoint.AsSpan().TrimStart('/')}";
    }
}
