using Android.App;
using Android.Content;
using Android.Service.QuickSettings;
using Java.Lang;
using Exception = System.Exception;

namespace obxodka;

//системная плитка быстрых настроек android в верхней шторке с фоновым запуском и ssot-синхронизацией
[Service(
    Name = "obxodka.OctopusTileService",
    Label = "Obxodka",
    Icon = "@drawable/notification_icon",
    Permission = "android.permission.BIND_QUICK_SETTINGS_TILE",
    Exported = true)]
[IntentFilter(["android.service.quicksettings.action.QS_TILE"])]
[SupportedOSPlatform("android30.0")]
public sealed class OctopusTileService : TileService
{
    private const int TileLaunchRequestCode = 4101;

    //принудительное обновление состояния плитки в шторке из любой точки приложения
    public static void RequestTileUpdate(Context context)
    {
        try
        {
            var appContext = context.ApplicationContext ?? context;
            using var component = new ComponentName(appContext, Class.FromType(typeof(OctopusTileService)));
            RequestListeningState(appContext, component);
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning($"[QS-TILE] Ошибка запроса обновления плитки: {ex.Message}");
        }
    }

    public override void OnTileAdded()
    {
        base.OnTileAdded();
        AndroidBackgroundVpnController.EnsureStateHooked(ApplicationContext ?? this);
        SyncTileVisual(AndroidVpnService.Orchestrator.CurrentState);
    }

    public override void OnStartListening()
    {
        base.OnStartListening();
        AndroidBackgroundVpnController.EnsureStateHooked(ApplicationContext ?? this);
        SyncTileVisual(AndroidVpnService.Orchestrator.CurrentState);
    }

    public override void OnClick()
    {
        base.OnClick();
        var appContext = ApplicationContext ?? this;
        AndroidBackgroundVpnController.EnsureStateHooked(appContext);

        var currentState = AndroidVpnService.Orchestrator.CurrentState;
        var isStopping = currentState is not (AppVpnState.Disconnected or AppVpnState.Error);

        //при холодном старте немедленно поднимаем foreground-службу внутри окна клика плитки
        if (!isStopping)
        {
            AndroidBackgroundVpnController.TryPrewarmForegroundServiceOnClick(appContext);
        }

        //мгновенный визуальный отклик плитки до завершения сетевого хендшейка
        SyncTileVisual(isStopping ? AppVpnState.Disconnecting : AppVpnState.Connecting);

        _ = Task.Run(async () =>
            await AndroidBackgroundVpnController.ToggleFromTileAsync(appContext, CollapseAndLaunchMainActivity).ConfigureAwait(false));
    }

    //сворачивание шторки и вывод окна приложения при нештатных сценариях
    private void CollapseAndLaunchMainActivity(string actionValue) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var appContext = ApplicationContext ?? this;
            using var intent = AndroidBackgroundVpnController.BuildMainActivityIntent(appContext, actionValue);
            try
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(34))
                {
                    using var pendingIntent = PendingIntent.GetActivity(
                        this,
                        TileLaunchRequestCode,
                        intent,
                        PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
                    if (pendingIntent is not null)
                    {
                        StartActivityAndCollapse(pendingIntent);
                        return;
                    }
                }

#pragma warning disable CA1422
                StartActivityAndCollapse(intent);
#pragma warning restore CA1422
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"[QS-TILE] Fallback запуск MainActivity: {ex.Message}");
                try
                {
                    appContext.StartActivity(intent);
                }
                catch
                {
                    //игнорируем ошибку запуска при уничтожении сервиса
                }
            }
        });

    //синхронизация иконки, состояния и подзаголовка плитки с живым состоянием оркестратора
    private void SyncTileVisual(AppVpnState state)
    {
        try
        {
            var tile = QsTile;
            if (tile is null)
            {
                return;
            }

            tile.Label = "Obxodka";
            switch (state)
            {
                case AppVpnState.Connected:
                    tile.State = TileState.Active;
                    tile.Subtitle = !string.IsNullOrWhiteSpace(OctopusEngine.Current.ActiveServerLocation)
                        ? OctopusEngine.Current.ActiveServerLocation
                        : "Подключено";
                    break;

                case AppVpnState.Connecting:
                case AppVpnState.Reconnecting:
                    tile.State = TileState.Active;
                    tile.Subtitle = "Подключение...";
                    break;

                case AppVpnState.Disconnecting:
                    tile.State = TileState.Inactive;
                    tile.Subtitle = "Отключение...";
                    break;

                default:
                    tile.State = TileState.Inactive;
                    tile.Subtitle = "Отключено";
                    break;
            }

            tile.UpdateTile();
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning($"[QS-TILE] Ошибка обновления QsTile: {ex.Message}");
        }
    }
}
