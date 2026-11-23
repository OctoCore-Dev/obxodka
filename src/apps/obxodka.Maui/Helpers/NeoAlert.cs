namespace obxodka.Helpers;

public static class NeoAlert
{
    private static Page? GetFallbackPage() =>
        Application.Current?.Windows is { Count: > 0 } windows ? windows[0].Page : null;

    public static async Task<bool> ShowConfirmAsync(string title, string message, string accept = "OK", string cancel = "Отмена")
    {
        return MainPage.Current is not null
            ? await MainPage.Current.ShowCustomDialogAsync(title, message, accept, cancel)
            : MainThread.IsMainThread
                ? await ShowFallbackConfirmAsync(title, message, accept, cancel)
                : await MainThread.InvokeOnMainThreadAsync(() => ShowFallbackConfirmAsync(title, message, accept, cancel));
    }

    public static async Task ShowAsync(string title, string message, string cancel = "OK")
    {
        if (MainPage.Current is not null)
        {
            _ = await MainPage.Current.ShowCustomDialogAsync(title, message, null, cancel);
            return;
        }

        if (MainThread.IsMainThread)
        {
            await ShowFallbackAlertAsync(title, message, cancel);
            return;
        }

        await MainThread.InvokeOnMainThreadAsync(() => ShowFallbackAlertAsync(title, message, cancel));
    }

    private static async Task<bool> ShowFallbackConfirmAsync(string title, string message, string accept, string cancel)
    {
        var page = GetFallbackPage();
        return page is not null && await page.DisplayAlertAsync(title, message, accept, cancel);
    }

    private static async Task ShowFallbackAlertAsync(string title, string message, string cancel)
    {
        var page = GetFallbackPage();
        if (page is not null)
        {
            await page.DisplayAlertAsync(title, message, cancel);
        }
    }
}
