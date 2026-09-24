namespace obxodka.Shared.Themes;

[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(ThemeManifest))]
[JsonSerializable(typeof(ThemeCatalogResponse))]
[JsonSerializable(typeof(ThemeCatalogItem))]
[JsonSerializable(typeof(List<ThemeCatalogItem>))]
[JsonSerializable(typeof(List<ThemeManifest>))]
[JsonSerializable(typeof(CoreThemeTokens))]
[JsonSerializable(typeof(ThemeColors))]
[JsonSerializable(typeof(ThemeGradients))]
[JsonSerializable(typeof(GradientDefinition))]
[JsonSerializable(typeof(ThemeUiTokens))]
[JsonSerializable(typeof(ThemeSounds))]
[JsonSerializable(typeof(ThemeBackground))]
[JsonSerializable(typeof(ThemeDecorations))]
[JsonSerializable(typeof(FrameSlice))]
[JsonSerializable(typeof(ThemeLayout))]
[JsonSerializable(typeof(ThemeVfx))]
[JsonSerializable(typeof(ThemeFeatures))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class ThemeJsonContext : JsonSerializerContext
{
}
