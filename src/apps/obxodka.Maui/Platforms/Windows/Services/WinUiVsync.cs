using Microsoft.UI.Xaml.Media;

namespace obxodka.Platforms.Windows;

//подписка на аппаратный такт vsync экрана windows
internal static class WinUiVsync
{
    public static void Subscribe(EventHandler<object> handler) =>
        CompositionTarget.Rendering += handler;

    public static void Unsubscribe(EventHandler<object> handler) =>
        CompositionTarget.Rendering -= handler;
}
