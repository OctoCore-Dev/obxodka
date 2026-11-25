using Avalonia.Markup.Xaml;
using obxodka.Avalonia.Services;
using obxodka.Avalonia.ViewModels;
using obxodka.Avalonia.Views;

namespace obxodka.Avalonia;

//точка входа приложения avalonia с инициализацией общих служб ядра
public sealed partial class App : Application, IDisposable
{
    private AvaloniaConnectivityService? _connectivity;
    private MainViewModel? _mainViewModel;
    private bool _disposed;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _connectivity = new AvaloniaConnectivityService();
            PlatformServices.Init(
                new AvaloniaPreferencesService(),
                new AvaloniaSecureStorageService(),
                _connectivity,
                new AvaloniaMainThreadService(),
                new AvaloniaDeviceInfoService());

            _mainViewModel = new MainViewModel();
            var window = new MainWindow
            {
                DataContext = _mainViewModel
            };
            desktop.MainWindow = window;

            desktop.ShutdownRequested += (_, _) => Dispose();
            _ = _mainViewModel.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _mainViewModel?.ShutdownBlocking();
        _mainViewModel?.Dispose();
        _connectivity?.Dispose();
    }
}
