namespace obxodka.Helpers;

public static class NeoAlert
{
    private static Page? GetFallbackPage() =>
        Application.Current?.Windows is { Count: > 0 } windows ? windows[0].Page : null;

    public static async Task<bool> ShowConfirmAsync(string title, string message, string accept = "OK", string cancel = "Отмена")
    {
        if (MainPage.Current is not null)
        {
            return await MainPage.Current.ShowCustomDialogAsync(title, message, accept, cancel);
        }

        if (MainThread.IsMainThread)
        {
            var page = GetFallbackPage();
            return page is not null && await page.DisplayAlertAsync(title, message, accept, cancel);
        }

        return await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var page = GetFallbackPage();
            return page is not null && await page.DisplayAlertAsync(title, message, accept, cancel);
        });
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
            var page = GetFallbackPage();
            if (page is not null)
            {
                await page.DisplayAlertAsync(title, message, cancel);
            }
            return;
        }

        await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var page = GetFallbackPage();
            if (page is not null)
            {
                await page.DisplayAlertAsync(title, message, cancel);
            }
        });
    }
}
