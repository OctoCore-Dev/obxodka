namespace obxodka.Views;

public sealed partial class FriendsView : ContentView
{
    private ApiService _apiService = null!;
    public event EventHandler? BackRequested;

    public FriendsView()
    {
        InitializeComponent();

        FriendsScrollView.SizeChanged += (s, e) =>
        {
            if (FriendsScrollView.Width > 0)
            {
                ApplyCardWidth(FriendsScrollView.Width);
            }
        };
    }

    public void ForceLayoutWidth()
    {
        if (FriendsScrollView.Width > 0)
        {
            ApplyCardWidth(FriendsScrollView.Width);
        }
        else if (Width > 0 && Content is Grid g)
        {
            var avail = Width - g.Padding.HorizontalThickness;
            if (avail > 0)
            {
                ApplyCardWidth(avail);
            }
        }
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (width > 0 && Content is Grid g)
        {
            var availableWidth = width - g.Padding.HorizontalThickness;
            if (availableWidth > 0)
            {
                ApplyCardWidth(availableWidth);
            }
        }
    }

    private void ApplyCardWidth(double targetWidth)
    {
        if (targetWidth <= 0 || FriendsCardsStack is null)
        {
            return;
        }

        var safeWidth = Math.Min(targetWidth - 6, 950);
        if (safeWidth <= 0)
        {
            return;
        }

        FriendsCardsStack.WidthRequest = safeWidth;
        FriendsCardsStack.MaximumWidthRequest = safeWidth;
    }

    public void Initialize(ApiService apiService)
    {
        _apiService = apiService;
        _ = LoadInitialDataAsync();
    }

    public async Task OnAppearingAsync()
    {
        await LoadInitialDataAsync();
        RefreshStats();
    }

    public async Task PlayEntranceAnimationAsync()
    {
        Opacity = 1;
        TranslationY = 0;
        await this.PlayCardsEntranceAsync(35, 240);
    }

    public void UpdateCardOpacity()
    {
        var bgSurface = (Application.Current?.Resources.TryGetValue("BgSurface", out var bg) == true && bg is Color bgColor)
            ? bgColor
            : Color.FromArgb("#161622");

        BackBtn.BackgroundColor = bgSurface;
        CardPersonalCode.BackgroundColor = bgSurface;
        CardActivate.BackgroundColor = bgSurface;
        CardReward.BackgroundColor = bgSurface;
    }

    public void SetHeaderTopInset(double top)
    {
        if (DeviceInfo.Idiom == DeviceIdiom.Phone && Content is Grid g)
        {
            g.Padding = new Thickness(16, Math.Max(top + 4, 12), 16, 0);
        }
    }

    public void OnThemeChanged()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateCardOpacity();
            if (ActivateFeedbackLabel.IsVisible)
            {
                ActivateFeedbackLabel.TextColor = SuccessColor;
            }
        });
    }

    private async Task LoadInitialDataAsync()
    {
        var savedCode = MeshSettings.ReferralCode;
        if (!string.IsNullOrWhiteSpace(savedCode))
        {
            MyCodeLabel.Text = savedCode;
        }

        try
        {
            var (success, data, _) = await _apiService.GetMyReferralCodeAsync();
            if (success && data is not null && !string.IsNullOrWhiteSpace(data.Code))
            {
                MyCodeLabel.Text = data.Code;
                MeshSettings.ReferralCode = data.Code;
            }
        }
        catch { }
    }

    private void OnBackClicked(object? sender, EventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    private async void OnCopyCodeClickedAsync(object? sender, EventArgs e)
    {
        try
        {
            await Clipboard.Default.SetTextAsync(MyCodeLabel.Text);
            ActivateFeedbackLabel.Text = "Код скопирован в буфер обмена!";
            ActivateFeedbackLabel.TextColor = SuccessColor;
            ActivateFeedbackLabel.IsVisible = true;
        }
        catch { }
    }

    private static Color SuccessColor =>
        Application.Current?.Resources.TryGetValue("Success", out var s) == true && s is Color sc ? sc : Color.FromArgb("#10B981");

    private static Color ErrorColor =>
        Application.Current?.Resources.TryGetValue("Error", out var e) == true && e is Color ec ? ec : Color.FromArgb("#EF4444");

    private async void OnActivateCodeClickedAsync(object? sender, EventArgs e)
    {
        var inputCode = FriendCodeEntry.Text?.Trim().ToUpperInvariant().Replace(" ", "");
        if (string.IsNullOrWhiteSpace(inputCode) || inputCode.Length < 6)
        {
            ActivateFeedbackLabel.Text = "Введите корректный код друга.";
            ActivateFeedbackLabel.TextColor = ErrorColor;
            ActivateFeedbackLabel.IsVisible = true;
            return;
        }

        if (string.Equals(inputCode, MyCodeLabel.Text, StringComparison.OrdinalIgnoreCase))
        {
            ActivateFeedbackLabel.Text = "Нельзя активировать собственный код.";
            ActivateFeedbackLabel.TextColor = ErrorColor;
            ActivateFeedbackLabel.IsVisible = true;
            return;
        }

        ActivateCodeBtn.IsEnabled = false;
        try
        {
            var (success, data, error) = await _apiService.ActivateReferralCodeAsync(inputCode);
            if (success)
            {
                ActivateFeedbackLabel.Text = data?.Message ?? $"Друг {inputCode} успешно активирован!";
                ActivateFeedbackLabel.TextColor = SuccessColor;
                ActivateFeedbackLabel.IsVisible = true;
                FriendCodeEntry.Text = string.Empty;
            }
            else
            {
                ActivateFeedbackLabel.Text = error ?? "Не удалось активировать код.";
                ActivateFeedbackLabel.TextColor = ErrorColor;
                ActivateFeedbackLabel.IsVisible = true;
            }
        }
        catch (Exception ex)
        {
            ActivateFeedbackLabel.Text = $"Ошибка: {ex.Message}";
            ActivateFeedbackLabel.TextColor = ErrorColor;
            ActivateFeedbackLabel.IsVisible = true;
        }
        finally
        {
            ActivateCodeBtn.IsEnabled = true;
        }
    }

    private void RefreshStats()
    {
        try
        {
            long bytesRelayed = 0;
            if (OperatingSystem.IsWindows() && OctopusEngine.ActiveRelayServer is not null)
            {
                bytesRelayed = OctopusEngine.ActiveRelayServer.Stats.BytesRelayedTotal;
            }

            var fiveGb = 5L * 1024 * 1024 * 1024;
            var currentGb = bytesRelayed / (1024.0 * 1024.0 * 1024.0);
            var progress = Math.Clamp(bytesRelayed / (double)fiveGb, 0.0, 1.0);

            RelayProgressBar.Progress = progress;
            ProgressTextLabel.Text = $"{currentGb:F1} / 5.0 ГБ";
            ClaimRewardBtn.IsEnabled = bytesRelayed >= fiveGb;
        }
        catch { }
    }

    private async void OnClaimRewardClickedAsync(object? sender, EventArgs e)
    {
        ClaimRewardBtn.IsEnabled = false;
        try
        {
            var claimId = Guid.NewGuid().ToString("N");
            var (success, data, error) = await _apiService.ClaimReferralRewardAsync(claimId);
            if (success)
            {
                await NeoAlert.ShowAsync("Награда получена!", $"Вам успешно начислено +{data?.HoursGranted ?? 5} часов подписки за помощь сети Obxodka.", "Отлично");
            }
            else
            {
                await NeoAlert.ShowAsync("Ошибка", error ?? "Не удалось получить награду", "OK");
            }
            RefreshStats();
        }
        catch (Exception ex)
        {
            await NeoAlert.ShowAsync("Ошибка", ex.Message, "OK");
        }
        finally
        {
            ClaimRewardBtn.IsEnabled = true;
        }
    }
}
