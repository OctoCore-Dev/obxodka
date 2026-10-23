namespace obxodka.Views;

public sealed partial class PaymentView : ContentView
{
    private MainPage _parent = null!;
    private ApiService _apiService = null!;
    private int _pollingGeneration;
    private long _baselineBalanceSeconds;
    private bool _isUpdatingAmountControls;
    private int _selectedCustomAmount = 300;

    public const string ProductId300 = "obxodka_300_hours";
    public const string ProductId500 = "obxodka_500_hours";
    public const string ProductId1000 = "obxodka_1000_hours";

    public event EventHandler? PaymentCompleted;
    public event EventHandler? PaymentCancelled;

    public void NotifyPaymentCompleted() => PaymentCompleted?.Invoke(this, EventArgs.Empty);
    public void NotifyPaymentCancelled() => PaymentCancelled?.Invoke(this, EventArgs.Empty);

    public PaymentView()
    {
        InitializeComponent();
#if ANDROID
        PaymentSubtitleLabel.Text = "Оплата через Google Play — часы не сгорают, когда VPN выключен";
        Btn300.Text = "Купить за 300 ₽ (Google Play)";
        Btn500.Text = "Купить за 500 ₽ (Google Play)";
        Btn1000.Text = "Купить за 1000 ₽ (Google Play)";
        CardConstructor.IsVisible = false;
        PaymentInfoNoticeLabel.Text = "Покупки в Google Play активируются автоматически. Часы привязываются к вашему аккаунту Obxodka и доступны на всех ваших устройствах.";
#else
        PaymentSubtitleLabel.Text = "Оплата через СБП и банковские карты (Lava.top) — часы не сгорают, когда VPN выключен";
        Btn300.Text = "Купить за 300 ₽ (СБП / Карта)";
        Btn500.Text = "Купить за 500 ₽ (СБП / Карта)";
        Btn1000.Text = "Купить за 1000 ₽ (СБП / Карта)";
        CardConstructor.IsVisible = true;
        PaymentInfoNoticeLabel.Text = "При оплате картой РФ или через СБП межбанковская проверка может занимать от 2 до 15 минут (статус «Покупка обрабатывается» в банке). Время начисляется на баланс автоматически сразу после подтверждения платежной системой.";
        UpdateCalculatedHoursUI(300);
#endif
    }

    public void Initialize(MainPage parent, ApiService apiService)
    {
        _parent = parent;
        _apiService = apiService;
    }

    public async Task PlayEntranceAnimationAsync()
    {
        Opacity = 1;
        TranslationY = 0;
        await this.PlayCardsEntranceAsync(40, 260);
    }

    public void LoadPaymentPage() => ResetPaymentView();

    public void SetHeaderTopInset(double top)
    {
        if (DeviceInfo.Idiom == DeviceIdiom.Phone && RootPaymentGrid != null)
        {
            RootPaymentGrid.Padding = new Thickness(16, Math.Max(top + 16, 16), 16, 100);
        }
    }

    public void UpdateCardOpacity()
    {
        var bgSurface = (Application.Current?.Resources.TryGetValue("BgSurface", out var bg) == true && bg is Color bgColor)
            ? bgColor
            : Color.FromArgb("#161622");
        var borderSubtle = (Application.Current?.Resources.TryGetValue("BorderSubtle", out var bs) == true && bs is Color bsc)
            ? bsc
            : Color.FromArgb("#282838");

        Card300.BackgroundColor = bgSurface;
        Card300.Stroke = borderSubtle;
        Card500.BackgroundColor = bgSurface;
        Card500.Stroke = borderSubtle;
        Card1000.BackgroundColor = bgSurface;
        Card1000.Stroke = borderSubtle;
        CardConstructor.BackgroundColor = bgSurface;
        CardConstructor.Stroke = borderSubtle;
    }

    public void OnThemeChanged() =>
        MainThread.BeginInvokeOnMainThread(UpdateCardOpacity);

    public void ResetPaymentView()
    {
        CardsScrollView.IsVisible = true;
        CardsScrollView.Opacity = 1;
        CardsScrollView.TranslationX = 0;
    }

    public void StopPaymentPolling()
    {
        _ = Interlocked.Increment(ref _pollingGeneration);
        MainThread.BeginInvokeOnMainThread(() => PendingPaymentBanner.IsVisible = false);
    }

    private static (int TotalHours, int BaseHours, int BonusHours, string BadgeText) CalculateHours(int amount)
    {
        if (amount <= 0)
        {
            return (0, 0, 0, "0% БОНУС");
        }

        var totalHours = amount switch
        {
            99 or 100 => 100,
            249 or 250 => 260,
            300 => 350,
            500 => 750,
            1000 => 2200,
            >= 1000 => (int)(amount * 2.2m),
            >= 500 => (int)(amount * 1.5m),
            >= 300 => (int)(amount * 1.17m),
            _ => amount
        };

        var bonusHours = Math.Max(0, totalHours - amount);
        var badge = amount switch
        {
            >= 1000 => "+120% БОНУС",
            >= 500 => "+50% БОНУС",
            >= 300 => "+17% БОНУС",
            249 or 250 => "+10ч БОНУС",
            99 => "+1ч БОНУС",
            _ => "БЕЗ БОНУСА"
        };

        return (totalHours, amount, bonusHours, badge);
    }

    private void SetCustomAmount(int amount, bool updateEntry, bool updateSlider)
    {
        var clamped = Math.Clamp(amount, 50, 10000);
        _selectedCustomAmount = clamped;

        _isUpdatingAmountControls = true;
        try
        {
            if (updateEntry)
            {
                var text = clamped.ToString(CultureInfo.InvariantCulture);
                if (CustomAmountEntry.Text != text)
                {
                    CustomAmountEntry.Text = text;
                }
            }

            if (updateSlider)
            {
                var sliderVal = Math.Clamp(clamped, (int)CustomAmountSlider.Minimum, (int)CustomAmountSlider.Maximum);
                if (Math.Abs(CustomAmountSlider.Value - sliderVal) > 0.01)
                {
                    CustomAmountSlider.Value = sliderVal;
                }
            }

            UpdateCalculatedHoursUI(clamped);
        }
        finally
        {
            _isUpdatingAmountControls = false;
        }
    }

    private void UpdateCalculatedHoursUI(int amount)
    {
        var (total, baseHours, bonus, badge) = CalculateHours(amount);
        ConstructorBonusBadge.Text = badge;
        CalculatedHoursLabel.Text = bonus > 0
            ? $"Начислим: {total} часов ({baseHours}ч база + {bonus}ч бонус)"
            : $"Начислим: {total} часов (1 ₽ = 1 час)";
        BtnCustomLava.Text = $"Оплатить {amount} ₽ (Lava.top)";
    }

    private void OnPreset99Clicked(object? sender, EventArgs e) => SetCustomAmount(99, updateEntry: true, updateSlider: true);
    private void OnPreset250Clicked(object? sender, EventArgs e) => SetCustomAmount(250, updateEntry: true, updateSlider: true);
    private void OnPreset300Clicked(object? sender, EventArgs e) => SetCustomAmount(300, updateEntry: true, updateSlider: true);
    private void OnPreset500Clicked(object? sender, EventArgs e) => SetCustomAmount(500, updateEntry: true, updateSlider: true);
    private void OnPreset1000Clicked(object? sender, EventArgs e) => SetCustomAmount(1000, updateEntry: true, updateSlider: true);

    private void OnCustomAmountTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_isUpdatingAmountControls)
        {
            return;
        }

        var raw = e.NewTextValue ?? string.Empty;
        var digits = new string([.. raw.Where(char.IsDigit)]);
        if (digits != raw)
        {
            _isUpdatingAmountControls = true;
            CustomAmountEntry.Text = digits;
            _isUpdatingAmountControls = false;
        }

        if (int.TryParse(digits, out var val) && val > 0)
        {
            SetCustomAmount(val, updateEntry: false, updateSlider: true);
        }
    }

    private void OnCustomAmountSliderChanged(object? sender, ValueChangedEventArgs e)
    {
        if (_isUpdatingAmountControls)
        {
            return;
        }

        var rounded = (int)(Math.Round(e.NewValue / 10.0) * 10);
        rounded = Math.Clamp(rounded, 50, 10000);
        if (rounded == _selectedCustomAmount)
        {
            return;
        }

        SetCustomAmount(rounded, updateEntry: true, updateSlider: false);
    }

    private async void OnBuy300ClickedAsync(object? sender, EventArgs e) =>
        await PurchaseProductAsync(ProductId300, 300, Btn300Border, Btn300, Loader300);

    private async void OnBuy500ClickedAsync(object? sender, EventArgs e) =>
        await PurchaseProductAsync(ProductId500, 500, Btn500Border, Btn500, Loader500);

    private async void OnBuy1000ClickedAsync(object? sender, EventArgs e) =>
        await PurchaseProductAsync(ProductId1000, 1000, Btn1000Border, Btn1000, Loader1000);

    private async void OnBuyCustomLavaClickedAsync(object? sender, EventArgs e)
    {
        var amount = _selectedCustomAmount;
        if (int.TryParse(CustomAmountEntry.Text?.Trim(), out var parsed))
        {
            amount = parsed;
        }

        if (amount is < 50 or > 10000)
        {
            await _parent.DisplayAlertAsync("Внимание", "Сумма пополнения должна быть от 50 ₽ до 10 000 ₽.", "OK");
            return;
        }

        await PayWithLavaAsync(amount, BtnCustomLavaBorder, BtnCustomLava, LoaderCustomLava);
    }

    private async Task PurchaseProductAsync(
        string productId,
        decimal amount,
        Border border,
        Button button,
        ActivityIndicator indicator)
    {
#if ANDROID
        await UIAnimations.SetButtonLoadingAsync(border, button, indicator, true);

        try
        {
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
        }
        catch (Exception ex)
        {
            await _parent.DisplayAlertAsync("Ошибка", ApiErrorHandler.ParseGeneralError(ex.Message, "Произошла непредвиденная ошибка при оплате."), "OK");
        }
        finally
        {
            await UIAnimations.SetButtonLoadingAsync(border, button, indicator, false);
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
        await UIAnimations.SetButtonLoadingAsync(border, button, indicator, true);

        try
        {
            var baseline = _parent.RemainingSeconds;
            var (success, payUrl, error) = await _apiService.GeneratePaymentLinkAsync(amount);
            if (success && !string.IsNullOrEmpty(payUrl))
            {
                _baselineBalanceSeconds = baseline;
                PendingPaymentBanner.IsVisible = true;
                PendingPaymentStatusLabel.Text = $"Счет на {amount:0} ₽ открыт в браузере. После оплаты баланс обновится автоматически.";
                StartPaymentPolling(baseline);
                _ = await Launcher.OpenAsync(new Uri(payUrl));
            }
            else
            {
                await _parent.DisplayAlertAsync("Ошибка", ApiErrorHandler.ParseGeneralError(error, "Не удалось сгенерировать ссылку для оплаты Lava.top."), "OK");
            }
        }
        catch (Exception ex)
        {
            await _parent.DisplayAlertAsync("Ошибка", ApiErrorHandler.ParseGeneralError(ex.Message, "Произошла непредвиденная ошибка при открытии страницы оплаты."), "OK");
        }
        finally
        {
            await UIAnimations.SetButtonLoadingAsync(border, button, indicator, false);
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
        BtnCheckPayment.IsEnabled = false;
        BtnCheckPayment.Text = "Проверка...";
        try
        {
            var credited = await CheckIfBalanceIncreasedAsync(_baselineBalanceSeconds);
            if (!credited)
            {
                await _parent.DisplayAlertAsync("Проверка платежа", "Оплата еще обрабатывается платежным шлюзом. Как только платеж подтвердится, часы сразу появятся на балансе.", "OK");
            }
        }
        finally
        {
            BtnCheckPayment.IsEnabled = true;
            BtnCheckPayment.Text = "Проверить";
        }
    }

    private async Task<bool> CheckIfBalanceIncreasedAsync(long baselineSeconds)
    {
        try
        {
            var (success, profile, _) = await _apiService.GetProfileAsync();
            if (success && profile is not null && profile.BalanceSeconds > baselineSeconds)
            {
                StopPaymentPolling();
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    await _parent.TriggerBalanceSyncAsync();
                    await _parent.DisplayAlertAsync("Успех", "Оплата прошла успешно! Часы зачислены на ваш баланс.", "OK");
                    PaymentCompleted?.Invoke(this, EventArgs.Empty);
                });
                return true;
            }
        }
        catch { }

        return false;
    }
}
