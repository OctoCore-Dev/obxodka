namespace obxodka.Shared.Themes;

//визуальные эффекты частицы свечение оверлеи и реакция на трафик
public sealed class ThemeVfx
{
    [JsonPropertyName("particles")]
    public string Particles { get; set; } = "none";

    [JsonPropertyName("particleSprite")]
    public string? ParticleSprite { get; set; }

    [JsonPropertyName("particleSprites")]
    public List<string>? ParticleSprites { get; set; }

    [JsonPropertyName("intensity")]
    public double Intensity { get; set; } = 0.5;

    [JsonPropertyName("speed")]
    public double Speed { get; set; } = 1.0;

    [JsonPropertyName("size")]
    public double Size { get; set; } = 1.0;

    [JsonPropertyName("color")]
    public string? Color { get; set; }

    [JsonPropertyName("interactiveParticles")]
    public bool InteractiveParticles { get; set; } = true;

    [JsonPropertyName("trafficReactive")]
    public bool TrafficReactive { get; set; } = true;

    [JsonPropertyName("parallaxIntensity")]
    public double ParallaxIntensity { get; set; } = 0.35;

    [JsonPropertyName("screenOverlay")]
    public string ScreenOverlay { get; set; } = "none";

    [JsonPropertyName("trafficGraphStyle")]
    public string TrafficGraphStyle { get; set; } = "neon_wave";

    [JsonPropertyName("glow")]
    public ThemeGlow? Glow { get; set; }

    [JsonPropertyName("buttonLoaderStyle")]
    public string ButtonLoaderStyle { get; set; } = "crystal";

    [JsonPropertyName("statusText")]
    public ThemeStatusText? StatusText { get; set; }

    [JsonPropertyName("activeOverrides")]
    public ThemeActiveOverrides? ActiveOverrides { get; set; }

    [JsonPropertyName("physics")]
    public ThemeParticlePhysics? Physics { get; set; }

    [JsonPropertyName("direction")]
    public string? Direction { get; set; }

    [JsonPropertyName("minSize")]
    public double? MinSize { get; set; }

    [JsonPropertyName("maxSize")]
    public double? MaxSize { get; set; }

    [JsonPropertyName("rotationSpeed")]
    public double? RotationSpeed { get; set; }

    [JsonPropertyName("swaySpeed")]
    public double? SwaySpeed { get; set; }

    [JsonPropertyName("twinkleSpeed")]
    public double? TwinkleSpeed { get; set; }

    [JsonPropertyName("connectLines")]
    public bool? ConnectLines { get; set; }

    [JsonPropertyName("overlayImage")]
    public string? OverlayImage { get; set; }

    [JsonPropertyName("overlayOpacity")]
    public double? OverlayOpacity { get; set; }
}

//параметры физики частиц темы оформления
public sealed class ThemeParticlePhysics
{
    [JsonPropertyName("direction")]
    public string? Direction { get; set; }

    [JsonPropertyName("minSize")]
    public double? MinSize { get; set; }

    [JsonPropertyName("maxSize")]
    public double? MaxSize { get; set; }

    [JsonPropertyName("speedX")]
    public double? SpeedX { get; set; }

    [JsonPropertyName("speedY")]
    public double? SpeedY { get; set; }

    [JsonPropertyName("rotationSpeed")]
    public double? RotationSpeed { get; set; }

    [JsonPropertyName("swaySpeed")]
    public double? SwaySpeed { get; set; }

    [JsonPropertyName("twinkleSpeed")]
    public double? TwinkleSpeed { get; set; }

    [JsonPropertyName("connectLines")]
    public bool? ConnectLines { get; set; }

    [JsonPropertyName("overlayImage")]
    public string? OverlayImage { get; set; }

    [JsonPropertyName("overlayOpacity")]
    public double? OverlayOpacity { get; set; }
}

//пользовательские статусные надписи состояния подключения темы
public sealed class ThemeStatusText
{
    [JsonPropertyName("disconnected")]
    public string? Disconnected { get; set; }

    [JsonPropertyName("connecting")]
    public string? Connecting { get; set; }

    [JsonPropertyName("connected")]
    public string? Connected { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("buttonIdle")]
    public string? ButtonIdle { get; set; }

    [JsonPropertyName("buttonConnecting")]
    public string? ButtonConnecting { get; set; }

    [JsonPropertyName("buttonConnected")]
    public string? ButtonConnected { get; set; }
}

//переопределение стилей при активном подключении к vpn
public sealed class ThemeActiveOverrides
{
    [JsonPropertyName("accentColor")]
    public string? AccentColor { get; set; }

    [JsonPropertyName("glowColor")]
    public string? GlowColor { get; set; }

    [JsonPropertyName("particleSpeedMultiplier")]
    public double ParticleSpeedMultiplier { get; set; } = 1.5;

    [JsonPropertyName("screenOverlay")]
    public string? ScreenOverlay { get; set; }
}

//параметры неонового свечения элементов
public sealed class ThemeGlow
{
    [JsonPropertyName("intensity")]
    public double Intensity { get; set; } = 1.0;

    [JsonPropertyName("radius")]
    public double Radius { get; set; } = 40.0;

    [JsonPropertyName("coreColor")]
    public string? CoreColor { get; set; }

    [JsonPropertyName("bloomColor")]
    public string? BloomColor { get; set; }
}
