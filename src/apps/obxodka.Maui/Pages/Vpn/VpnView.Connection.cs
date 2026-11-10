//using только для сплит тунеля хватит удалять и натыкатся потостоянно на одно и тоже 5ый раз, рома ты тупой, ии не поможет ты сам это сделал
using Microsoft.Maui.Controls.Shapes;
using Path = System.IO.Path;

namespace obxodka.Pages;

//стейт-машин подключения vpn, обработка ошибок, ip адреса и индикаторов
public sealed partial class VpnView
{
    //обработка событий системного лога vpn
    private void HandleVpnLog(string logMsg) =>
        MainThread.BeginInvokeOnMainThread(() => StatusLabel.Text = logMsg);

    //переподключение по нажатию в системном уведомлении
    private void HandleNotificationReconnect()
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (ParentPage is not null)
            {
                await ParentPage.SwitchTabAsync("vpn");
            }

            if (_vpnService is null || _vpnService.CurrentState is AppVpnState.Disconnected or AppVpnState.Error)
            {
                OnConnectClickedAsync(null, EventArgs.Empty);
            }
        });
    }

    //обновление отображения активного хоста подключения
    public void UpdateActiveNode(string? host = null)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_vpnService == null || _vpnService.CurrentState == AppVpnState.Disconnected)
            {
                NodeHostLabel?.Text = "Нет подключения";
                return;
            }

            _activeConnectedNode = !string.IsNullOrWhiteSpace(host) &&
                !IPAddress.TryParse(host, out _) &&
                !host.Contains(AppConfig.PrimaryDomain, StringComparison.OrdinalIgnoreCase) &&
                !host.Contains(AppConfig.MirrorDomain, StringComparison.OrdinalIgnoreCase)
                ? host
                : OctopusEngine.Current?.ActiveSni ?? AppSecrets.AllowedSniPool[0];

            var displayHost = _activeConnectedNode;
            if (string.IsNullOrWhiteSpace(displayHost) ||
                IPAddress.TryParse(displayHost, out _) ||
                displayHost.Contains(AppConfig.PrimaryDomain, StringComparison.OrdinalIgnoreCase) ||
                displayHost.Contains(AppConfig.MirrorDomain, StringComparison.OrdinalIgnoreCase))
            {
                displayHost = AppSecrets.AllowedSniPool[0];
            }

            NodeHostLabel?.Text = displayHost;
        });
    }

    //обновление статуса и цвета ip адреса
    private void UpdateIpStatusDisplay(string text, Color textColor, Color iconColor)
    {
        if (OctopusEngine.Current is { IsConnected: true })
        {
            var v4 = OctopusEngine.Current.AssignedIp;
            var v6 = OctopusEngine.Current.AssignedIpV6;
            var ip = _showIpV6 && !string.IsNullOrWhiteSpace(v6) ? v6 : v4;
            var tag = _showIpV6 && !string.IsNullOrWhiteSpace(v6) ? "v6" : "v4";
            IpAddressLabel.Text = !string.IsNullOrWhiteSpace(ip) ? ip : text;
            IpAddressLabel.TextColor = textColor;
            IpStatusIcon.IconColor = iconColor;
            IpVersionBadge.IsVisible = true;
            IpVersionLabel.Text = tag;
            IpCopyButton.IsVisible = true;
        }
        else
        {
            _showIpV6 = false;
            IpAddressLabel.Text = text;
            IpAddressLabel.TextColor = textColor;
            IpStatusIcon.IconColor = iconColor;
            IpVersionBadge.IsVisible = false;
            IpVersionLabel.Text = "v4";
            IpCopyButton.IsVisible = false;
        }
    }

    //переключение между отображением ipv4 и ipv6
    private void OnToggleIpVersionClicked(object? sender, EventArgs e)
    {
        if (OctopusEngine.Current is not { IsConnected: true })
        {
            return;
        }

        ThemeAudioService.PlayClick();
        _showIpV6 = !_showIpV6;
        var v4 = OctopusEngine.Current.AssignedIp;
        var v6 = OctopusEngine.Current.AssignedIpV6;
        var currentIp = _showIpV6 && !string.IsNullOrWhiteSpace(v6) ? v6 : v4;
        var tag = _showIpV6 && !string.IsNullOrWhiteSpace(v6) ? "v6" : "v4";

        IpAddressLabel.Text = currentIp ?? "Подключен";
        IpAddressLabel.TextColor = GetAppColor("TextPrimary", Colors.White);
        IpVersionBadge.IsVisible = true;
        IpVersionLabel.Text = tag;
        IpCopyButton.IsVisible = true;
    }

    //копирование ip адреса в буфер обмена
    private async void OnCopyIpClickedAsync(object? sender, EventArgs e)
    {
        if (OctopusEngine.Current is not { IsConnected: true })
        {
            return;
        }

        var v4 = OctopusEngine.Current.AssignedIp;
        var v6 = OctopusEngine.Current.AssignedIpV6;
        var currentIp = _showIpV6 && !string.IsNullOrWhiteSpace(v6) ? v6 : v4;
        var tag = _showIpV6 && !string.IsNullOrWhiteSpace(v6) ? "v6" : "v4";

        if (!string.IsNullOrWhiteSpace(currentIp) && currentIp != "0.0.0.0")
        {
            ThemeAudioService.PlayClick();
            await Clipboard.Default.SetTextAsync(currentIp);
            var prevText = IpAddressLabel.Text;
            var prevColor = IpAddressLabel.TextColor;
            var successColor = GetAppColor("Success", s_greenText);
            IpAddressLabel.Text = $"{tag.ToUpperInvariant()} скопирован!";
            IpAddressLabel.TextColor = successColor;
            IpCopyIcon.Icon = FluentIcons.Checkmark20;
            IpCopyIcon.IconColor = successColor;
            await Task.Delay(1100);
            if (OctopusEngine.Current.IsConnected)
            {
                var refreshedV4 = OctopusEngine.Current.AssignedIp;
                var refreshedV6 = OctopusEngine.Current.AssignedIpV6;
                var refreshedIp = _showIpV6 && !string.IsNullOrWhiteSpace(refreshedV6) ? refreshedV6 : refreshedV4;
                IpAddressLabel.Text = refreshedIp ?? prevText;
                IpAddressLabel.TextColor = prevColor;
                IpCopyIcon.Icon = FluentIcons.Copy20;
                IpCopyIcon.IconColor = GetAppColor("TextSecondary", s_grayText);
            }
        }
    }

    //обработка сетевых ошибок подключения vpn
    private void HandleVpnError(string err)
    {
        if (string.IsNullOrWhiteSpace(err) ||
            err.Contains("canceled", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("cancelled", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("OperationCanceledException", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("StatusCode=\"Cancelled\"", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("Call canceled by the client", StringComparison.OrdinalIgnoreCase))
        {
            AppLogger.Log($"[VPN DISCONNECT] Нормальное завершение соединения: {err}");
            return;
        }

        PlatformServices.Notification.ShowUnexpectedDisconnectNotification();

        var friendlyMessage = FormatUserFriendlyError(err);
        _lastErrorMessage = friendlyMessage;

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            StatusLabel.Text = friendlyMessage;
            StatusLabel.TextColor = GetAppColor("Error", s_redDot);
            try
            {
                await NeoAlert.ShowAsync("Сбой сети", friendlyMessage, "OK");
            }
            catch { }
        });
    }

    //преобразование технических ошибок в понятный текст
    private static string FormatUserFriendlyError(string rawError)
    {
        if (string.IsNullOrWhiteSpace(rawError))
        {
            return "Не удалось установить соединение с сервером.";
        }

        if (rawError.Contains("Таймаут", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("Timeout", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return "Не удалось подключиться к серверу (таймаут ответа).\n\nВозможные причины:\n• Блокировка TLS/протокола провайдером\n• Нестабильный интернет\n\nПопробуйте повторить попытку.";
        }

        if (rawError.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("Unauthenticated", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("Device revoked", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("Missing certificate", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("401") ||
            rawError.Contains("Old certificate", StringComparison.OrdinalIgnoreCase))
        {
            return "Срок действия ключа истёк или устройство не авторизовано. Пожалуйста, выполните повторный вход в аккаунт.";
        }

        if (rawError.Contains("PINNING MISMATCH", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("RemoteCertificateNameMismatch", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("RemoteCertificateChainErrors", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("The remote certificate is invalid", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("RemoteCertificateValidationCallback", StringComparison.OrdinalIgnoreCase))
        {
            return "Ошибка проверки сертификата безопасности сервера. Пожалуйста, обновите список серверов или обратитесь в поддержку.";
        }

        if (rawError.Contains("50052") || rawError.Contains("50051") || rawError.Contains("Unavailable", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("SocketException", StringComparison.OrdinalIgnoreCase) || rawError.Contains("не получен нужный отклик", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("от другого компьютера за требуемое время", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("GOAWAY", StringComparison.OrdinalIgnoreCase) || rawError.Contains("The SSL connection could not be established", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("reset by peer", StringComparison.OrdinalIgnoreCase) || rawError.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase))
        {
            return "Сервер временно недоступен или связь была прервана сетью.\n\nПожалуйста, проверьте интернет-соединение или повторите попытку.";
        }

        if (rawError.Contains("No such host is known", StringComparison.OrdinalIgnoreCase) || rawError.Contains("NameResolutionFailure", StringComparison.OrdinalIgnoreCase))
        {
            return "Не удалось найти сервер. Проверьте подключение вашего устройства к интернету.";
        }

        var firstLine = rawError.Split(s_errorSeparators, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        return firstLine.Length > 120 ? firstLine[..120] + "..." : firstLine;
    }

    //сброс индикаторов сетевого пинга
    private void ResetPingIndicators()
    {
        var muted = GetAppColor("TextMuted", s_grayText);
        var border = GetAppColor("BorderMedium", s_grayStroke);
        PingValueLabel.Text = "-- ms";
        PingDot.Color = muted;
        PingBadge.Stroke = border;
        PingValueLabel.TextColor = muted;
    }

    //обработка изменения задержки сети от ядра туннеля
    private void HandlePingUpdated(long rtt)
    {
        PlatformThrottler.Post("ui:ping", TimeSpan.FromMilliseconds(250), () =>
        {
            if (OctopusEngine.Current is { IsConnected: true })
            {
                UpdateIpStatusDisplay(
                    OctopusEngine.Current.AssignedIp ?? "Подключен",
                    GetAppColor("TextPrimary", Colors.White),
                    GetAppColor("Accent", s_cyanAccent));
                PingValueLabel.Text = $"{rtt} ms";
                if (rtt < 75)
                {
                    var sc = GetAppColor("Success", s_greenDot);
                    PingDot.Color = sc;
                    PingBadge.Stroke = sc;
                    PingValueLabel.TextColor = sc;
                }
                else if (rtt < 150)
                {
                    var wc = GetAppColor("Warning", s_amberDot);
                    PingDot.Color = wc;
                    PingBadge.Stroke = wc;
                    PingValueLabel.TextColor = wc;
                }
                else
                {
                    var ec = GetAppColor("Error", s_redDot);
                    PingDot.Color = ec;
                    PingBadge.Stroke = ec;
                    PingValueLabel.TextColor = ec;
                }
            }
            else
            {
                ResetPingIndicators();
            }
        });
    }

    //обработка смены состояния vpn туннеля
    private void HandleAppVpnStateChanged(AppVpnState state)
    {
        UpdateRayIndicator();
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            ServerSelectorButton.Opacity = state is AppVpnState.Connected or AppVpnState.Connecting or AppVpnState.Reconnecting or AppVpnState.Disconnecting ? 0.55 : 1.0;
            var muted = GetAppColor("TextMuted", s_grayText);
            var textPrimary = GetAppColor("TextPrimary", Colors.White);
            var accent = GetAppColor("Accent", s_cyanAccent);

            switch (state)
            {
                case AppVpnState.Disconnected:
                    _isBusy = false;
                    _isErrorState = false;
                    _errorTransitionFactor = 0f;
                    StopLoaderAnimation(animateExit: true);
                    StopGraphAnimation();
                    ResetPingIndicators();
                    TriggerShockwave(isConnect: false);
                    OuterAura.IsVisible = true;
                    UpdateIpStatusDisplay(VpnStatusMessages.IpNotAssigned, muted, muted);
                    _activeConnectedNode = null;
                    UpdateActiveNode();
                    ConnectButtonCore.IsEnabled = true;
                    await SetNeonStateAsync(
                        GetStatusTextForState(AppVpnState.Disconnected, VpnStatusMessages.Disconnected),
                        GetButtonTextForState(AppVpnState.Disconnected, VpnStatusMessages.StartAction),
                        AppVpnState.Disconnected);
                    ParentPage?.NotifyVpnDisconnected();
                    break;

                case AppVpnState.Connected:
                    _isBusy = false;
                    _isErrorState = false;
                    _errorTransitionFactor = 0f;
                    StartLoaderAnimation(animateEntrance: false);
                    StartGraphAnimation();
                    AnimateReactorMorph(1.0f, 650);
                    TriggerShockwave(isConnect: true);
                    OuterAura.IsVisible = true;
                    UpdateIpStatusDisplay(OctopusEngine.Current.AssignedIp ?? VpnStatusMessages.Connected, textPrimary, accent);
                    UpdateActiveNode(_activeConnectedNode);
                    ConnectButtonCore.IsEnabled = true;
                    UpdateRayIndicator();
                    await SetNeonStateAsync(
                        GetStatusTextForState(AppVpnState.Connected, VpnStatusMessages.Protected),
                        GetButtonTextForState(AppVpnState.Connected, VpnStatusMessages.StopAction),
                        AppVpnState.Connected);
                    ParentPage?.NotifyVpnConnected();
                    break;

                case AppVpnState.Error:
                    _isBusy = false;
                    _isErrorState = true;
                    StopGraphAnimation();
                    ResetPingIndicators();
                    AnimateErrorMorph(1.0f, 350);
                    TriggerShockwave(isConnect: false);
                    OuterAura.IsVisible = true;
                    UpdateIpStatusDisplay(VpnStatusMessages.IpNotAssigned, muted, muted);
                    _activeConnectedNode = null;
                    UpdateActiveNode();
                    ConnectButtonCore.IsEnabled = true;
                    PlatformServices.Notification.ShowUnexpectedDisconnectNotification();
                    var errDisplay = !string.IsNullOrWhiteSpace(_lastErrorMessage)
                        ? _lastErrorMessage
                        : GetStatusTextForState(AppVpnState.Error, VpnStatusMessages.DefaultConnectionError);
                    await SetNeonStateAsync(
                        errDisplay,
                        GetButtonTextForState(AppVpnState.Error, VpnStatusMessages.Retry),
                        AppVpnState.Error);
                    ParentPage?.NotifyVpnDisconnected();
                    StopLoaderAnimation(animateExit: true);
                    break;

                case AppVpnState.Connecting:
                case AppVpnState.Reconnecting:
                    _isErrorState = false;
                    _errorTransitionFactor = 0f;
                    _reactorTransitionFactor = 0f;
                    ResetPingIndicators();
                    StartLoaderAnimation(animateEntrance: true);
                    ConnectButtonCore.IsEnabled = true;
                    UpdateIpStatusDisplay(VpnStatusMessages.IpAcquiring, accent, accent);
                    UpdateActiveNode();
                    await SetNeonStateAsync(
                        GetStatusTextForState(state, VpnStatusMessages.Connecting),
                        GetButtonTextForState(state, VpnStatusMessages.PleaseWait),
                        state);
                    break;

                case AppVpnState.Disconnecting:
                    StopGraphAnimation();
                    ResetPingIndicators();
                    ConnectButtonCore.IsEnabled = false;
                    UpdateIpStatusDisplay(VpnStatusMessages.Disconnecting, muted, muted);
                    NodeHostLabel.Text = VpnStatusMessages.Disconnecting;
                    StatusLabel.Text = VpnStatusMessages.Disconnecting;
                    ConnectButtonText.Text = GetButtonTextForState(AppVpnState.Disconnecting, VpnStatusMessages.PleaseWait);
                    _ = UpdateCustomButtonStateAsync(AppVpnState.Disconnecting);
                    AnimateReactorMorph(0.0f, 350);
                    SafeScaleTo(LoaderCanvas, 0.35, 400, Easing.CubicIn);
                    SafeFadeTo(LoaderCanvas, 0, 350, Easing.CubicIn);
                    break;

                default:
                    break;
            }
        });
    }

    private async Task SetNeonStateAsync(string status, string btnText, AppVpnState state)
    {
        StatusLabel.Text = status;
        ConnectButtonText.Text = btnText;

        _ = UpdateCustomButtonStateAsync(state);

        if (state == AppVpnState.Connected)
        {
            await UIAnimations.SetVpnConnectedAsync(ConnectIcon, StatusLabel, OuterAura);
            var activeOv = ThemeManager.Instance?.ActiveTheme?.Vfx?.ActiveOverrides;
            var accentColor = GetAppColor("Accent", s_cyanAccent);
            if (!string.IsNullOrWhiteSpace(activeOv?.AccentColor) && Color.TryParse(activeOv.AccentColor, out var ovColor))
            {
                accentColor = ovColor;
                StatusLabel.TextColor = ovColor;
                ConnectIcon.IconColor = ovColor;
            }

            if (!CustomButtonImage.IsVisible && !CustomButtonAnimatedCanvas.IsVisible)
            {
                ConnectButtonCore.BackgroundColor = accentColor.WithAlpha(0.12f);
                ConnectButtonCore.Stroke = accentColor;
            }
            var targetScale = DeviceInfo.Idiom == DeviceIdiom.Phone ? 1.05 : 1.12;
            SafeScaleTo(LoaderCanvas, targetScale, 650, Easing.SpringOut);
        }
        else if (state == AppVpnState.Error)
        {
            await UIAnimations.SetVpnDisconnectedAsync(ConnectIcon, StatusLabel, OuterAura);
            var errColor = GetAppColor("Error", s_redDot);
            var errDim = GetAppColor("ErrorDim", s_redBg);
            ConnectIcon.IconColor = errColor;
            StatusLabel.TextColor = errColor;
            if (!CustomButtonImage.IsVisible && !CustomButtonAnimatedCanvas.IsVisible)
            {
                ConnectButtonCore.BackgroundColor = errDim;
                ConnectButtonCore.Stroke = errColor;
            }
        }
        else if (state is AppVpnState.Connecting or AppVpnState.Reconnecting)
        {
            if (!CustomButtonImage.IsVisible && !CustomButtonAnimatedCanvas.IsVisible)
            {
                ConnectButtonCore.ClearValue(BackgroundColorProperty);
                ConnectButtonCore.ClearValue(Border.StrokeProperty);
                ConnectButtonCore.SetDynamicResource(BackgroundColorProperty, "BgElevated");
                ConnectButtonCore.SetDynamicResource(Border.StrokeProperty, "BorderMedium");
            }
            SafeScaleTo(LoaderCanvas, 1.0, 500, Easing.SpringOut);
        }
        else
        {
            await UIAnimations.SetVpnDisconnectedAsync(ConnectIcon, StatusLabel, OuterAura);
            if (!CustomButtonImage.IsVisible && !CustomButtonAnimatedCanvas.IsVisible)
            {
                ConnectButtonCore.ClearValue(BackgroundColorProperty);
                ConnectButtonCore.ClearValue(Border.StrokeProperty);
                ConnectButtonCore.SetDynamicResource(BackgroundColorProperty, "BgElevated");
                ConnectButtonCore.SetDynamicResource(Border.StrokeProperty, "BorderMedium");
            }
            SafeScaleTo(LoaderCanvas, 1.0, 500, Easing.SpringOut);
        }
    }

    //нажатие кнопки подключения и отключения vpn
    private async void OnConnectClickedAsync(object? sender, EventArgs? e)
    {
        if (_isBusy || _vpnService is null)
        {
            return;
        }

        ThemeAudioService.PlayClick();
        if (ParentPage is not null)
        {
            float bx;
            float by;
            if (e is TappedEventArgs te && te.GetPosition(ParentPage) is { } pos)
            {
                bx = (float)pos.X;
                by = (float)pos.Y;
            }
            else
            {
                bx = (float)(ParentPage.Width * 0.5);
                by = (float)(ParentPage.Height * 0.5);
            }
            ParentPage.TriggerParticleBurst(bx, by);
        }
        _isBusy = true;

        if (_vpnService.CurrentState is not (AppVpnState.Disconnected or AppVpnState.Error))
        {
            _ = ConnectButtonCore.BounceClickAsync();
            _ = Task.Run(async () =>
            {
                try
                {
                    _ = _apiService.StopVpnOnServerAsync();
                    await _vpnService.StopVpnAsync();
                }
                catch (Exception ex)
                {
                    AppLogger.LogError($"[VPN STOP ERROR] {ex.Message}", ex);
                }
                finally
                {
                    _isBusy = false;
                }
            });
            return;
        }

        _lastErrorMessage = null;
        await ConnectButtonCore.BounceClickAsync();
        try
        {
#if ANDROID
            var isDisclosureAccepted = Preferences.Get("VpnDisclosureAccepted", false);
            if (!isDisclosureAccepted)
            {
                var accepted = ParentPage is not null && await ParentPage.DisplayAlertAsync(
                    "Защита и использование VPN",
                    "Приложение Obxodka использует службу Android VpnService для создания безопасного зашифрованного туннеля и защиты вашего интернет-трафика.\n\n" +
                    "• Мы не сохраняем историю посещений, сетевые логи и личные данные.\n" +
                    "• Все данные передаются в зашифрованном виде.\n\n" +
                    "Вы согласны использовать VPN-соединение для защиты сети?",
                    "Принять",
                    "Отмена");

                if (!accepted)
                {
                    return;
                }
                Preferences.Set("VpnDisclosureAccepted", true);
            }
#endif

            if (!OctopusEngine.IsUplinkAvailable)
            {
                await NeoAlert.ShowAsync(
                    "Нет подключения к сети",
                    "На устройстве отсутствует доступ в интернет (Wi-Fi или сетевой кабель отключены).\n\nПожалуйста, проверьте подключение к сети и повторите попытку.",
                    "Понятно");
                return;
            }

            //быстрая проверка реального выхода в интернет (баланс тарифа, авторизация в wi-fi)
            var hasInternet = await OctopusEngine.VerifyPhysicalUplinkAsync(1200).ConfigureAwait(true);
            if (!hasInternet)
            {
                await NeoAlert.ShowAsync(
                    "Нет доступа в интернет",
                    "Устройство подключено к сети, но выход в интернет отсутствует.\n\nВозможно, закончился баланс тарифа, требуется авторизация в сети (Wi-Fi) или на линии провайдера сбой.\n\nПроверьте соединение и повторите попытку.",
                    "Понятно");
                return;
            }

            var auditResult = await PlatformServices.CertificateAudit.CheckCertificatesAsync();
            if (auditResult.HasUntrustedRoot)
            {
                var platformInstructions = DeviceInfo.Platform == DevicePlatform.Android
                    ? "Как удалить:\n1. В открывшихся настройках выберите «Надежные сертификаты» (или «Хранилище учетных данных»).\n2. Перейдите во вкладку «Пользователь».\n3. Нажмите на сертификат и выберите «Удалить»."
                    : "Как удалить:\n1. В открывшемся окне «certmgr» раскройте «Доверенные корневые центры сертификации» -> «Сертификаты».\n2. Найдите сертификат, нажмите правой кнопкой мыши -> «Удалить».";

                var openSettings = ParentPage is not null && await ParentPage.DisplayAlertAsync(
                    "Обнаружен сертификат перехвата!",
                    $"На вашем устройстве установлен сторонний корневой сертификат:\n«{auditResult.CertificateName}»\n\n" +
                    "ВНИМАНИЕ: Вне VPN этот сертификат позволяет операторам связи и третьим лицам расшифровывать ваш защищённый трафик (HTTPS), видеть переписки и перехватывать пароли.\n\n" +
                    $"{platformInstructions}\n\n" +
                    "Желаете открыть настройки для удаления?",
                    "Открыть настройки",
                    "Отмена");

                if (openSettings)
                {
                    await PlatformServices.CertificateAudit.OpenCertificateSettingsAsync();
                }

                return;
            }

            if ((ParentPage?.RemainingSeconds ?? 0) <= 0)
            {
                if (ParentPage is not null)
                {
                    await ParentPage.DisplayAlertAsync("Внимание", "Нет доступного времени.", "OK");
                }
                return;
            }

            var conflictingVpn = _vpnService.DetectConflictingVpn();
            if (!string.IsNullOrEmpty(conflictingVpn))
            {
                if (ParentPage is not null)
                {
                    var proceed = await ParentPage.DisplayAlertAsync(
                        "Сторонний VPN",
                        $"Обнаружен сторонний VPN ({conflictingVpn}).\n\nЕсли вы уже отключили тот VPN, Обходка автоматически нейтрализует оставшийся адаптер и подключится.\n\nПродолжить?",
                        "Продолжить",
                        "Отмена");

                    if (!proceed)
                    {
                        return;
                    }
                }
            }

            StartLoaderAnimation();
            ConnectButtonCore.IsEnabled = true;
            var accent = GetAppColor("Accent", s_cyanAccent);
            UpdateIpStatusDisplay("Получение...", accent, accent);
            await SetNeonStateAsync(
                GetStatusTextForState(AppVpnState.Connecting, "Подключение..."),
                GetButtonTextForState(AppVpnState.Connecting, "ЖДИТЕ"),
                AppVpnState.Connecting);

            var preflightError = await Task.Run(_vpnService.RunNetworkPreflightAsync);
            if (!string.IsNullOrEmpty(preflightError))
            {
                throw new InvalidOperationException(preflightError);
            }

            var (success, servers, errorMsg) = await _apiService.GetServersAsync();
            if (!success || servers is null || servers.Count == 0)
            {
                if (AppConfig.ApiBaseUrl != AppConfig.DefaultApiBaseUrl)
                {
                    AppConfig.ApiBaseUrl = AppConfig.DefaultApiBaseUrl;
                    (success, servers, errorMsg) = await _apiService.GetServersAsync();
                }
            }

            if ((!success || servers is null || servers.Count == 0) && _cachedServers.Count > 0)
            {
                servers = _cachedServers;
            }

            if (servers is null || servers.Count == 0)
            {
                throw new InvalidOperationException("Не удалось получить список серверов. Проверьте интернет и нажмите «СТАРТ» для повтора.");
            }

            _cachedServers = servers;

            VpnServerDto targetServer;
            if (_selectedServer is not null && servers.Any(s => s.Ip == _selectedServer.Ip))
            {
                targetServer = servers.First(s => s.Ip == _selectedServer.Ip);
            }
            else
            {
                targetServer = servers[0];
                _selectedServer = targetServer;
                UpdateSelectedServerDisplay();
            }

            var orderedCandidates = new List<VpnServerDto> { targetServer };
            orderedCandidates.AddRange(servers.Where(s => s.Ip != targetServer.Ip));

            var isShieldNode = string.Equals(targetServer.Role, "shield", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(targetServer.Provider, "Cloudflare", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(targetServer.Ip, AppConfig.PrimaryDomain, StringComparison.OrdinalIgnoreCase) ||
                               targetServer.Ip.EndsWith($".{AppConfig.PrimaryDomain}", StringComparison.OrdinalIgnoreCase);

            if (isShieldNode)
            {
                //для cloudflare shield узлов ssl-пиннинг отключается так как cloudflare использует веб-сертификаты с регулярной ротацией
                OctopusEngine.DynamicSslPublicKeyHash = null;
                UpdateActiveNode(targetServer.Ip);
            }
            else if (!string.IsNullOrWhiteSpace(targetServer.CertHash))
            {
                OctopusEngine.DynamicSslPublicKeyHash = targetServer.CertHash;
                UpdateActiveNode(AppSecrets.AllowedSniPool[0]);
            }
            else
            {
                var (successHash, hashData, _) = await _apiService.GetCertHashAsync();
                if (successHash && hashData is not null && !string.IsNullOrEmpty(hashData.Hash))
                {
                    OctopusEngine.DynamicSslPublicKeyHash = hashData.Hash;
                }
                UpdateActiveNode(AppSecrets.AllowedSniPool[0]);
            }

            AppLogger.Log($"[VPN-TARGET] User initiated connect to: {targetServer.Location} ({targetServer.Ip}:{targetServer.Port}) | Role: {targetServer.Role ?? "worker"} | Provider: {targetServer.Provider} | Flag: {targetServer.Flag}");
            await Task.Run(async () => await _vpnService.StartVpnAsync(targetServer.Ip, targetServer.Port, orderedCandidates));
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[VPN CONNECT ERROR] {ex.Message}", ex);
            StopLoaderAnimation();
            ConnectButtonCore.IsEnabled = true;
            var muted = GetAppColor("TextMuted", s_grayText);
            UpdateIpStatusDisplay("Не назначен", muted, muted);
            await SetNeonStateAsync(
                GetStatusTextForState(AppVpnState.Disconnected, "Не в сети"),
                GetButtonTextForState(AppVpnState.Disconnected, "СТАРТ"),
                AppVpnState.Disconnected);
            var friendlyMsg = ex is SocketException or InvalidOperationException
                ? ex.Message
                : "Произошла ошибка при подключении/отключении";
            var logHint = !string.IsNullOrEmpty(AppLogger.LogFilePath)
                ? $"\n\nПодробный лог сохранен в:\n{AppLogger.LogFilePath}"
                : "";
            await NeoAlert.ShowAsync("Ошибка подключения", $"{friendlyMsg}{logHint}", "OK");
        }
        finally
        {
            _isBusy = false;
        }
    }


}
