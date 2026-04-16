namespace obxodka.Core;
public static class AppConfig
{
    public static readonly string ApiBaseUrl = "https://obxodka.one/";
    public static readonly string BaseUrl = ApiBaseUrl;
    public static string ApiUrl(string endpoint)
        => $"{ApiBaseUrl.TrimEnd('/')}/{endpoint.TrimStart('/')}";
}