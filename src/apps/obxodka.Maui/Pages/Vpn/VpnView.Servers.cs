//using только для сплит тунеля хватит удалять и натыкатся потостоянно на одно и тоже 5ый раз, рома ты тупой, ии не поможет ты сам это сделал
using Microsoft.Maui.Controls.Shapes;
using Path = System.IO.Path;

namespace obxodka.Pages;

//управление выбором серверов, пингами, модальным окном и hot-swap
public sealed partial class VpnView
{
    //получение флага страны сервера
    private static string GetServerFlag(VpnServerDto server) =>
        !string.IsNullOrWhiteSpace(server.Flag) ? server.Flag : "";

    //получение провайдера сервера
    private static string GetServerProvider(VpnServerDto server) =>
        !string.IsNullOrWhiteSpace(server.Provider) ? server.Provider : "Vultr";

    //получение роли узла
    private static string GetServerRole(VpnServerDto server) =>
        string.Equals(server.Role, "shield", StringComparison.OrdinalIgnoreCase) ? "Shield" :
        (string.Equals(server.Role, "main", StringComparison.OrdinalIgnoreCase) ? "Main" : "Worker");

    //получение текста статуса подключения с учетом темы
    private static string GetStatusTextForState(AppVpnState state, string fallback)
    {
        var st = ThemeManager.Instance?.ActiveTheme?.Vfx?.StatusText;
        return st is null
            ? fallback
            : state switch
            {
                AppVpnState.Disconnected => !string.IsNullOrWhiteSpace(st.Disconnected) ? st.Disconnected : fallback,
                AppVpnState.Connecting or AppVpnState.Reconnecting or AppVpnState.Disconnecting => !string.IsNullOrWhiteSpace(st.Connecting) ? st.Connecting : fallback,
                AppVpnState.Connected => !string.IsNullOrWhiteSpace(st.Connected) ? st.Connected : fallback,
                AppVpnState.Error => !string.IsNullOrWhiteSpace(st.Error) ? st.Error : fallback,
                _ => fallback
            };
    }

    //получение текста кнопки с учетом темы
    private static string GetButtonTextForState(AppVpnState state, string fallback)
    {
        var st = ThemeManager.Instance?.ActiveTheme?.Vfx?.StatusText;
        return st is null
            ? fallback
            : state switch
            {
                AppVpnState.Disconnected => !string.IsNullOrWhiteSpace(st.ButtonIdle) ? st.ButtonIdle : fallback,
                AppVpnState.Connecting or AppVpnState.Reconnecting or AppVpnState.Disconnecting => !string.IsNullOrWhiteSpace(st.ButtonConnecting) ? st.ButtonConnecting : fallback,
                AppVpnState.Connected => !string.IsNullOrWhiteSpace(st.ButtonConnected) ? st.ButtonConnected : fallback,
                AppVpnState.Error => !string.IsNullOrWhiteSpace(st.ButtonIdle) ? st.ButtonIdle : fallback,
                _ => fallback
            };
    }

    //загрузка сохраненного выбора локации из настроек
    private void LoadSavedServerSelection()
    {
        var cachedJson = Preferences.Get("cached_cluster_servers_json", string.Empty);
        if (!string.IsNullOrEmpty(cachedJson))
        {
            try
            {
                var cached = JsonSerializer.Deserialize(cachedJson, AppJsonContext.Default.ListVpnServerDto);
                if (cached is { Count: > 0 })
                {
                    _cachedServers = cached;
                }
            }
            catch { }
        }

        var savedIp = Preferences.Get("selected_server_ip", string.Empty);
        if (!string.IsNullOrEmpty(savedIp) && _cachedServers.Any(s => s.Ip == savedIp))
        {
            _selectedServer = _cachedServers.First(s => s.Ip == savedIp);
        }
        else if (_cachedServers.Count > 0)
        {
            _selectedServer = _cachedServers[0];
        }

        UpdateSelectedServerDisplay();
    }

    //обновление плашки выбранного сервера в интерфейсе
    private void UpdateSelectedServerDisplay()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (SelectedServerFlagLabel is null || SelectedServerLocationLabel is null || SelectedServerProviderLabel is null || SelectedServerFlagImage is null)
            {
                return;
            }

            var flagEmoji = _selectedServer != null ? GetServerFlag(_selectedServer) : "";
            var flagUrl = FlagHelper.GetFlagImageUrl(flagEmoji);

#if WINDOWS
            if (!string.IsNullOrEmpty(flagUrl))
            {
                SelectedServerFlagImage.Source = new UriImageSource
                {
                    Uri = new Uri(flagUrl),
                    CachingEnabled = true,
                    CacheValidity = TimeSpan.FromDays(30)
                };
                SelectedServerFlagImage.IsVisible = true;
                SelectedServerFlagLabel.IsVisible = false;
            }
            else
            {
                SelectedServerFlagLabel.Text = flagEmoji;
                SelectedServerFlagLabel.IsVisible = true;
                SelectedServerFlagImage.IsVisible = false;
            }
#else
            SelectedServerFlagLabel.Text = flagEmoji;
            SelectedServerFlagLabel.IsVisible = true;
            SelectedServerFlagImage.IsVisible = false;
#endif

            if (_selectedServer is null)
            {
                SelectedServerLocationLabel.Text = "Выберите сервер";
                SelectedServerProviderLabel.Text = "Нажмите для обновления";
                SelectedServerLatencyLabel.Text = "--";
                return;
            }

            SelectedServerLocationLabel.Text = _selectedServer.Location;
            SelectedServerProviderLabel.Text = $"{GetServerProvider(_selectedServer)} • {GetServerRole(_selectedServer)}";
            SelectedServerLatencyLabel.Text = _serverPings.TryGetValue(_selectedServer.Ip, out var pingMs)
                ? $"{pingMs} ms"
                : "Online";
        });
    }

    //запрос актуального списка серверов с api
    private async Task RefreshServerListAsync()
    {
        try
        {
            var (success, servers, _) = await _apiService.GetServersAsync();
            if (success && servers is { Count: > 0 })
            {
                _cachedServers = servers;
                try
                {
                    Preferences.Set("cached_cluster_servers_json", JsonSerializer.Serialize(servers, AppJsonContext.Default.ListVpnServerDto));
                }
                catch { }
            }

            if (_selectedServer is null && _cachedServers.Count > 0)
            {
                _selectedServer = _cachedServers[0];
            }
            else if (_selectedServer is not null && _cachedServers.Any(s => s.Ip == _selectedServer.Ip))
            {
                _selectedServer = _cachedServers.First(s => s.Ip == _selectedServer.Ip);
            }

            UpdateSelectedServerDisplay();
            PopulateServerList();
            if (_cachedServers.Count > 0)
            {
                _ = Task.Run(PingCachedServersAsync);
            }
        }
        catch
        {
        }
    }

    //фоновый опрос задержки доступных серверов
    private async Task PingCachedServersAsync()
    {
        var tasks = _cachedServers.Select(async s =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var cts = new CancellationTokenSource(2500);
                using var client = new TcpClient();
                await client.ConnectAsync(s.Ip, s.Port > 0 ? s.Port : 443, cts.Token).AsTask();
                sw.Stop();
                _serverPings[s.Ip] = sw.ElapsedMilliseconds;
            }
            catch
            {
                sw.Stop();
            }
        });

        await Task.WhenAll(tasks);
        UpdateSelectedServerDisplay();
        PopulateServerList();
    }

    //построение карточек серверов в карусели и списке
    private void PopulateServerList()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (ServerItemsContainer is null)
            {
                return;
            }

            var bgSurface = GetAppColor("BgSurface", Color.FromArgb("#161622"));
            var bgElevated = GetAppColor("BgElevated", Color.FromArgb("#202030"));
            var borderMedium = GetAppColor("BorderMedium", Color.FromArgb("#363650"));
            var textPrimary = GetAppColor("TextPrimary", Colors.White);
            var primaryColor = GetAppColor("Primary", Color.FromArgb("#7C3AED"));
            var primaryBright = GetAppColor("PrimaryBright", Color.FromArgb("#9F6FF0"));
            var primaryDim = GetAppColor("PrimaryDim", Color.FromArgb("#267C3AED"));
            var accentColor = GetAppColor("Accent", Color.FromArgb("#00E5FF"));
            var accentDim = GetAppColor("AccentDim", Color.FromArgb("#1A00E5FF"));
            var successColor = GetAppColor("Success", Color.FromArgb("#10B981"));

            ServerItemsContainer.Children.Clear();
            if (_cachedServers.Count == 0)
            {
                var retryCard = new Border
                {
                    Padding = new Thickness(14, 18),
                    StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(14) },
                    StrokeThickness = 1.0,
                    BackgroundColor = bgSurface,
                    Stroke = borderMedium
                };

                var retryStack = new VerticalStackLayout
                {
                    Spacing = 6,
                    HorizontalOptions = LayoutOptions.Center
                };

                var retryLabel = new Label
                {
                    Text = "Список серверов пуст",
                    FontSize = 13,
                    TextColor = textPrimary,
                    HorizontalTextAlignment = TextAlignment.Center
                };
                retryLabel.SetDynamicResource(Label.FontFamilyProperty, "AppFontBold");
                retryStack.Children.Add(retryLabel);

                var retrySubLabel = new Label
                {
                    Text = "Нажмите, чтобы обновить",
                    FontSize = 11,
                    TextColor = accentColor,
                    HorizontalTextAlignment = TextAlignment.Center
                };
                retrySubLabel.SetDynamicResource(Label.FontFamilyProperty, "AppFontMedium");
                retryStack.Children.Add(retrySubLabel);

                retryCard.Content = retryStack;

                var retryTap = new TapGestureRecognizer();
                retryTap.Tapped += async (s, e) =>
                {
                    ThemeAudioService.PlayClick();
                    _ = retryCard.BounceClickAsync();
                    await RefreshServerListAsync();
                };
                retryCard.GestureRecognizers.Add(retryTap);

                ServerItemsContainer.Children.Add(retryCard);
                return;
            }

            var currentSelectedIp = _selectedServer?.Ip ?? (_cachedServers.Count > 0 ? _cachedServers[0].Ip : string.Empty);

            foreach (var server in _cachedServers)
            {
                var isSelected = string.Equals(server.Ip, currentSelectedIp, StringComparison.OrdinalIgnoreCase);

                var card = new Border
                {
                    Padding = new Thickness(14, 10),
                    StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(14) },
                    StrokeThickness = isSelected ? 1.5 : 1.0,
                    BackgroundColor = isSelected ? bgElevated : bgSurface,
                    Stroke = isSelected ? primaryColor : borderMedium
                };

                var grid = new Grid
                {
                    ColumnDefinitions =
                    [
                        new ColumnDefinition(GridLength.Auto),
                        new ColumnDefinition(GridLength.Star),
                        new ColumnDefinition(GridLength.Auto)
                    ],
                    ColumnSpacing = 12,
                    VerticalOptions = LayoutOptions.Center
                };

                var flagEmoji = GetServerFlag(server);
                var flagUrl = FlagHelper.GetFlagImageUrl(flagEmoji);

                var flagBorder = new Border
                {
                    WidthRequest = 28,
                    HeightRequest = 19,
                    StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(3) },
                    StrokeThickness = 0.8,
                    Stroke = borderMedium,
                    VerticalOptions = LayoutOptions.Center
                };

                var flagGrid = new Grid();

#if WINDOWS
                if (!string.IsNullOrEmpty(flagUrl))
                {
                    var flagImage = new Image
                    {
                        Aspect = Aspect.AspectFill,
                        Source = new UriImageSource
                        {
                            Uri = new Uri(flagUrl),
                            CachingEnabled = true,
                            CacheValidity = TimeSpan.FromDays(30)
                        }
                    };
                    flagGrid.Children.Add(flagImage);
                }
                else
                {
                    var flagFallback = new Label
                    {
                        Text = flagEmoji,
                        FontSize = 14,
                        HorizontalOptions = LayoutOptions.Center,
                        VerticalOptions = LayoutOptions.Center
                    };
                    flagGrid.Children.Add(flagFallback);
                }
#else
                var flagLabel = new Label
                {
                    Text = flagEmoji,
                    FontSize = 18,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center,
                    Margin = new Thickness(0, -2, 0, 0)
                };
                flagGrid.Children.Add(flagLabel);
#endif
                flagBorder.Content = flagGrid;
                grid.Add(flagBorder, 0, 0);

                var infoStack = new VerticalStackLayout
                {
                    Spacing = 3,
                    VerticalOptions = LayoutOptions.Center
                };

                var nameLabel = new Label
                {
                    Text = server.Location,
                    FontSize = 13,
                    TextColor = textPrimary
                };
                nameLabel.SetDynamicResource(Label.FontFamilyProperty, "AppFontBold");
                infoStack.Children.Add(nameLabel);

                var badgeStack = new HorizontalStackLayout
                {
                    Spacing = 6,
                    VerticalOptions = LayoutOptions.Center
                };

                var providerBadge = new Border
                {
                    Padding = new Thickness(5, 1),
                    StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(4) },
                    StrokeThickness = 0.6,
                    BackgroundColor = accentDim,
                    Stroke = accentColor
                };

                var providerLabel = new Label
                {
                    Text = GetServerProvider(server),
                    FontSize = 9,
                    TextColor = accentColor
                };
                providerLabel.SetDynamicResource(Label.FontFamilyProperty, "AppFontMedium");
                providerBadge.Content = providerLabel;
                badgeStack.Children.Add(providerBadge);

                var isShieldRole = string.Equals(server.Role, "shield", StringComparison.OrdinalIgnoreCase);
                var isMainRole = string.Equals(server.Role, "main", StringComparison.OrdinalIgnoreCase);
                var isHighlightRole = isShieldRole || isMainRole;
                var roleBadge = new Border
                {
                    Padding = new Thickness(5, 1),
                    StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(4) },
                    StrokeThickness = 0.6,
                    BackgroundColor = isHighlightRole ? primaryDim : accentDim,
                    Stroke = isHighlightRole ? primaryBright : accentColor
                };

                var roleLabel = new Label
                {
                    Text = GetServerRole(server),
                    FontSize = 9,
                    TextColor = isHighlightRole ? primaryBright : accentColor
                };
                roleLabel.SetDynamicResource(Label.FontFamilyProperty, "AppFontMedium");
                roleBadge.Content = roleLabel;
                badgeStack.Children.Add(roleBadge);

                var pingDot = new BoxView
                {
                    WidthRequest = 5,
                    HeightRequest = 5,
                    CornerRadius = 2.5,
                    VerticalOptions = LayoutOptions.Center,
                    Color = successColor
                };
                badgeStack.Children.Add(pingDot);

                var pingText = _serverPings.TryGetValue(server.Ip, out var pingMs) ? $"{pingMs} ms" : "Онлайн";
                var pingLabel = new Label
                {
                    Text = pingText,
                    FontSize = 10,
                    TextColor = successColor,
                    VerticalOptions = LayoutOptions.Center
                };
                pingLabel.SetDynamicResource(Label.FontFamilyProperty, "AppFontMedium");
                badgeStack.Children.Add(pingLabel);

                infoStack.Children.Add(badgeStack);
                grid.Add(infoStack, 1, 0);

                var statusIcon = new MauiIcon
                {
                    Icon = isSelected ? FluentIcons.Checkmark24 : FluentIcons.Circle24,
                    IconColor = isSelected ? successColor : borderMedium,
                    IconSize = isSelected ? 20 : 18,
                    VerticalOptions = LayoutOptions.Center
                };
                grid.Add(statusIcon, 2, 0);

                card.Content = grid;

                var tap = new TapGestureRecognizer();
                tap.Tapped += async (s, e) =>
                {
                    if (_isBusy || _vpnService?.CurrentState is AppVpnState.Connecting or AppVpnState.Reconnecting or AppVpnState.Disconnecting)
                    {
                        return;
                    }
                    ThemeAudioService.PlayClick();
                    _ = card.BounceClickAsync();
                    var wasConnected = _vpnService?.CurrentState == AppVpnState.Connected;
                    _selectedServer = server;
                    Preferences.Set("selected_server_ip", server.Ip);
                    Preferences.Set("selected_server_location", server.Location);
                    Preferences.Set("selected_server_provider", GetServerProvider(server));
                    Preferences.Set("selected_server_flag", GetServerFlag(server));
                    if (!string.IsNullOrEmpty(server.Role))
                    {
                        Preferences.Set("selected_server_role", server.Role);
                    }
                    if (!string.IsNullOrEmpty(server.CertHash))
                    {
                        Preferences.Set("selected_server_cert_hash", server.CertHash);
                    }
                    UpdateSelectedServerDisplay();
                    await CloseServerModalAsync();

                    if (wasConnected)
                    {
                        await SwitchServerHotSwapAsync(server);
                    }
                };
                card.GestureRecognizers.Add(tap);

                ServerItemsContainer.Children.Add(card);
            }
        });
    }

    //бесшовное переключение сервера на лету без полного разрыва
    private async Task SwitchServerHotSwapAsync(VpnServerDto newServer)
    {
        if (_isBusy || _vpnService is null)
        {
            return;
        }

        _isBusy = true;
        try
        {
            StartLoaderAnimation();
            ConnectButtonCore.IsEnabled = false;
            var accent = GetAppColor("Accent", s_cyanAccent);
            UpdateIpStatusDisplay("Переключение...", accent, accent);
            await SetNeonStateAsync(
                GetStatusTextForState(AppVpnState.Connecting, "Смена сервера..."),
                GetButtonTextForState(AppVpnState.Connecting, "ЖДИТЕ"),
                AppVpnState.Connecting);

            await _vpnService.StopVpnAsync();

            var orderedCandidates = new List<VpnServerDto> { newServer };
            orderedCandidates.AddRange(_cachedServers.Where(s => s.Ip != newServer.Ip));

            var isShieldNode = string.Equals(newServer.Role, "shield", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(newServer.Provider, "Cloudflare", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(newServer.Ip, AppConfig.PrimaryDomain, StringComparison.OrdinalIgnoreCase) ||
                               newServer.Ip.EndsWith($".{AppConfig.PrimaryDomain}", StringComparison.OrdinalIgnoreCase);

            if (isShieldNode)
            {
                OctopusEngine.DynamicSslPublicKeyHash = null;
                UpdateActiveNode(newServer.Ip);
            }
            else if (!string.IsNullOrWhiteSpace(newServer.CertHash))
            {
                OctopusEngine.DynamicSslPublicKeyHash = newServer.CertHash;
                UpdateActiveNode(AppSecrets.AllowedSniPool[0]);
            }
            else
            {
                UpdateActiveNode(AppSecrets.AllowedSniPool[0]);
            }

            AppLogger.Log($"[HOT-SWAP] Seamlessly switching to server: {newServer.Location} ({newServer.Ip}:{newServer.Port})");
            await Task.Run(async () => await _vpnService.StartVpnAsync(newServer.Ip, newServer.Port, orderedCandidates));
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[HOT-SWAP ERROR] {ex.Message}", ex);
            StopLoaderAnimation();
            ConnectButtonCore.IsEnabled = true;
            var muted = GetAppColor("TextMuted", s_grayText);
            UpdateIpStatusDisplay("Не назначен", muted, muted);
            await SetNeonStateAsync(
                GetStatusTextForState(AppVpnState.Disconnected, "Не в сети"),
                GetButtonTextForState(AppVpnState.Disconnected, "СТАРТ"),
                AppVpnState.Disconnected);
            if (ParentPage is not null)
            {
                await ParentPage.DisplayAlertAsync("Смена сервера", $"Не удалось переключить сервер: {ex.Message}", "OK");
            }
        }
        finally
        {
            _isBusy = false;
        }
    }

    //флаг активности модального окна выбора серверов
    public bool IsServerModalOpen => ServerModalOverlay?.IsVisible == true;

    //открытие модального окна выбора локации
    private async Task OpenServerModalAsync()
    {
        if (_isBusy || _vpnService?.CurrentState is AppVpnState.Connecting or AppVpnState.Reconnecting or AppVpnState.Disconnecting)
        {
            return;
        }
        if (_cachedServers.Count == 0)
        {
            await RefreshServerListAsync();
        }
        else
        {
            PopulateServerList();
            _ = Task.Run(PingCachedServersAsync);
        }

        _ = ParentPage?.SetSidebarDimmedAsync(true);
        ServerModalOverlay.IsVisible = true;
        ServerModalOverlay.InputTransparent = false;
        ServerModalCard.Scale = 0.95;
        ServerModalCard.Opacity = 0.0;

        _ = ServerModalOverlay.FadeToAsync(1.0, 200, Easing.CubicOut);
        _ = ServerModalCard.FadeToAsync(1.0, 200, Easing.CubicOut);
        _ = await ServerModalCard.ScaleToAsync(1.0, 250, Easing.CubicOut);
    }

    //закрытие модального окна выбора локации
    public async Task CloseServerModalAsync()
    {
        ServerModalOverlay.InputTransparent = true;
        _ = ParentPage?.SetSidebarDimmedAsync(false);
        _ = ServerModalCard.ScaleToAsync(0.95, 180, Easing.CubicIn);
        _ = ServerModalCard.FadeToAsync(0.0, 180, Easing.CubicIn);
        _ = await ServerModalOverlay.FadeToAsync(0.0, 180, Easing.CubicIn);
        ServerModalOverlay.IsVisible = false;
    }

    //клик по затемненному фону для закрытия модалки
    private void OnServerModalBackdropTapped(object? sender, EventArgs e)
    {
        ThemeAudioService.PlayClick();
        _ = CloseServerModalAsync();
    }

    //нажатие кнопки закрытия модального окна
    private void OnCloseServerModalClicked(object? sender, EventArgs e)
    {
        ThemeAudioService.PlayClick();
        _ = CloseServerModalAsync();
    }

    //ручное обновление списка серверов по кнопке
    private async void OnRefreshServersClickedAsync(object? sender, EventArgs e)
    {
        ThemeAudioService.PlayClick();
        if (sender is VisualElement ve)
        {
            _ = ve.BounceClickAsync();
        }
        await RefreshServerListAsync();
    }

    //клик по карточке выбора сервера для открытия модалки
    private async void OnServerSelectorTappedAsync(object? sender, EventArgs e)
    {
        if (_isBusy || _vpnService?.CurrentState is AppVpnState.Connecting or AppVpnState.Reconnecting or AppVpnState.Disconnecting)
        {
            return;
        }

        ThemeAudioService.PlayClick();
        if (ServerSelectorButton is not null)
        {
            _ = ServerSelectorButton.BounceClickAsync();
        }
        await OpenServerModalAsync();
    }


}
