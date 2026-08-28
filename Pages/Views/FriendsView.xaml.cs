namespace obxodka.Views;

public sealed partial class FriendsView : ContentView
{
    private ApiService _apiService = null!;
    public event EventHandler? BackRequested;

    public FriendsView() => InitializeComponent();

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
            ActivateFeedbackLabel.TextColor = Color.FromArgb("#10B981");
            ActivateFeedbackLabel.IsVisible = true;
        }
        catch { }
    }

    private async void OnActivateCodeClickedAsync(object? sender, EventArgs e)
    {
        var inputCode = FriendCodeEntry.Text?.Trim().ToUpperInvariant().Replace(" ", "");
        if (string.IsNullOrWhiteSpace(inputCode) || inputCode.Length < 6)
        {
            ActivateFeedbackLabel.Text = "Введите корректный код друга.";
            ActivateFeedbackLabel.TextColor = Color.FromArgb("#EF4444");
            ActivateFeedbackLabel.IsVisible = true;
            return;
        }

        if (string.Equals(inputCode, MyCodeLabel.Text, StringComparison.OrdinalIgnoreCase))
        {
            ActivateFeedbackLabel.Text = "Нельзя активировать собственный код.";
            ActivateFeedbackLabel.TextColor = Color.FromArgb("#EF4444");
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
                ActivateFeedbackLabel.TextColor = Color.FromArgb("#10B981");
                ActivateFeedbackLabel.IsVisible = true;
                FriendCodeEntry.Text = string.Empty;
            }
            else
            {
                ActivateFeedbackLabel.Text = error ?? "Не удалось активировать код.";
                ActivateFeedbackLabel.TextColor = Color.FromArgb("#EF4444");
                ActivateFeedbackLabel.IsVisible = true;
            }
        }
        catch (Exception ex)
        {
            ActivateFeedbackLabel.Text = $"Ошибка: {ex.Message}";
            ActivateFeedbackLabel.TextColor = Color.FromArgb("#EF4444");
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
            var page = Application.Current?.Windows is { Count: > 0 } windows ? windows[0].Page : null;

            if (success)
            {
                if (page is not null)
                {
                    await page.DisplayAlertAsync("Награда получена!", $"Вам успешно начислено +{data?.HoursGranted ?? 5} часов подписки за помощь сети Obxodka.", "Отлично");
                }
            }

            else
            {
                if (page is not null)
                {
                    await page.DisplayAlertAsync("Ошибка", error ?? "Не удалось получить награду", "OK");
                }
            }
            RefreshStats();
        }
        catch (Exception ex)
        {
            var page = Application.Current?.Windows is { Count: > 0 } windows ? windows[0].Page : null;
            if (page is not null)
            {
                await page.DisplayAlertAsync("Ошибка", ex.Message, "OK");
            }
        }
        finally
        {
            ClaimRewardBtn.IsEnabled = true;
        }
    }
}
