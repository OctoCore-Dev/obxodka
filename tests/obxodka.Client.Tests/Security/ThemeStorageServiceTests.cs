namespace obxodka.Client.Tests.Security;

[Trait("Category", "Unit")]
public sealed class ThemeStorageServiceTests
{
    [Fact]
    public void ThemeCatalogClientToMirrorUrlTransformsGithubToJsdelivr()
    {
        //проверка преобразования ссылок github raw в cdn зеркало jsdelivr
        const string rawUrl = "https://raw.githubusercontent.com/OctoCore-Dev/themes/main/assets/preview.png";
        var mirrorUrl = ThemeCatalogClient.ToMirrorUrl(rawUrl);
        Assert.Equal("https://cdn.jsdelivr.net/gh/OctoCore-Dev/themes@main/assets/preview.png", mirrorUrl);

        //проверка неизменности сторонних ссылок
        const string externalUrl = "https://example.com/image.png";
        Assert.Equal(externalUrl, ThemeCatalogClient.ToMirrorUrl(externalUrl));

        //проверка пустых и null ссылок
        Assert.Equal(string.Empty, ThemeCatalogClient.ToMirrorUrl(null));
        Assert.Equal(string.Empty, ThemeCatalogClient.ToMirrorUrl(string.Empty));
    }

    [Theory]
    [InlineData("video.mp4", true)]
    [InlineData("anim.webm", true)]
    [InlineData("clip.mov", true)]
    [InlineData("image.png", false)]
    [InlineData("sound.wav", false)]
    [InlineData("script.sh", false)]
    public void ThemeStorageServiceIsVideoFileValidatesCorrectly(string path, bool expected)
    {
        //проверка валидации видеоформатов
        Assert.Equal(expected, ThemeStorageService.IsVideoFile(path));
    }

    [Fact]
    public void ThemeStorageServiceEnumerateAssetPathsExtractsAllManifestAssets()
    {
        //проверка сбора всех путей ресурсов темы из манифеста
        var manifest = new ThemeManifest
        {
            Id = "asset-enum-test",
            Name = "Asset Enum Test",
            Background = new ThemeBackground
            {
                ImageSource = "assets/bg.png",
                VideoSource = "assets/bg.mp4"
            },
            Decorations = new ThemeDecorations
            {
                ScreenFrame = "assets/frame.png",
                ButtonImageIdle = "assets/btn_idle.png"
            },
            Core = new CoreThemeTokens
            {
                Sounds = new ThemeSounds
                {
                    Connect = "assets/connect.wav",
                    Click = "assets/click.wav"
                }
            },
            Vfx = new ThemeVfx
            {
                ParticleSprite = "assets/particle.png",
                ParticleSprites = ["assets/p1.png", "assets/p2.png"]
            }
        };

        var paths = ThemeStorageService.EnumerateAssetPaths(manifest).ToList();

        Assert.Contains("icon.png", paths);
        Assert.Contains(Path.Combine("assets", "bg.png"), paths);
        Assert.Contains(Path.Combine("assets", "bg.mp4"), paths);
        Assert.Contains(Path.Combine("assets", "frame.png"), paths);
        Assert.Contains(Path.Combine("assets", "btn_idle.png"), paths);
        Assert.Contains(Path.Combine("assets", "connect.wav"), paths);
        Assert.Contains(Path.Combine("assets", "click.wav"), paths);
        Assert.Contains(Path.Combine("assets", "particle.png"), paths);
        Assert.Contains(Path.Combine("assets", "p1.png"), paths);
        Assert.Contains(Path.Combine("assets", "p2.png"), paths);
    }

    [Fact]
    public void ThemeValidatorManifestWithParticlePhysicsIsValid()
    {
        //проверка валидации динамической темы с параметрами физики частиц
        var manifest = new ThemeManifest
        {
            Id = "cyber-particles",
            Name = "Cyber Particles",
            Vfx = new ThemeVfx
            {
                Particles = "cyber",
                Physics = new ThemeParticlePhysics
                {
                    Direction = "rising",
                    MinSize = 8,
                    MaxSize = 24,
                    SpeedX = 0.5,
                    SpeedY = 2.0,
                    RotationSpeed = 1.5,
                    SwaySpeed = 0.05,
                    TwinkleSpeed = 0.08,
                    ConnectLines = true
                }
            }
        };
        Assert.True(ThemeValidator.ValidateManifest(manifest, out var err), err);
    }

    [Fact]
    public void ThemeValidatorSynthesizeGradientGeneratesValidHexColors()
    {
        //проверка синтеза градиента для валидных 6-значных и 8-значных hex
        var (start6, end6) = ThemeValidator.SynthesizeGradient("#0078D4");
        Assert.True(ThemeValidator.IsValidHexColor(start6));
        Assert.True(ThemeValidator.IsValidHexColor(end6));

        var (start8, end8) = ThemeValidator.SynthesizeGradient("#FF0078D4");
        Assert.True(ThemeValidator.IsValidHexColor(start8));
        Assert.True(ThemeValidator.IsValidHexColor(end8));

        //проверка отката для невалидного цвета
        var (fallbackStart, fallbackEnd) = ThemeValidator.SynthesizeGradient("invalid-hex");
        Assert.Equal("#0078D4", fallbackStart);
        Assert.Equal("#004578", fallbackEnd);
    }

    [Theory]
    [InlineData("assets/images/background.png", true)]
    [InlineData("assets/sounds/connect.wav", true)]
    [InlineData("../secret/file.png", false)]
    [InlineData("/absolute/path.png", false)]
    [InlineData("C:\\windows\\system32.dll", false)]
    [InlineData("assets/NUL/test.png", false)]
    [InlineData("assets/CON.png", false)]
    public void ThemeValidatorIsSafeRelativePathRejectsUnsafePaths(string path, bool expectedSafe)
    {
        //проверка фильтрации небезопасных относительных путей
        Assert.Equal(expectedSafe, ThemeValidator.IsSafeRelativePath(path));
    }
}
