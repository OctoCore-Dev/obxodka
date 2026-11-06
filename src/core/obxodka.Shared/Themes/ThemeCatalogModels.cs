namespace obxodka.Shared.Themes;

//ответ каталога доступных тем оформления
public sealed class ThemeCatalogResponse
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = "4.0.0";

    [JsonPropertyName("generatedAt")]
    public string? GeneratedAt { get; set; }

    [JsonPropertyName("totalThemes")]
    public int TotalThemes { get; set; }

    [JsonPropertyName("themes")]
    public List<ThemeCatalogItem> Themes { get; set; } = [];
}

//элемент каталога темы оформления со ссылками на превью и описанием
public sealed class ThemeCatalogItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0.0";

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    [JsonPropertyName("supportedApps")]
    public List<string> SupportedApps { get; set; } = ["*"];

    [JsonPropertyName("iconUrl")]
    public string? IconUrl { get; set; }

    [JsonPropertyName("previewUrl")]
    public string? PreviewUrl { get; set; }

    [JsonPropertyName("primaryColor")]
    public string PrimaryColor { get; set; } = "#0078D4";

    [JsonPropertyName("backgroundColor")]
    public string BackgroundColor { get; set; } = "#0E0E14";

    [JsonPropertyName("accentColor")]
    public string AccentColor { get; set; } = "#00E5FF";

    [JsonPropertyName("features")]
    public ThemeFeatures Features { get; set; } = new();

    [JsonPropertyName("updatedAt")]
    public string? UpdatedAt { get; set; }
}

//флаги поддерживаемых возможностей темы
public sealed class ThemeFeatures
{
    [JsonPropertyName("hasSounds")]
    public bool HasSounds { get; set; }

    [JsonPropertyName("hasVideo")]
    public bool HasVideo { get; set; }

    [JsonPropertyName("hasFrames")]
    public bool HasFrames { get; set; }

    [JsonPropertyName("hasIdolButton")]
    public bool HasIdolButton { get; set; }

    [JsonPropertyName("hasButtonVideo")]
    public bool HasButtonVideo { get; set; }

    [JsonPropertyName("hasParticles")]
    public bool HasParticles { get; set; }

    [JsonPropertyName("hasScreenOverlay")]
    public bool HasScreenOverlay { get; set; }

    [JsonPropertyName("hasCustomFont")]
    public bool HasCustomFont { get; set; }

    [JsonPropertyName("hasReactiveVfx")]
    public bool HasReactiveVfx { get; set; }
}
