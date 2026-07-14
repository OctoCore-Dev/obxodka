namespace obxodka.Views;

public partial class PaymentView : ContentView
{
    private MainPage _parent = null!;
    private ApiService _apiService = null!;

    public event EventHandler? PaymentCompleted;
    public event EventHandler? PaymentCancelled;

    public PaymentView() => InitializeComponent();

    public void Initialize(MainPage parent, ApiService apiService)
    {
        _parent = parent;
        _apiService = apiService;
    }

    public async Task PlayEntranceAnimationAsync()
    {
        Opacity = 1;
        TranslationY = 0;
        await UIAnimations.PlayEntranceCascadeAsync(80, 450, Card300, Card500, Card1000);
    }

    public void LoadPaymentPage() => ResetPaymentView();

    private async void OnBuy300ClickedAsync(object? sender, EventArgs e) => await OpenBrowserPaymentAsync(300, Btn300Border, Btn300, Loader300);
    private async void OnBuy500ClickedAsync(object? sender, EventArgs e) => await OpenBrowserPaymentAsync(500, Btn500Border, Btn500, Loader500);
    private async void OnBuy1000ClickedAsync(object? sender, EventArgs e) => await OpenBrowserPaymentAsync(1000, Btn1000Border, Btn1000, Loader1000);

    private async Task OpenBrowserPaymentAsync(int amount, Border border, Button button, ActivityIndicator indicator)
    {
        await UIAnimations.SetButtonLoadingAsync(border, button, indicator, true);
        var (success, payUrl, error) = await _apiService.GeneratePaymentLinkAsync(amount);
        await UIAnimations.SetButtonLoadingAsync(border, button, indicator, false);

        if (success && !string.IsNullOrEmpty(payUrl))
        {
            PaymentLoader.IsVisible = true;
            PaymentLoader.IsRunning = true;
            PaymentWebView.Source = payUrl;

            await UIAnimations.SwitchViewAsync(CardsScrollView, BrowserBorder);
        }
        else
        {
            await _parent.DisplayAlertAsync("Ошибка", error ?? "Не удалось сгенерировать ссылку", "OK");
        }
    }

    private void OnCancelPaymentTapped(object sender, TappedEventArgs e)
    {
        PaymentWebView.Source = "about:blank";
        ResetPaymentView();
    }

    public void ResetPaymentView()
    {
        CardsScrollView.IsVisible = true;
        CardsScrollView.Opacity = 1;
        CardsScrollView.TranslationX = 0;
        BrowserBorder.IsVisible = false;
    }



    private async void OnPaymentWebViewNavigatingAsync(object sender, WebNavigatingEventArgs e)
    {
        if (e.Url.Contains("obxodka.one/Payment/Success", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            await Task.Delay(5000);
            await _parent.DisplayAlertAsync("Успех", "Оплата прошла успешно! Ваш баланс пополнен.", "OK");
            ResetPaymentView();
            PaymentCompleted?.Invoke(this, EventArgs.Empty);
        }
        else if (e.Url.Contains("obxodka.one/Payment/Fail", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            await Task.Delay(5000);
            await _parent.DisplayAlertAsync("Ошибка", "Оплата отменена.", "OK");
            ResetPaymentView();
            PaymentCancelled?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnPaymentWebViewNavigated(object sender, WebNavigatedEventArgs e)
    {
        PaymentLoader.IsVisible = false;
        PaymentLoader.IsRunning = false;
    }
}
