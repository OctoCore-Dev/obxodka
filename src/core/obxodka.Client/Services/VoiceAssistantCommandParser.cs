namespace obxodka.Services;

//тип команды голосового ассистента
public enum VoiceAssistantCommand
{
    Unknown = 0,
    Connect = 1,
    Disconnect = 2,
    Status = 3
}

//парсер intent-действий и deep-link ссылок для google gemini и samsung bixby
public static class VoiceAssistantCommandParser
{
    public const string ActionConnectVpn = "com.octocore.obxodka.ACTION_CONNECT_VPN";
    public const string ActionDisconnectVpn = "com.octocore.obxodka.ACTION_DISCONNECT_VPN";
    public const string ActionStatusVpn = "com.octocore.obxodka.ACTION_STATUS_VPN";

    public const string DeepLinkScheme = "obxodka";
    public const string DeepLinkHost = "assistant";

    //разбор команды из intent action, uri и текстового параметра app actions
    public static VoiceAssistantCommand Parse(string? action, string? dataUri, string? featureOrCommand = null)
    {
        if (!string.IsNullOrWhiteSpace(action))
        {
            if (string.Equals(action, ActionConnectVpn, StringComparison.OrdinalIgnoreCase))
            {
                return VoiceAssistantCommand.Connect;
            }

            if (string.Equals(action, ActionDisconnectVpn, StringComparison.OrdinalIgnoreCase))
            {
                return VoiceAssistantCommand.Disconnect;
            }

            if (string.Equals(action, ActionStatusVpn, StringComparison.OrdinalIgnoreCase))
            {
                return VoiceAssistantCommand.Status;
            }
        }

        var fromUri = ParseFromUri(dataUri);
        return fromUri != VoiceAssistantCommand.Unknown ? fromUri : ParseFromToken(featureOrCommand);
    }

    //разбор deep-link вида obxodka://assistant/connect или obxodka://assistant?action=status
    public static VoiceAssistantCommand ParseFromUri(string? rawUri)
    {
        if (string.IsNullOrWhiteSpace(rawUri))
        {
            return VoiceAssistantCommand.Unknown;
        }

        var trimmed = rawUri.Trim().Trim('"');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return VoiceAssistantCommand.Unknown;
        }

        if (!string.Equals(uri.Scheme, DeepLinkScheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, DeepLinkHost, StringComparison.OrdinalIgnoreCase))
        {
            return VoiceAssistantCommand.Unknown;
        }

        var pathToken = uri.AbsolutePath.Trim('/');
        var byPath = ParseFromToken(pathToken);
        if (byPath != VoiceAssistantCommand.Unknown)
        {
            return byPath;
        }

        if (string.IsNullOrWhiteSpace(uri.Query))
        {
            return VoiceAssistantCommand.Unknown;
        }

        var pairs = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var kv in pairs.Select(static pair => pair.Split('=', 2)).Where(static kv => kv.Length == 2))
        {
            var key = Uri.UnescapeDataString(kv[0]).Trim();
            if (!string.Equals(key, "action", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(key, "command", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(key, "feature", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = Uri.UnescapeDataString(kv[1]).Trim();
            var parsed = ParseFromToken(value);
            if (parsed != VoiceAssistantCommand.Unknown)
            {
                return parsed;
            }
        }

        return VoiceAssistantCommand.Unknown;
    }

    //разбор короткого токена или фразы на русском и английском
    public static VoiceAssistantCommand ParseFromToken(string? rawToken)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return VoiceAssistantCommand.Unknown;
        }

        var token = rawToken.Trim().ToLowerInvariant();

        //сначала проверяем отключение, так как "отключи" и "выключи" пересекаются с "включи" по подстроке
        return IsDisconnectToken(token)
            ? VoiceAssistantCommand.Disconnect
            : IsConnectToken(token)
                ? VoiceAssistantCommand.Connect
                : IsStatusToken(token)
                    ? VoiceAssistantCommand.Status
                    : VoiceAssistantCommand.Unknown;
    }

    private static bool IsDisconnectToken(string token) =>
        token is "disconnect" or "stop" or "off" or "disable" or "disconnect_vpn" or "stop_vpn" ||
        token.Contains("выключ", StringComparison.Ordinal) ||
        token.Contains("отключ", StringComparison.Ordinal) ||
        token.Contains("останов", StringComparison.Ordinal) ||
        token.Contains("disconnect", StringComparison.Ordinal) ||
        token.Contains("turn off", StringComparison.Ordinal);

    private static bool IsConnectToken(string token) =>
        token is "connect" or "start" or "on" or "enable" or "connect_vpn" or "start_vpn" ||
        token.Contains("включ", StringComparison.Ordinal) ||
        token.Contains("подключ", StringComparison.Ordinal) ||
        token.Contains("запуст", StringComparison.Ordinal) ||
        token.Contains("connect", StringComparison.Ordinal) ||
        token.Contains("turn on", StringComparison.Ordinal);

    private static bool IsStatusToken(string token) =>
        token is "status" or "state" or "check" or "status_vpn" ||
        token.Contains("статус", StringComparison.Ordinal) ||
        token.Contains("состоян", StringComparison.Ordinal) ||
        token.Contains("провер", StringComparison.Ordinal) ||
        token.Contains("status", StringComparison.Ordinal);
}
