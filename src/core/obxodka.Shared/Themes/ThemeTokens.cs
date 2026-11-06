namespace obxodka.Shared.Themes;

//базовые токены темы оформления цвета градиенты шрифты и звуки
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

//палитра цветов темы оформления для фонов поверхностей текста и акцентов
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

//градиенты темы оформления
public sealed class ThemeGradients
{
    [JsonPropertyName("PrimaryGradient")]
    public GradientDefinition? PrimaryGradient { get; set; }

    [JsonPropertyName("SurfaceGradient")]
    public GradientDefinition? SurfaceGradient { get; set; }
}

//определение цветового градиента и контрольных точек
public sealed class GradientDefinition
{
    [JsonPropertyName("angle")]
    public double Angle { get; set; } = 45;

    [JsonPropertyName("stops")]
    public List<string> Stops { get; set; } = [];
}

//токены интерфейса радиусы скруглений толщина границ и шрифты
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

//звуковые эффекты темы оформления
public sealed class ThemeSounds
{
    [JsonPropertyName("click")]
    public string? Click { get; set; }

    [JsonPropertyName("hover")]
    public string? Hover { get; set; }

    [JsonPropertyName("notification")]
    public string? Notification { get; set; }

    [JsonPropertyName("connect")]
    public string? Connect { get; set; }

    [JsonPropertyName("disconnect")]
    public string? Disconnect { get; set; }

    [JsonPropertyName("ambient")]
    public string? Ambient { get; set; }

    [JsonPropertyName("volume")]
    public double Volume { get; set; } = 0.85;
}
