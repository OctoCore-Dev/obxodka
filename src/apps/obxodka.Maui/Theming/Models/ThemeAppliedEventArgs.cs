namespace obxodka.Maui.Theming.Models;

//аргументы события применения темы оформления к интерфейсу приложения
public sealed class ThemeAppliedEventArgs : EventArgs
{
    public ThemeManifest Manifest { get; init; } = new();
    public string ThemeFolderPath { get; init; } = string.Empty;
    public string? BackgroundImagePath { get; init; }
    public string? BackgroundVideoPath { get; init; }
    public string? ScreenFramePath { get; init; }
    public FrameSlice? FrameSlice { get; init; }
    public string? CardFramePath { get; init; }
    public string? ButtonRingPath { get; init; }
    public string? FontFilePath { get; init; }
    public string? ConnectSoundPath { get; init; }
    public string? DisconnectSoundPath { get; init; }
    public string? ClickSoundPath { get; init; }
    public string? HoverSoundPath { get; init; }
    public string? NotificationSoundPath { get; init; }
    public string? AmbientSoundPath { get; init; }
    public bool HasParticles { get; init; }
    public string? ParticleSpritePath { get; init; }
    public List<string> ParticleSpritePaths { get; init; } = [];
    public string? CornerTopLeftPath { get; init; }
    public string? CornerTopRightPath { get; init; }
    public string? CornerBottomLeftPath { get; init; }
    public string? CornerBottomRightPath { get; init; }
    public string? ButtonImageIdlePath { get; init; }
    public string? ButtonImageConnectingPath { get; init; }
    public string? ButtonImageActivePath { get; init; }
    public string? ButtonImageErrorPath { get; init; }
    public string? ButtonVideoIdlePath { get; init; }
    public string? ButtonVideoConnectingPath { get; init; }
    public string? ButtonVideoActivePath { get; init; }
    public string? ButtonVideoErrorPath { get; init; }
    public string? ScreenOverlayPath { get; init; }
}
