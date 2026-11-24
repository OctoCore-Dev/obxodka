namespace obxodka.Controls.Dialogs;

public partial class NeoDialog
{
    private static readonly SearchValues<string> s_updateKeywords =
        SearchValues.Create(["обновлен", "update"], StringComparison.OrdinalIgnoreCase);

    private static readonly SearchValues<string> s_errorKeywords =
        SearchValues.Create(["ошибк", "сбой", "нет интернета", "нет подключения", "нет доступа"], StringComparison.OrdinalIgnoreCase);

    private static readonly SearchValues<string> s_warningKeywords =
        SearchValues.Create(["внимани", "лимит"], StringComparison.OrdinalIgnoreCase);

    private static readonly SearchValues<string> s_successKeywords =
        SearchValues.Create(["успех", "наград", "поздравля", "скопирован", "отлично"], StringComparison.OrdinalIgnoreCase);

    private TaskCompletionSource<bool>? _dialogTcs;
    private DialogTone _lastTone = DialogTone.Info;
    private int _dialogGeneration;

    public NeoDialog() => InitializeComponent();

    public Task<bool> ShowErrorAsync(string title, string message, string buttonText = "OK") =>
        ShowAsync(title, message, null, buttonText, DialogTone.Error);

    public Task<bool> ShowUpdateAsync(string title, string message, string acceptText = "Обновить", string cancelText = "Позже") =>
        ShowAsync(title, message, acceptText, cancelText, DialogTone.Update);

    public Task<bool> ShowSuccessAsync(string title, string message, string buttonText = "OK") =>
        ShowAsync(title, message, null, buttonText, DialogTone.Success);

    private void OnAcceptClicked(object? sender, EventArgs e) => _ = CloseAsync(true);

    private void OnCancelClicked(object? sender, EventArgs e) => _ = CloseAsync(false);

    private void OnBackdropTapped(object? sender, EventArgs e) => _ = CloseAsync(false);

    public void OnThemeChanged()
    {
        if (IsVisible)
        {
            ConfigureDialogStyling(_lastTone);
        }
    }

    public Task<bool> ShowAsync(
        string title,
        string message,
        string? acceptText = null,
        string cancelText = "OK",
        DialogTone? tone = null)
    {
        var generation = Interlocked.Increment(ref _dialogGeneration);
        _ = _dialogTcs?.TrySetResult(false);
        var tcs = new TaskCompletionSource<bool>();
        _dialogTcs = tcs;
        var resolvedTone = tone ?? ResolveTone(title);
        _lastTone = resolvedTone;

        MainThread.BeginInvokeOnMainThread(async void () =>
        {
            try
            {
                if (generation != Volatile.Read(ref _dialogGeneration))
                {
                    _ = tcs.TrySetResult(false);
                    return;
                }

                TitleLabel.Text = title;
                MessageLabel.Text = message;

                var isConfirm = !string.IsNullOrWhiteSpace(acceptText);
                CancelCol.Width = isConfirm ? GridLength.Star : GridLength.Auto;
                CancelButton.IsVisible = isConfirm;
                CancelButton.Text = cancelText;
                AcceptButton.Text = (isConfirm ? acceptText : cancelText) ?? "";
                Grid.SetColumn(AcceptButton, isConfirm ? 1 : 0);
                Grid.SetColumnSpan(AcceptButton, isConfirm ? 1 : 2);

                ConfigureDialogStyling(resolvedTone);
                ThemeAudioService.PlayNotification();

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
                AppLogger.LogWarning($"[NEO DIALOG ERROR] {ex.Message}");
                _ = tcs.TrySetResult(false);
            }
        });

        return tcs.Task;
    }

    private static DialogTone ResolveTone(string title)
    {
        var span = title.AsSpan();
        return span.ContainsAny(s_updateKeywords)
            ? DialogTone.Update
            : span.Contains("удал", StringComparison.OrdinalIgnoreCase)
                ? DialogTone.Delete
                : span.ContainsAny(s_errorKeywords)
                    ? DialogTone.Error
                    : span.ContainsAny(s_warningKeywords)
                        ? DialogTone.Warning
                        : span.ContainsAny(s_successKeywords)
                            ? DialogTone.Success
                            : DialogTone.Info;
    }

    private void ConfigureDialogStyling(DialogTone tone)
    {
#pragma warning disable IDE0072 // C# 15 closed hierarchy проверяется компилятором (CS8509)
        (DialogMauiIcon.Icon, var color, var dim) = tone switch
        {
            DialogUpdateTone => (FluentIcons.Rocket24, "Accent", "AccentDim"),
            DialogDeleteTone => (FluentIcons.Delete24, "Error", "ErrorDim"),
            DialogErrorTone => (FluentIcons.DismissCircle24, "Error", "ErrorDim"),
            DialogWarningTone => (FluentIcons.Warning24, "Warning", "WarningDim"),
            DialogSuccessTone => (FluentIcons.CheckmarkCircle24, "Success", "SuccessDim"),
            DialogInfoTone => (FluentIcons.Info24, "Primary", "PrimaryDim")
        };
#pragma warning restore IDE0072
        SetDialogGlow(color, dim);
    }

    private void SetDialogGlow(string colorKey, string dimColorKey)
    {
        if (Application.Current?.Resources.TryGetValue(colorKey, out var cVal) is true && cVal is Color c)
        {
            DialogMauiIcon.IconColor = c;
            IconBadge.Stroke = new SolidColorBrush(c);
            AcceptButton.BackgroundColor = c;

            var luminance = 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue;
            AcceptButton.TextColor = luminance > 0.62 ? Color.FromArgb("#0E0E14") : Colors.White;
        }

        if (Application.Current?.Resources.TryGetValue(dimColorKey, out var dVal) is true && dVal is Color d)
        {
            IconBadge.BackgroundColor = d;
        }
    }

    private async Task CloseAsync(bool result)
    {
        ThemeAudioService.PlayClick();
        var generation = Volatile.Read(ref _dialogGeneration);
        var tcs = _dialogTcs;
        _dialogTcs = null;

        try
        {
            _ = await Task.WhenAll(
                this.FadeToAsync(0, 150, Easing.CubicIn),
                DialogCard.ScaleToAsync(0.88, 150, Easing.CubicIn));

            if (generation == Volatile.Read(ref _dialogGeneration))
            {
                InputTransparent = true;
                IsVisible = false;
            }
        }
        catch
        {
            // ignored, да игнорить
        }
        finally
        {
            _ = tcs?.TrySetResult(result);
        }
    }
}
