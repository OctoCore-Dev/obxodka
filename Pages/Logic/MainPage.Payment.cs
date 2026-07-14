namespace obxodka.Pages;

internal sealed partial class MainPage
{
    private async void OnBuyTokensClickedAsync(object? sender, EventArgs? e)
    {
        var session = await AuthManager.LoadSessionAsync();
        var email = Uri.EscapeDataString(session.Email ?? "");
        PaymentWebView.Source = $"https://obxodka.one/Payment/Pay?appEmail={email}";
        SwitchTab("payment");
    }

    private async void OnPaymentWebViewNavigatingAsync(object sender, WebNavigatingEventArgs e)
    {
        PaymentLoader.IsVisible = false;

        if (e.Url.Contains("obxodka.one/Payment/Success", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            await DisplayAlertAsync("Успех", "Оплата прошла успешно! Время скоро обновится.", "OK");
            var session = await AuthManager.LoadSessionAsync();
            await SyncBalanceFromServerAsync(session);
            SwitchTab("profile");
        }
        else if (e.Url.Contains("obxodka.one/Payment/Fail", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            await DisplayAlertAsync("Отмена", "Оплата отменена.", "OK");
            SwitchTab("profile");
        }
    }
}
