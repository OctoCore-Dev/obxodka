namespace obxodka.Client.Tests;

//тесты парсера intent-действий и deep-link команд для google gemini и samsung bixby
public sealed class VoiceAssistantCommandParserTests
{
    [Theory]
    [InlineData(VoiceAssistantCommandParser.ActionConnectVpn, VoiceAssistantCommand.Connect)]
    [InlineData(VoiceAssistantCommandParser.ActionDisconnectVpn, VoiceAssistantCommand.Disconnect)]
    [InlineData(VoiceAssistantCommandParser.ActionStatusVpn, VoiceAssistantCommand.Status)]
    public void ParseShouldRecognizeExplicitIntentActions(string action, VoiceAssistantCommand expected)
    {
        var actual = VoiceAssistantCommandParser.Parse(action, null);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("obxodka://assistant/connect", VoiceAssistantCommand.Connect)]
    [InlineData("obxodka://assistant/disconnect", VoiceAssistantCommand.Disconnect)]
    [InlineData("obxodka://assistant/status", VoiceAssistantCommand.Status)]
    [InlineData("obxodka://assistant?action=connect", VoiceAssistantCommand.Connect)]
    [InlineData("obxodka://assistant?command=disconnect", VoiceAssistantCommand.Disconnect)]
    [InlineData("obxodka://assistant?feature=%D0%B2%D0%BA%D0%BB%D1%8E%D1%87%D0%B8%20vpn", VoiceAssistantCommand.Connect)]
    [InlineData("obxodka://assistant?feature=%D0%B2%D1%8B%D0%BA%D0%BB%D1%8E%D1%87%D0%B8%20vpn", VoiceAssistantCommand.Disconnect)]
    [InlineData("obxodka://assistant?feature=%D1%81%D1%82%D0%B0%D1%82%D1%83%D1%81", VoiceAssistantCommand.Status)]
    public void ParseFromUriShouldRecognizeAssistantDeepLinks(string uri, VoiceAssistantCommand expected)
    {
        var actual = VoiceAssistantCommandParser.Parse("android.intent.action.VIEW", uri);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("Включи VPN в Обходке", VoiceAssistantCommand.Connect)]
    [InlineData("Подключи Обходку", VoiceAssistantCommand.Connect)]
    [InlineData("Выключи VPN в Обходке", VoiceAssistantCommand.Disconnect)]
    [InlineData("Отключи Обходку", VoiceAssistantCommand.Disconnect)]
    [InlineData("Проверь статус Обходки", VoiceAssistantCommand.Status)]
    [InlineData("turn off vpn", VoiceAssistantCommand.Disconnect)]
    [InlineData("turn on vpn", VoiceAssistantCommand.Connect)]
    [InlineData("check status", VoiceAssistantCommand.Status)]
    public void ParseFromTokenShouldRecognizeRussianAndEnglishVoicePhrases(string phrase, VoiceAssistantCommand expected)
    {
        var actual = VoiceAssistantCommandParser.Parse(null, null, phrase);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("https://example.com/assistant/connect")]
    [InlineData("obxodka://login?email=a@b.com&code=123456")]
    [InlineData("obxodka://assistant/unknown_action")]
    [InlineData("")]
    [InlineData(null)]
    public void ParseShouldRejectUnrelatedOrInvalidInputs(string? uri)
    {
        var actual = VoiceAssistantCommandParser.Parse("android.intent.action.VIEW", uri);
        Assert.Equal(VoiceAssistantCommand.Unknown, actual);
    }

    [Fact]
    public void AppPreferenceKeysShouldExposeVoiceAssistantEnabledKey()
    {
        Assert.Equal("voice_assistant_enabled", AppPreferenceKeys.VoiceAssistantEnabled);
        Assert.Equal("selected_server_ip", AppPreferenceKeys.SelectedServerIp);
        Assert.Equal("VpnDisclosureAccepted", AppPreferenceKeys.VpnDisclosureAccepted);
    }
}
