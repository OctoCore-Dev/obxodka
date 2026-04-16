namespace obxodka.Pages;
internal sealed partial class PaymentPage : ContentPage
{
    public PaymentPage()
    {
        InitializeComponent();
    }
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        var session = await AuthManager.LoadSessionAsync();
        string email = Uri.EscapeDataString(session.Email ?? "");
        PaymentWebView.Source = $"https://obxodka.one/Payment/Pay?appEmail={email}";
    }
    private void OnWebViewNavigating(object sender, WebNavigatingEventArgs e)
    {
        Loader.IsVisible = false;
        if (e.Url.Contains("obxodka.one/Payment/Success"))
        {
            e.Cancel = true;
            DisplayAlertAsync("Успех", "Оплата прошла успешно! Время скоро обновится.", "OK");
            Navigation.PopAsync();
        }
        else if (e.Url.Contains("obxodka.one/Payment/Fail"))
        {
            e.Cancel = true;
            DisplayAlertAsync("Отмена", "Оплата отменена.", "OK");
            Navigation.PopAsync();
        }
    }
}