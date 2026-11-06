namespace obxodka.Shared.Themes;

//манифест темы оформления со спецификацией токенов, цветов, эффектов и звуков
public sealed class ThemeManifest
{
    [JsonPropertyName("$schema")]
    public string? Schema { get; set; }

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0.0";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    [JsonPropertyName("supportedApps")]
    public List<string> SupportedApps { get; set; } = ["*"];

    [JsonPropertyName("core")]
    public CoreThemeTokens Core { get; set; } = new();

    [JsonPropertyName("background")]
    public ThemeBackground? Background { get; set; }

    [JsonPropertyName("decorations")]
    public ThemeDecorations? Decorations { get; set; }

    [JsonPropertyName("layout")]
    public ThemeLayout? Layout { get; set; }

    [JsonPropertyName("vfx")]
    public ThemeVfx? Vfx { get; set; }

    [JsonPropertyName("apps")]
    public Dictionary<string, JsonElement>? Apps { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? CustomProperties { get; set; }
}
