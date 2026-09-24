namespace obxodka.Shared.Themes;

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

public sealed class CoreThemeTokens
{
    [JsonPropertyName("colors")]
    public ThemeColors Colors { get; set; } = new();

    [JsonPropertyName("gradients")]
    public ThemeGradients? Gradients { get; set; }

    [JsonPropertyName("ui")]
    public ThemeUiTokens? Ui { get; set; }

    [JsonPropertyName("sounds")]
    public ThemeSounds? Sounds { get; set; }
}

public sealed class ThemeColors
{
    [JsonPropertyName("BgBase")]
    public string BgBase { get; set; } = "#0E0E14";

    [JsonPropertyName("BgSurface")]
    public string BgSurface { get; set; } = "#181824";

    [JsonPropertyName("BgElevated")]
    public string? BgElevated { get; set; }

    [JsonPropertyName("BgInput")]
    public string? BgInput { get; set; }

    [JsonPropertyName("Primary")]
    public string Primary { get; set; } = "#0078D4";

    [JsonPropertyName("PrimaryBright")]
    public string? PrimaryBright { get; set; }

    [JsonPropertyName("PrimaryDim")]
    public string? PrimaryDim { get; set; }

    [JsonPropertyName("Accent")]
    public string? Accent { get; set; }

    [JsonPropertyName("TextPrimary")]
    public string TextPrimary { get; set; } = "#FFFFFF";

    [JsonPropertyName("TextSecondary")]
    public string? TextSecondary { get; set; }

    [JsonPropertyName("TextMuted")]
    public string? TextMuted { get; set; }

    [JsonPropertyName("SolidBorderDark")]
    public string? SolidBorderDark { get; set; }

    [JsonPropertyName("BorderSubtle")]
    public string? BorderSubtle { get; set; }

    [JsonPropertyName("BorderMedium")]
    public string? BorderMedium { get; set; }

    [JsonPropertyName("Success")]
    public string? Success { get; set; }

    [JsonPropertyName("Warning")]
    public string? Warning { get; set; }

    [JsonPropertyName("Error")]
    public string? Error { get; set; }
}

public sealed class ThemeGradients
{
    [JsonPropertyName("PrimaryGradient")]
    public GradientDefinition? PrimaryGradient { get; set; }

    [JsonPropertyName("SurfaceGradient")]
    public GradientDefinition? SurfaceGradient { get; set; }
}

public sealed class GradientDefinition
{
    [JsonPropertyName("angle")]
    public double Angle { get; set; } = 45;

    [JsonPropertyName("stops")]
    public List<string> Stops { get; set; } = [];
}

public sealed class ThemeUiTokens
{
    [JsonPropertyName("CornerRadius")]
    public double CornerRadius { get; set; } = 16;

    [JsonPropertyName("BlurIntensity")]
    public double BlurIntensity { get; set; } = 20;

    [JsonPropertyName("BorderThickness")]
    public double BorderThickness { get; set; } = 1.0;

    [JsonPropertyName("FontRegular")]
    public string? FontRegular { get; set; }

    [JsonPropertyName("FontMedium")]
    public string? FontMedium { get; set; }

    [JsonPropertyName("FontBold")]
    public string? FontBold { get; set; }

    [JsonPropertyName("FontTitle")]
    public string? FontTitle { get; set; }

    [JsonPropertyName("FontFile")]
    public string? FontFile { get; set; }

    [JsonPropertyName("CardOpacity")]
    public double? CardOpacity { get; set; }
}

public sealed class ThemeSounds
{
    [JsonPropertyName("click")]
    public string? Click { get; set; }

    [JsonPropertyName("notification")]
    public string? Notification { get; set; }

    [JsonPropertyName("connect")]
    public string? Connect { get; set; }

    [JsonPropertyName("disconnect")]
    public string? Disconnect { get; set; }
}

public sealed class ThemeBackground
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "image";

    [JsonPropertyName("videoSource")]
    public string? VideoSource { get; set; }

    [JsonPropertyName("imageSource")]
    public string? ImageSource { get; set; }

    [JsonPropertyName("fallbackImage")]
    public string? FallbackImage { get; set; }

    [JsonPropertyName("loop")]
    public bool Loop { get; set; } = true;

    [JsonPropertyName("muted")]
    public bool Muted { get; set; } = true;

    [JsonPropertyName("opacity")]
    public double Opacity { get; set; } = 0.35;
}

public sealed class ThemeDecorations
{
    [JsonPropertyName("screenFrame")]
    public string? ScreenFrame { get; set; }

    [JsonPropertyName("screenFrameMobile")]
    public string? ScreenFrameMobile { get; set; }

    [JsonPropertyName("cardFrame")]
    public string? CardFrame { get; set; }

    [JsonPropertyName("buttonRing")]
    public string? ButtonRing { get; set; }

    [JsonPropertyName("buttonImageIdle")]
    public string? ButtonImageIdle { get; set; }

    [JsonPropertyName("buttonImageConnecting")]
    public string? ButtonImageConnecting { get; set; }

    [JsonPropertyName("buttonImageActive")]
    public string? ButtonImageActive { get; set; }

    [JsonPropertyName("buttonImageError")]
    public string? ButtonImageError { get; set; }

    [JsonPropertyName("buttonVideoIdle")]
    public string? ButtonVideoIdle { get; set; }

    [JsonPropertyName("buttonVideoConnecting")]
    public string? ButtonVideoConnecting { get; set; }

    [JsonPropertyName("buttonVideoActive")]
    public string? ButtonVideoActive { get; set; }

    [JsonPropertyName("buttonVideoError")]
    public string? ButtonVideoError { get; set; }

    [JsonPropertyName("frameSlice")]
    public FrameSlice? FrameSlice { get; set; }

    [JsonPropertyName("frameSliceMobile")]
    public FrameSlice? FrameSliceMobile { get; set; }

    [JsonPropertyName("cornerStickers")]
    public Dictionary<string, string>? CornerStickers { get; set; }
}

public sealed class FrameSlice
{
    [JsonPropertyName("top")]
    public int Top { get; set; }

    [JsonPropertyName("right")]
    public int Right { get; set; }

    [JsonPropertyName("bottom")]
    public int Bottom { get; set; }

    [JsonPropertyName("left")]
    public int Left { get; set; }

    public bool IsValid => Top >= 0 && Right >= 0 && Bottom >= 0 && Left >= 0 && (Top > 0 || Right > 0 || Bottom > 0 || Left > 0);
}

public sealed class ThemeLayout
{
    [JsonPropertyName("buttonPosition")]
    public string ButtonPosition { get; set; } = "center";

    [JsonPropertyName("speedWidget")]
    public string SpeedWidget { get; set; } = "graph";

    [JsonPropertyName("serverCardStyle")]
    public string ServerCardStyle { get; set; } = "glass";

    [JsonPropertyName("showQuickToggles")]
    public bool ShowQuickToggles { get; set; } = true;
}

public sealed class ThemeVfx
{
    [JsonPropertyName("particles")]
    public string Particles { get; set; } = "none";

    [JsonPropertyName("particleSprite")]
    public string? ParticleSprite { get; set; }

    [JsonPropertyName("intensity")]
    public double Intensity { get; set; } = 0.5;

    [JsonPropertyName("speed")]
    public double Speed { get; set; } = 1.0;

    [JsonPropertyName("size")]
    public double Size { get; set; } = 1.0;

    [JsonPropertyName("color")]
    public string? Color { get; set; }
}

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
}
