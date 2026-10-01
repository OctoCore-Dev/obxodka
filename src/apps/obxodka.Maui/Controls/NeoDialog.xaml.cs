namespace obxodka.Maui.Controls;

public partial class NeoDialog : ContentView
{
    private TaskCompletionSource<bool>? _dialogTcs;

    public NeoDialog() => InitializeComponent();

    public Task<bool> ShowAsync(
        string title,
        string message,
        string? acceptText = null,
        string cancelText = "OK")
    {
        _ = _dialogTcs?.TrySetResult(false);
        var tcs = new TaskCompletionSource<bool>();
        _dialogTcs = tcs;

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                TitleLabel.Text = title;
                MessageLabel.Text = message;

                var isConfirm = !string.IsNullOrWhiteSpace(acceptText);
                CancelCol.Width = isConfirm ? GridLength.Star : GridLength.Auto;
                CancelButton.IsVisible = isConfirm;
                CancelButton.Text = cancelText;
                AcceptButton.Text = isConfirm ? acceptText : cancelText;
                Grid.SetColumn(AcceptButton, isConfirm ? 1 : 0);
                Grid.SetColumnSpan(AcceptButton, isConfirm ? 1 : 2);

                ConfigureDialogStyling(title);

                IsVisible = true;
                InputTransparent = false;
                DialogCard.Scale = 0.85;
                DialogCard.Opacity = 0;

                _ = await Task.WhenAll(
                    this.FadeToAsync(1, 200, Easing.CubicOut),
                    DialogCard.ScaleToAsync(1.0, 250, Easing.SpringOut),
                    DialogCard.FadeToAsync(1, 200, Easing.CubicOut));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NEO DIALOG ERROR] {ex.Message}");
                _ = tcs.TrySetResult(false);
            }
        });

        return tcs.Task;
    }

    public Task<bool> ShowErrorAsync(string title, string message, string buttonText = "OK") =>
        ShowAsync(title, message, null, buttonText);

    public Task<bool> ShowUpdateAsync(string title, string message, string acceptText = "Обновить", string cancelText = "Позже") =>
        ShowAsync(title, message, acceptText, cancelText);

    public Task<bool> ShowSuccessAsync(string title, string message, string buttonText = "OK") =>
        ShowAsync(title, message, null, buttonText);

    private void ConfigureDialogStyling(string title)
    {
        var lower = title.ToLowerInvariant();
        (DialogMauiIcon.Icon, var color, var dim) = lower switch
        {
            _ when lower.Contains("обновлен") || lower.Contains("update") => (FluentIcons.Rocket24, "Accent", "AccentDim"),
            _ when lower.Contains("удал") => (FluentIcons.Delete24, "Error", "ErrorDim"),
            _ when lower.Contains("ошибк") || lower.Contains("сбой") || lower.Contains("нет интернета") => (FluentIcons.DismissCircle24, "Error", "ErrorDim"),
            _ when lower.Contains("внимани") || lower.Contains("лимит") => (FluentIcons.Warning24, "Warning", "WarningDim"),
            _ when lower.Contains("успех") || lower.Contains("наград") || lower.Contains("поздравля") || lower.Contains("скопирован") || lower.Contains("отлично") => (FluentIcons.CheckmarkCircle24, "Success", "SuccessDim"),
            _ => (FluentIcons.Info24, "Primary", "PrimaryDim")
        };
        SetDialogGlow(color, dim);
    }

    private void SetDialogGlow(string colorKey, string dimColorKey)
    {
        if (Application.Current?.Resources.TryGetValue(colorKey, out var cVal) is true && cVal is Color c)
        {
            DialogShadow.Brush = new SolidColorBrush(c);
            IconShadow.Brush = new SolidColorBrush(c);
            DialogMauiIcon.IconColor = c;
            IconBadge.Stroke = new SolidColorBrush(c);
            AcceptButton.BackgroundColor = c;
        }

        if (Application.Current?.Resources.TryGetValue(dimColorKey, out var dVal) is true && dVal is Color d)
        {
            IconBadge.BackgroundColor = d;
        }
    }

    private async Task CloseAsync(bool result)
    {
        var tcs = _dialogTcs;
        _dialogTcs = null;

        try
        {
            _ = await Task.WhenAll(
                this.FadeToAsync(0, 150, Easing.CubicIn),
                DialogCard.ScaleToAsync(0.88, 150, Easing.CubicIn));

            InputTransparent = true;
            IsVisible = false;
        }
        catch
        {
        }
        finally
        {
            _ = tcs?.TrySetResult(result);
        }
    }

    private void OnAcceptClicked(object? sender, EventArgs e) => _ = CloseAsync(true);

    private void OnCancelClicked(object? sender, EventArgs e) => _ = CloseAsync(false);

    private void OnBackdropTapped(object? sender, EventArgs e) => _ = CloseAsync(false);
}
