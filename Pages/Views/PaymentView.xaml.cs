namespace obxodka.Views;

public sealed partial class PaymentView : ContentView
{
    private MainPage _parent = null!;
    private ApiService _apiService = null!;

    public const string ProductId300 = "obxodka_300_hours";
    public const string ProductId500 = "obxodka_500_hours";
    public const string ProductId1000 = "obxodka_1000_hours";

    public event EventHandler? PaymentCompleted;
    public event EventHandler? PaymentCancelled;

    public void NotifyPaymentCompleted() => PaymentCompleted?.Invoke(this, EventArgs.Empty);
    public void NotifyPaymentCancelled() => PaymentCancelled?.Invoke(this, EventArgs.Empty);

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

    public void ResetPaymentView()
    {
        CardsScrollView.IsVisible = true;
        CardsScrollView.Opacity = 1;
        CardsScrollView.TranslationX = 0;
    }

    private async void OnBuy300ClickedAsync(object? sender, EventArgs e) =>
        await PurchaseProductAsync(ProductId300, 300, Btn300Border, Btn300, Loader300);

    private async void OnBuy500ClickedAsync(object? sender, EventArgs e) =>
        await PurchaseProductAsync(ProductId500, 500, Btn500Border, Btn500, Loader500);

    private async void OnBuy1000ClickedAsync(object? sender, EventArgs e) =>
        await PurchaseProductAsync(ProductId1000, 1000, Btn1000Border, Btn1000, Loader1000);

    private async Task PurchaseProductAsync(
        string productId,
        decimal amount,
        Border border,
        Button button,
        ActivityIndicator indicator)
    {
        await UIAnimations.SetButtonLoadingAsync(border, button, indicator, true);

        try
        {
#if ANDROID
            var billing = CrossInAppBilling.Current;
            var connected = await billing.ConnectAsync();
            if (!connected)
            {
                await _parent.DisplayAlertAsync("Ошибка", "Не удалось подключиться к Google Play Billing. Убедитесь, что сервисы Google Play доступны и выполнен вход в аккаунт.", "OK");
                return;
            }

            try
            {
                InAppBillingPurchase? purchase = null;
                try
                {
                    purchase = await billing.PurchaseAsync(productId, ItemType.InAppPurchase);
                }
                catch (InAppBillingPurchaseException pEx) when (pEx.PurchaseError == PurchaseError.AlreadyOwned)
                {
                    var existing = await billing.GetPurchasesAsync(ItemType.InAppPurchase);
                    purchase = existing?.FirstOrDefault(p => p.ProductId == productId && p.State == PurchaseState.Purchased);
                }

                if (purchase is null)
                {
                    var existing = await billing.GetPurchasesAsync(ItemType.InAppPurchase);
                    purchase = existing?.FirstOrDefault(p => p.ProductId == productId && p.State == PurchaseState.Purchased);
                }

                if (purchase is { State: PurchaseState.Purchased })
                {
                    var token = purchase.PurchaseToken ?? purchase.Id;
                    var (success, error) = await _apiService.VerifyGooglePurchaseAsync(productId, token, purchase.Id);

                    if (success)
                    {
                        try
                        {
                            await billing.ConsumePurchaseAsync(purchase.ProductId, token);
                        }
                        catch { }

                        await _parent.DisplayAlertAsync("Успех", "Оплата прошла успешно! Ваш баланс пополнен.", "OK");
                        PaymentCompleted?.Invoke(this, EventArgs.Empty);
                    }
                    else
                    {
                        await _parent.DisplayAlertAsync("Внимание", ApiErrorHandler.ParseGeneralError(error, "Ошибка связи с сервером при зачислении часов. Убедитесь, что интернет активен, и нажмите кнопку еще раз."), "OK");
                    }
                }
                else if (purchase is { State: PurchaseState.PaymentPending })
                {
                    await _parent.DisplayAlertAsync("Ожидание", "Платеж находится в обработке Google Play.", "OK");
                }
            }
            catch (InAppBillingPurchaseException pEx)
            {
                if (pEx.PurchaseError != PurchaseError.UserCancelled)
                {
                    await _parent.DisplayAlertAsync("Ошибка", $"Ошибка покупки: {pEx.PurchaseError}", "OK");
                }
                else
                {
                    PaymentCancelled?.Invoke(this, EventArgs.Empty);
                }
            }
            finally
            {
                await billing.DisconnectAsync();
            }
#else
            var (success, payUrl, error) = await _apiService.GeneratePaymentLinkAsync(amount);
            if (success && !string.IsNullOrEmpty(payUrl))
            {
                await Launcher.OpenAsync(new Uri(payUrl));
            }
            else
            {
                await _parent.DisplayAlertAsync("Ошибка", ApiErrorHandler.ParseGeneralError(error, "Не удалось сгенерировать ссылку для оплаты."), "OK");
            }
#endif
        }
        catch (Exception ex)
        {
            await _parent.DisplayAlertAsync("Ошибка", ApiErrorHandler.ParseGeneralError(ex.Message, "Произошла непредвиденная ошибка при оплате."), "OK");
        }
        finally
        {
            await UIAnimations.SetButtonLoadingAsync(border, button, indicator, false);
        }
    }
}
