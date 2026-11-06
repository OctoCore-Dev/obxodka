namespace obxodka.Shared.Themes;

//настройки фонового изображения или видео
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

//графические декорации рамки кнопки кольца и стикеры
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

//параметры нарезки девятисегментной 9-slice графической рамки
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

//настройки расположения элементов интерфейса темы
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
