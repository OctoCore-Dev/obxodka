namespace obxodka.Pages.Store;

public sealed partial class PaymentView
{
    private async void OnBuy300ClickedAsync(object? sender, EventArgs e)
    {
        try
        {
            var amount = _selectedCurrency is "USD" or "EUR" ? 5m : 300m;
            await PurchaseProductAsync(TariffCalculator.GooglePlaySku300, amount, Btn300Border, Btn300, Loader300);
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[PAYMENT CLICK ERROR] {ex.Message}", ex);
        }
    }

    private async void OnBuy500ClickedAsync(object? sender, EventArgs e)
    {
        try
        {
            var amount = _selectedCurrency is "USD" or "EUR" ? 10m : 500m;
            await PurchaseProductAsync(TariffCalculator.GooglePlaySku500, amount, Btn500Border, Btn500, Loader500);
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[PAYMENT CLICK ERROR] {ex.Message}", ex);
        }
    }

    private async void OnBuy1000ClickedAsync(object? sender, EventArgs e)
    {
        try
        {
            var amount = _selectedCurrency is "USD" or "EUR" ? 20m : 1000m;
            await PurchaseProductAsync(TariffCalculator.GooglePlaySku1000, amount, Btn1000Border, Btn1000, Loader1000);
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[PAYMENT CLICK ERROR] {ex.Message}", ex);
        }
    }

    private async void OnBuyGoogle100ClickedAsync(object? sender, EventArgs e)
    {
        try
        {
            await PurchaseProductAsync(TariffCalculator.GooglePlaySku100, 99m, BtnGoogle100Border, BtnGoogle100, LoaderGoogle100);
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[PAYMENT CLICK ERROR] {ex.Message}", ex);
        }
    }

    private async void OnBuyGoogle250ClickedAsync(object? sender, EventArgs e)
    {
        try
        {
            await PurchaseProductAsync(TariffCalculator.GooglePlaySku250, 250m, BtnGoogle250Border, BtnGoogle250, LoaderGoogle250);
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[PAYMENT CLICK ERROR] {ex.Message}", ex);
        }
    }

    private async void OnBuyGoogle2000ClickedAsync(object? sender, EventArgs e)
    {
        try
        {
            await PurchaseProductAsync(TariffCalculator.GooglePlaySku2000, 2000m, BtnGoogle2000Border, BtnGoogle2000, LoaderGoogle2000);
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[PAYMENT CLICK ERROR] {ex.Message}", ex);
        }
    }

    private async void OnBuyGoogle5000ClickedAsync(object? sender, EventArgs e)
    {
        try
        {
            await PurchaseProductAsync(TariffCalculator.GooglePlaySku5000, 5000m, BtnGoogle5000Border, BtnGoogle5000, LoaderGoogle5000);
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[PAYMENT CLICK ERROR] {ex.Message}", ex);
        }
    }

    private async void OnBuyCustomLavaClickedAsync(object? sender, EventArgs e)
    {
        try
        {
            var amount = _selectedCustomAmount;
            if (int.TryParse(CustomAmountEntry.Text?.Trim(), out var parsed))
            {
                amount = parsed;
            }

            var minAmount = _selectedCurrency is "USD" or "EUR" ? 5 : 50;
            var maxAmount = _selectedCurrency is "USD" or "EUR" ? 100 : 10000;
            var symbol = _selectedCurrency switch
            {
                "USD" => "$",
                "EUR" => "€",
                _ => "₽"
            };

            if (amount < minAmount || amount > maxAmount)
            {
                var msg = _selectedCurrency is "USD" or "EUR"
                    ? $"Сумма пополнения должна быть от {symbol}{minAmount} до {symbol}{maxAmount}."
                    : $"Сумма пополнения должна быть от {minAmount} ₽ до {maxAmount:N0} ₽.";
                await _parent.DisplayAlertAsync("Внимание", msg, "OK");
                return;
            }

            await PayWithLavaAsync(amount, BtnCustomLavaBorder, BtnCustomLava, LoaderCustomLava);
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[CUSTOM PAYMENT ERROR] {ex.Message}", ex);
        }
    }

    private async Task PurchaseProductAsync(
        string productId,
        decimal amount,
        Border border,
        Button button,
        ActivityIndicator indicator)
    {
#if ANDROID
        if (_selectedCurrency != "RUB")
        {
            await PayWithLavaAsync(amount, border, button, indicator);
            return;
        }

        //блокировка повторного нажатия пока открыта шторка оплаты Google Play
        if (Interlocked.CompareExchange(ref _isGooglePurchaseInProgress, 1, 0) != 0)
        {
            return;
        }

        await UiAnimations.SetButtonLoadingAsync(border, button, indicator, true);

        try
        {
            await AndroidBillingRecoveryService.Gate.WaitAsync();
            try
            {
                var billing = CrossInAppBilling.Current;
                var connected = await billing.ConnectAsync();
                if (!connected)
                {
                    await _parent.DisplayAlertAsync("Ошибка",
                        "Не удалось подключиться к Google Play Billing. Убедитесь, что сервисы Google Play доступны и выполнен вход в аккаунт.",
                        "OK");
                    return;
                }

                try
                {
                    InAppBillingPurchase? purchase;
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

                    switch (purchase)
                    {
                        case { State: PurchaseState.Purchased }:
                            {
                                var token = purchase.PurchaseToken ?? purchase.Id;
                                var (success, error) = await _apiService.VerifyGooglePurchaseAsync(productId, token, purchase.Id);

                                if (success)
                                {
                                    //потребление только после подтверждения на сервере с проверкой результата и ретраями
                                    _ = await AndroidBillingRecoveryService.ConsumeWithRetryAsync(billing, purchase.ProductId, token);

                                    await _parent.DisplayAlertAsync(StoreMessages.PaymentSuccessTitle, StoreMessages.PaymentSuccessBody,
                                        AccountMessages.ButtonOk);
                                    PaymentCompleted?.Invoke(this, EventArgs.Empty);
                                }
                                else
                                {
                                    await _parent.DisplayAlertAsync(AccountMessages.AttentionTitle,
                                        ApiErrorHandler.ParseGeneralError(error, StoreMessages.PaymentVerifyError),
                                        AccountMessages.ButtonOk);
                                }

                                break;
                            }
                        case { State: PurchaseState.PaymentPending }:
                            await _parent.DisplayAlertAsync(StoreMessages.PaymentPendingTitle, StoreMessages.PaymentPendingBody,
                                AccountMessages.ButtonOk);
                            break;
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
            }
            finally
            {
                _ = AndroidBillingRecoveryService.Gate.Release();
            }
        }
        catch (Exception ex)
        {
            await _parent.DisplayAlertAsync("Ошибка",
                ApiErrorHandler.ParseGeneralError(ex.Message, "Произошла непредвиденная ошибка при оплате."), "OK");
        }
        finally
        {
            Volatile.Write(ref _isGooglePurchaseInProgress, 0);
            await UiAnimations.SetButtonLoadingAsync(border, button, indicator, false);
        }
#else
        _ = productId;
        await PayWithLavaAsync(amount, border, button, indicator);
#endif
    }

    private async Task PayWithLavaAsync(
        decimal amount,
        Border border,
        Button button,
        ActivityIndicator indicator)
    {
        await UiAnimations.SetButtonLoadingAsync(border, button, indicator, true);

        try
        {
            var baseline = _parent.RemainingSeconds;
            var (success, payUrl, error) = await _apiService.GeneratePaymentLinkAsync(amount, _selectedCurrency, _selectedPaymentMethod);
            if (success &&
                !string.IsNullOrEmpty(payUrl) &&
                Uri.TryCreate(payUrl, UriKind.Absolute, out var payUri) &&
                string.Equals(payUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                _baselineBalanceSeconds = baseline;
                PendingPaymentBanner.IsVisible = true;
                var methodLabel = GetSelectedMethodDisplayName();
                PendingPaymentStatusLabel.Text = _selectedCurrency switch
                {
                    "USD" => $"Счет на ${amount:0} ({methodLabel}) открыт в браузере. После оплаты баланс обновится автоматически.",
                    "EUR" => $"Счет на {amount:0} € ({methodLabel}) открыт в браузере. После оплаты баланс обновится автоматически.",
                    _ => $"Счет на {amount:0} ₽ ({methodLabel}) открыт в браузере. После оплаты баланс обновится автоматически."
                };
                StartPaymentPolling(baseline);
                _ = await Launcher.OpenAsync(payUri);
            }
            else
            {
                await _parent.DisplayAlertAsync("Ошибка",
                    ApiErrorHandler.ParseGeneralError(error, "Не удалось сгенерировать ссылку для оплаты Lava.top."), "OK");
            }
        }
        catch (Exception ex)
        {
            await _parent.DisplayAlertAsync("Ошибка",
                ApiErrorHandler.ParseGeneralError(ex.Message, "Произошла непредвиденная ошибка при открытии страницы оплаты."), "OK");
        }
        finally
        {
            await UiAnimations.SetButtonLoadingAsync(border, button, indicator, false);
        }
    }

    private void StartPaymentPolling(long baselineSeconds)
    {
        var gen = Interlocked.Increment(ref _pollingGeneration);

        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 75 && Volatile.Read(ref _pollingGeneration) == gen; attempt++)
            {
                await Task.Delay(4000);
                if (Volatile.Read(ref _pollingGeneration) != gen)
                {
                    return;
                }

                var credited = await CheckIfBalanceIncreasedAsync(baselineSeconds);
                if (credited)
                {
                    return;
                }
            }
        });
    }

    private async void OnCheckPaymentClickedAsync(object? sender, EventArgs e)
    {
        try
        {
            BtnCheckPayment.IsEnabled = false;
            BtnCheckPayment.Text = "Проверка...";
            try
            {
                var credited = await CheckIfBalanceIncreasedAsync(_baselineBalanceSeconds);
                if (!credited)
                {
                    await _parent.DisplayAlertAsync("Проверка платежа",
                        "Оплата еще обрабатывается платежным шлюзом. Как только платеж подтвердится, часы сразу появятся на балансе.",
                        "OK");
                }
            }
            finally
            {
                BtnCheckPayment.IsEnabled = true;
                BtnCheckPayment.Text = "Проверить";
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[CHECK PAYMENT ERROR] {ex.Message}", ex);
        }
    }

    private async Task<bool> CheckIfBalanceIncreasedAsync(long baselineSeconds)
    {
        if (ApiService.IsUnauthorizedPending || await AuthManager.GetAuthHeaderAsync() is null)
        {
            StopPaymentPolling();
            return false;
        }

        try
        {
            var (_, syncData, _) = await _apiService.SyncPendingPaymentsAsync();
            var (success, profile, _) = await _apiService.GetProfileAsync();
            if (success &&
                profile is not null &&
                (syncData is { Credited: true } || profile.BalanceSeconds >= baselineSeconds + 300))
            {
                StopPaymentPolling();
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    await _parent.TriggerBalanceSyncAsync();
                    await _parent.DisplayAlertAsync(StoreMessages.PaymentSuccessTitle, StoreMessages.PaymentSuccessHours,
                        AccountMessages.ButtonOk);
                    PaymentCompleted?.Invoke(this, EventArgs.Empty);
                });
                return true;
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning($"[PAYMENT POLL CHECK] {ex.Message}");
        }

        return false;
    }
#if ANDROID
    private Dictionary<string, InAppBillingProduct>? _googleProductCatalog;
    private int _isGooglePurchaseInProgress;

    private sealed record GoogleCatalogSlot(
        string ProductId,
        Button Button,
        Label? CardPriceLabel,
        View? RowContainer,
        bool IsBaseCard);
#endif

#if ANDROID
    //асинхронная загрузка локализованных цен и доступных вариантов из Google Play
    private async Task RefreshGooglePlayCatalogAsync()
    {
        var cached = _googleProductCatalog;
        if (cached is { Count: > 0 })
        {
            await MainThread.InvokeOnMainThreadAsync(() => ApplyGooglePlayCatalogUi(cached));
            return;
        }

        await AndroidBillingRecoveryService.Gate.WaitAsync();
        try
        {
            cached = _googleProductCatalog;
            if (cached is { Count: > 0 })
            {
                await MainThread.InvokeOnMainThreadAsync(() => ApplyGooglePlayCatalogUi(cached));
                return;
            }

            var billing = CrossInAppBilling.Current;
            if (!await billing.ConnectAsync())
            {
                return;
            }

            try
            {
                var products = await billing.GetProductInfoAsync(
                    ItemType.InAppPurchase,
                    TariffCalculator.GetGooglePlayProductIds());

                var map = new Dictionary<string, InAppBillingProduct>(StringComparer.OrdinalIgnoreCase);
                if (products is not null)
                {
                    foreach (var item in products.Where(static item => !string.IsNullOrWhiteSpace(item.ProductId)))
                    {
                        map[item.ProductId] = item;
                    }
                }

                if (map.Count > 0)
                {
                    _googleProductCatalog = map;
                    await MainThread.InvokeOnMainThreadAsync(() => ApplyGooglePlayCatalogUi(map));
                }
            }
            finally
            {
                await billing.DisconnectAsync();
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning($"[BILLING CATALOG] {ex.Message}");
        }
        finally
        {
            _ = AndroidBillingRecoveryService.Gate.Release();
        }
    }

    //применение цен Google Play к карточкам и скрытие еще не опубликованных в консоли вариантов
    private void ApplyGooglePlayCatalogUi(Dictionary<string, InAppBillingProduct> catalog)
    {
        GoogleCatalogSlot[] slots =
        [
            new(TariffCalculator.GooglePlaySku300, Btn300, Card1PriceLabel, null, true),
            new(TariffCalculator.GooglePlaySku500, Btn500, Card2PriceLabel, null, true),
            new(TariffCalculator.GooglePlaySku1000, Btn1000, Card3PriceLabel, null, true),
            new(TariffCalculator.GooglePlaySku100, BtnGoogle100, null, RowGoogle100, false),
            new(TariffCalculator.GooglePlaySku250, BtnGoogle250, null, RowGoogle250, false),
            new(TariffCalculator.GooglePlaySku2000, BtnGoogle2000, null, RowGoogle2000, false),
            new(TariffCalculator.GooglePlaySku5000, BtnGoogle5000, null, RowGoogle5000, false)
        ];

        var anyExtraVisible = false;
        foreach (var slot in slots)
        {
            if (catalog.TryGetValue(slot.ProductId, out var product) &&
                !string.IsNullOrWhiteSpace(product.LocalizedPrice))
            {
                slot.Button.Text = slot.IsBaseCard
                    ? $"Купить за {product.LocalizedPrice} (Google Play)"
                    : product.LocalizedPrice;

                slot.CardPriceLabel?.Text = product.LocalizedPrice;

                if (slot.RowContainer is not null)
                {
                    slot.RowContainer.IsVisible = true;
                    anyExtraVisible = true;
                }
            }
            else
            {
                slot.RowContainer?.IsVisible = false;
            }
        }

        CardGoogleExtra.IsVisible = anyExtraVisible;
    }
#endif
}
