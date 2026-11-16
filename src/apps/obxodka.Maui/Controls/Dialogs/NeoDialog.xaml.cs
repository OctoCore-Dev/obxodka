namespace obxodka.Dialogs;

//закрытая иерархия тонов диалога с проверкой полноты перебора в компиляторе c# 15
public closed record DialogTone
{
    public static DialogTone Update { get; } = new DialogUpdateTone();
    public static DialogTone Delete { get; } = new DialogDeleteTone();
    public static DialogTone Error { get; } = new DialogErrorTone();
    public static DialogTone Warning { get; } = new DialogWarningTone();
    public static DialogTone Success { get; } = new DialogSuccessTone();
    public static DialogTone Info { get; } = new DialogInfoTone();
}

public sealed record DialogUpdateTone : DialogTone;
public sealed record DialogDeleteTone : DialogTone;
public sealed record DialogErrorTone : DialogTone;
public sealed record DialogWarningTone : DialogTone;
public sealed record DialogSuccessTone : DialogTone;
public sealed record DialogInfoTone : DialogTone;

public partial class NeoDialog : ContentView
{
    private TaskCompletionSource<bool>? _dialogTcs;
    private DialogTone _lastTone = DialogTone.Info;

    public NeoDialog() => InitializeComponent();

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
        _ = _dialogTcs?.TrySetResult(false);
        var tcs = new TaskCompletionSource<bool>();
        _dialogTcs = tcs;
        var resolvedTone = tone ?? ResolveTone(title);
        _lastTone = resolvedTone;

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
                obxodka.Shared.Logging.AppLogger.LogWarning($"[NEO DIALOG ERROR] {ex.Message}");
                _ = tcs.TrySetResult(false);
            }
        });

        return tcs.Task;
    }

    public Task<bool> ShowErrorAsync(string title, string message, string buttonText = "OK") =>
        ShowAsync(title, message, null, buttonText, DialogTone.Error);

    public Task<bool> ShowUpdateAsync(string title, string message, string acceptText = "Обновить", string cancelText = "Позже") =>
        ShowAsync(title, message, acceptText, cancelText, DialogTone.Update);

    public Task<bool> ShowSuccessAsync(string title, string message, string buttonText = "OK") =>
        ShowAsync(title, message, null, buttonText, DialogTone.Success);

    private static DialogTone ResolveTone(string title)
    {
        var lower = title.ToLowerInvariant();
        return lower switch
        {
            _ when lower.Contains("обновлен") || lower.Contains("update") => DialogTone.Update,
            _ when lower.Contains("удал") => DialogTone.Delete,
            _ when lower.Contains("ошибк") || lower.Contains("сбой") || lower.Contains("нет интернета") || lower.Contains("нет подключения") || lower.Contains("нет доступа") => DialogTone.Error,
            _ when lower.Contains("внимани") || lower.Contains("лимит") => DialogTone.Warning,
            _ when lower.Contains("успех") || lower.Contains("наград") || lower.Contains("поздравля") || lower.Contains("скопирован") || lower.Contains("отлично") => DialogTone.Success,
            _ => DialogTone.Info
        };
    }

    private void ConfigureDialogStyling(DialogTone tone)
    {
#pragma warning disable IDE0072 // C# 15 closed hierarchy is compiler-exhaustive (CS8509)
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

            var luminance = (0.299 * c.Red) + (0.587 * c.Green) + (0.114 * c.Blue);
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
