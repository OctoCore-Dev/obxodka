namespace obxodka.Helpers;

public static class NeoAlert
{
    public static Task<bool> ShowConfirmAsync(string title, string message, string accept = "OK", string cancel = "Отмена") =>
        MainPage.Current is not null
            ? MainPage.Current.ShowCustomDialogAsync(title, message, accept, cancel)
            : MainThread.IsMainThread
                ? Application.Current?.Windows is { Count: > 0 } windows && windows[0].Page is { } page
                    ? page.DisplayAlertAsync(title, message, accept, cancel)
                    : Task.FromResult(false)
                : MainThread.InvokeOnMainThreadAsync(() =>
                    Application.Current?.Windows is { Count: > 0 } windows && windows[0].Page is { } page
                        ? page.DisplayAlertAsync(title, message, accept, cancel)
                        : Task.FromResult(false));

    public static Task ShowAsync(string title, string message, string cancel = "OK") =>
        MainPage.Current is not null
            ? MainPage.Current.ShowCustomDialogAsync(title, message, null, cancel)
            : MainThread.IsMainThread
                ? Application.Current?.Windows is { Count: > 0 } windows && windows[0].Page is { } page
                    ? page.DisplayAlertAsync(title, message, cancel)
                    : Task.CompletedTask
                : MainThread.InvokeOnMainThreadAsync(() =>
                    Application.Current?.Windows is { Count: > 0 } windows && windows[0].Page is { } page
                        ? page.DisplayAlertAsync(title, message, cancel)
                        : Task.CompletedTask);
}
