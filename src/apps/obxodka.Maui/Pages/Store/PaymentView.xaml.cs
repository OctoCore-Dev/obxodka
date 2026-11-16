#if ANDROID
using Plugin.InAppBilling;
#endif

namespace obxodka.Pages;

public sealed partial class PaymentView : ContentView
{
    private MainPage _parent = null!;
    private ApiService _apiService = null!;
    private int _pollingGeneration;
    private long _baselineBalanceSeconds;
    private bool _isUpdatingAmountControls;
    private int _selectedCustomAmount = 300;
    private string _selectedCurrency = "RUB";
    private string _selectedPaymentMethod = TariffCalculator.MethodSbp;

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
        SetCurrency("RUB");
        CardsScrollView.Scrolled += (s, e) =>
            CardStackScrollHelper.ApplyToChildren(
                CardsScrollView,
                [PendingPaymentBanner, Card300, Card500, Card1000, CardConstructor, PaymentInfoCard],
                zone: 65.0,
                minScale: 0.92,
                power: 2.5);
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

    private void OnCurrencyRubClicked(object? sender, EventArgs e) => SetCurrency("RUB");
    private void OnCurrencyUsdClicked(object? sender, EventArgs e) => SetCurrency("USD");
    private void OnCurrencyEurClicked(object? sender, EventArgs e) => SetCurrency("EUR");

    private void OnMethodFirstClicked(object? sender, EventArgs e) =>
        SetPaymentMethod(_selectedCurrency == "RUB" ? TariffCalculator.MethodSbp : TariffCalculator.MethodCard);

    private void OnMethodSecondClicked(object? sender, EventArgs e) =>
        SetPaymentMethod(_selectedCurrency == "RUB" ? TariffCalculator.MethodCard : TariffCalculator.MethodPayPal);

    private void SetCurrency(string currency)
    {
        _selectedCurrency = currency;
        _selectedPaymentMethod = currency == "RUB"
            ? (_selectedPaymentMethod is TariffCalculator.MethodSbp or TariffCalculator.MethodCard ? _selectedPaymentMethod : TariffCalculator.MethodSbp)
            : (_selectedPaymentMethod is TariffCalculator.MethodCard or TariffCalculator.MethodPayPal ? _selectedPaymentMethod : TariffCalculator.MethodCard);

        UpdateCurrencySwitcherUI();
        UpdateMethodSwitcherUI();
        UpdateCurrencyCardsUI();
    }

    private void SetPaymentMethod(string method)
    {
        _selectedPaymentMethod = method;
        UpdateMethodSwitcherUI();
        UpdateCurrencyCardsUI();
        UpdateCalculatedHoursUI(_selectedCustomAmount);
    }

    private void UpdateCurrencySwitcherUI()
    {
        StyleSelectorPill(BtnCurrencyRub, _selectedCurrency == "RUB");
        StyleSelectorPill(BtnCurrencyUsd, _selectedCurrency == "USD");
        StyleSelectorPill(BtnCurrencyEur, _selectedCurrency == "EUR");
    }

    private void UpdateMethodSwitcherUI()
    {
        if (_selectedCurrency == "RUB")
        {
            BtnMethodFirst.Text = "⚡ СБП (QR / банк)";
            BtnMethodSecond.Text = "💳 Карта РФ";
            StyleSelectorPill(BtnMethodFirst, _selectedPaymentMethod == TariffCalculator.MethodSbp);
            StyleSelectorPill(BtnMethodSecond, _selectedPaymentMethod == TariffCalculator.MethodCard);
        }
        else
        {
            BtnMethodFirst.Text = "💳 Карта СНГ / мира";
            BtnMethodSecond.Text = "🅿️ PayPal";
            StyleSelectorPill(BtnMethodFirst, _selectedPaymentMethod == TariffCalculator.MethodCard);
            StyleSelectorPill(BtnMethodSecond, _selectedPaymentMethod == TariffCalculator.MethodPayPal);
        }
    }

    private static void StyleSelectorPill(Button btn, bool isActive)
    {
        var bgSurface = (Application.Current?.Resources.TryGetValue("BgSurface", out var bg) == true && bg is Color bgCol)
            ? bgCol
            : Color.FromArgb("#222230");
        var accent = (Application.Current?.Resources.TryGetValue("Accent", out var ac) == true && ac is Color acCol)
            ? acCol
            : Color.FromArgb("#60cdff");
        var textSecondary = (Application.Current?.Resources.TryGetValue("TextSecondary", out var ts) == true && ts is Color tsCol)
            ? tsCol
            : Color.FromArgb("#9d9da8");
        var fontBold = (Application.Current?.Resources.TryGetValue("AppFontBold", out var fb) == true && fb is string fbName)
            ? fbName
            : "SegoeUI-Bold";
        var fontMedium = (Application.Current?.Resources.TryGetValue("AppFontMedium", out var fm) == true && fm is string fmName)
            ? fmName
            : "SegoeUI-SemiBold";

        btn.BackgroundColor = isActive ? bgSurface : Colors.Transparent;
        btn.BorderColor = isActive ? accent : Colors.Transparent;
        btn.BorderWidth = isActive ? 1 : 0;
        btn.TextColor = isActive ? accent : textSecondary;
        btn.FontFamily = isActive ? fontBold : fontMedium;
    }

    private string GetSelectedMethodDisplayName() => _selectedPaymentMethod switch
    {
        TariffCalculator.MethodSbp => "СБП",
        TariffCalculator.MethodPayPal => "PayPal",
        _ => _selectedCurrency == "RUB" ? "Карта РФ" : "Карта"
    };

    private void UpdateCurrencyCardsUI()
    {
        var isDesktop = DeviceInfo.Idiom == DeviceIdiom.Desktop;
        var methodLabel = GetSelectedMethodDisplayName();

        if (_selectedCurrency == "USD")
        {
            PaymentSubtitleLabel.Text = "Международная оплата для СНГ и мира: карты Visa/MC и PayPal";
            PaymentMethodsHintLabel.Text = "Способы оплаты: Карта СНГ / мира (Visa, Mastercard) • PayPal";

            Card1TitleLabel.Text = "СТАРТОВЫЙ (+50%)";
            Card1PriceLabel.Text = "$5";
            Card1HoursLabel.Text = "750 часов (+250ч бонус)";
            Card1SubtitleLabel.Text = "Карты СНГ / зарубежных банков или PayPal. Без ограничений.";
            Btn300.Text = $"Купить за $5 ({methodLabel})";

            Card2TitleLabel.Text = "ОПТИМАЛЬНЫЙ";
            Card2BadgeLabel.Text = "ХИТ +120%";
            Card2PriceLabel.Text = "$10";
            Card2HoursLabel.Text = "2200 часов (+1200ч бонус)";
            Card2SubtitleLabel.Text = "Самый популярный тариф для стриминга, YouTube 4K и игр.";
            Btn500.Text = $"Купить за $10 ({methodLabel})";

            Card3TitleLabel.Text = "МАКСИМУМ (VIP x2.35)";
            Card3PriceLabel.Text = "$20";
            Card3HoursLabel.Text = "4700 часов (+2700ч бонус)";
            Card3SubtitleLabel.Text = "Максимальный пакет для семьи и всех устройств (до 5 одновременно).";
            Btn1000.Text = $"Купить за $20 ({methodLabel})";

            ChipPreset99.Text = "$5 (750ч)";
            ChipPreset250.Text = "$7 (1050ч)";
            ChipPreset300.Text = "$10 (2200ч)";
            ChipPreset500.Text = "$15 (3300ч)";
            ChipPreset1000.Text = "$20 (4700ч)";
            ChipPreset2000.Text = "$50 (12 000ч)";
            ChipPreset5000.Text = "$100 (24 000ч)";

            CustomCurrencyUnitLabel.Text = "$";
            ConstructorSubtitleLabel.Text = "От $5 до $100. Поддержка карт СНГ/мира (Visa, MC) и PayPal";
            CustomAmountSlider.Minimum = 5;
            CustomAmountSlider.Maximum = 100;
            SetCustomAmount(10, updateEntry: true, updateSlider: true);

            PaymentInfoNoticeLabel.Text = "Поддерживаются PayPal и карты Visa/Mastercard банков Казахстана, Узбекистана, Армении, Грузии, ЕС и неподсанкционных банков Беларуси (должны быть включены международные платежи в USD). Карты Белкарт и подсанкционных банков могут отклоняться банком-эмитентом.";
            CardConstructor.IsVisible = true;
            PaymentInfoCard.IsVisible = true;
        }
        else if (_selectedCurrency == "EUR")
        {
            PaymentSubtitleLabel.Text = "Международная оплата для Европы, СНГ и мира: карты Visa/MC и PayPal";
            PaymentMethodsHintLabel.Text = "Способы оплаты: Карта Европы и СНГ (Visa, Mastercard) • PayPal";

            Card1TitleLabel.Text = "СТАРТОВЫЙ (+50%)";
            Card1PriceLabel.Text = "5 €";
            Card1HoursLabel.Text = "750 часов (+250ч бонус)";
            Card1SubtitleLabel.Text = "Европейская / зарубежная карта или PayPal. Без ограничений.";
            Btn300.Text = $"Купить за 5 € ({methodLabel})";

            Card2TitleLabel.Text = "ОПТИМАЛЬНЫЙ";
            Card2BadgeLabel.Text = "ХИТ +120%";
            Card2PriceLabel.Text = "10 €";
            Card2HoursLabel.Text = "2200 часов (+1200ч бонус)";
            Card2SubtitleLabel.Text = "Самый популярный тариф для стриминга, YouTube 4K и игр.";
            Btn500.Text = $"Купить за 10 € ({methodLabel})";

            Card3TitleLabel.Text = "МАКСИМУМ (VIP x2.35)";
            Card3PriceLabel.Text = "20 €";
            Card3HoursLabel.Text = "4700 часов (+2700ч бонус)";
            Card3SubtitleLabel.Text = "Максимальный пакет для семьи и всех устройств (до 5 одновременно).";
            Btn1000.Text = $"Купить за 20 € ({methodLabel})";

            ChipPreset99.Text = "5 € (750ч)";
            ChipPreset250.Text = "7 € (1050ч)";
            ChipPreset300.Text = "10 € (2200ч)";
            ChipPreset500.Text = "15 € (3300ч)";
            ChipPreset1000.Text = "20 € (4700ч)";
            ChipPreset2000.Text = "50 € (12 000ч)";
            ChipPreset5000.Text = "100 € (24 000ч)";

            CustomCurrencyUnitLabel.Text = "€";
            ConstructorSubtitleLabel.Text = "От 5 € до 100 €. Поддержка карт банков Европы и СНГ и PayPal";
            CustomAmountSlider.Minimum = 5;
            CustomAmountSlider.Maximum = 100;
            SetCustomAmount(10, updateEntry: true, updateSlider: true);

            PaymentInfoNoticeLabel.Text = "Поддерживаются PayPal и карты Visa/Mastercard банков Европы, Казахстана, Узбекистана, Армении, Грузии и неподсанкционных банков Беларуси (должны быть включены международные платежи в EUR). Карты Белкарт и подсанкционных банков могут отклоняться банком-эмитентом.";
            CardConstructor.IsVisible = true;
            PaymentInfoCard.IsVisible = true;
        }
        else
        {
#if ANDROID
            PaymentSubtitleLabel.Text = "Оплата через Google Play — часы не сгорают, когда VPN выключен";
            Btn300.Text = "Купить за 300 ₽ (Google Play)";
            Btn500.Text = "Купить за 500 ₽ (Google Play)";
            Btn1000.Text = "Купить за 1000 ₽ (Google Play)";
            CardConstructor.IsVisible = false;
            PaymentInfoCard.IsVisible = false;
#else
            PaymentSubtitleLabel.Text = "Оплата через СБП и банковские карты РФ — часы не сгорают, когда VPN выключен";
            Btn300.Text = $"Купить за 300 ₽ ({methodLabel})";
            Btn500.Text = $"Купить за 500 ₽ ({methodLabel})";
            Btn1000.Text = $"Купить за 1000 ₽ ({methodLabel})";
            CardConstructor.IsVisible = isDesktop;
            PaymentInfoCard.IsVisible = isDesktop;
#endif
            PaymentMethodsHintLabel.Text = "Способы оплаты: СБП (QR / банк РФ) • Карты банков РФ (Мир, Visa, MC)";

            Card1TitleLabel.Text = "СТАРТОВЫЙ";
            Card1PriceLabel.Text = "300 ₽";
            Card1HoursLabel.Text = "350 часов (+50ч бонус)";
            Card1SubtitleLabel.Text = "Отличный выбор для знакомства и повседневного использования.";

            Card2TitleLabel.Text = "ОПТИМАЛЬНЫЙ";
            Card2BadgeLabel.Text = "ХИТ +50%";
            Card2PriceLabel.Text = "500 ₽";
            Card2HoursLabel.Text = "750 часов (+250ч бонус)";
            Card2SubtitleLabel.Text = "Идеально на несколько месяцев активного стриминга и игр.";

            Card3TitleLabel.Text = "МАКСИМУМ (VIP x2.2)";
            Card3PriceLabel.Text = "1000 ₽";
            Card3HoursLabel.Text = "2200 часов (+1200ч бонус)";
            Card3SubtitleLabel.Text = "Максимальная выгода для всех ваших устройств (до 5 одновременно).";

            ChipPreset99.Text = "99 ₽ (100ч)";
            ChipPreset250.Text = "250 ₽ (260ч)";
            ChipPreset300.Text = "300 ₽ (350ч)";
            ChipPreset500.Text = "500 ₽ (750ч)";
            ChipPreset1000.Text = "1000 ₽ (2200ч)";
            ChipPreset2000.Text = "2000 ₽ (4700ч)";
            ChipPreset5000.Text = "5000 ₽ (12 000ч)";

            CustomCurrencyUnitLabel.Text = "₽";
            ConstructorSubtitleLabel.Text = "От 50 ₽ до 10 000 ₽. Бонус до +140% часов при пополнении от 300 ₽";
            CustomAmountSlider.Minimum = 50;
            CustomAmountSlider.Maximum = 10000;
            SetCustomAmount(300, updateEntry: true, updateSlider: true);

            PaymentInfoNoticeLabel.Text = "При оплате через СБП открывается прямая ссылка на приложение вашего банка РФ. При оплате картой РФ межбанковская проверка может занимать от 2 до 15 минут; время начисляется автоматически.";
        }
    }

    private (int TotalHours, int BaseHours, int BonusHours, string BadgeText) CalculateHours(int amount)
    {
        if (amount <= 0)
        {
            return (0, 0, 0, "0% БОНУС");
        }

        var tariff = TariffCalculator.Calculate(amount, _selectedCurrency);
        var badge = tariff.BonusPercent > 0
            ? $"+{tariff.BonusPercent}% БОНУС"
            : (tariff.BonusHours > 0 ? $"+{tariff.BonusHours}ч БОНУС" : "БЕЗ БОНУСА");

        return (tariff.TotalHours, tariff.BaseHours, tariff.BonusHours, badge);
    }

    private void SetCustomAmount(int amount, bool updateEntry, bool updateSlider)
    {
        var min = _selectedCurrency is "USD" or "EUR" ? 5 : 50;
        var max = _selectedCurrency is "USD" or "EUR" ? 100 : 10000;
        var clamped = Math.Clamp(amount, min, max);
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
            HighlightPresetChip(clamped);
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
        var methodLabel = GetSelectedMethodDisplayName();
        if (_selectedCurrency is "USD" or "EUR")
        {
            CalculatedHoursLabel.Text = bonus > 0
                ? $"Начислим: {total} часов ({baseHours}ч база + {bonus}ч бонус)"
                : $"Начислим: {total} часов (1 ед. = 100ч)";
            BtnCustomLava.Text = _selectedCurrency == "USD"
                ? $"Оплатить ${amount} ({methodLabel})"
                : $"Оплатить {amount} € ({methodLabel})";
        }
        else
        {
            CalculatedHoursLabel.Text = bonus > 0
                ? $"Начислим: {total} часов ({baseHours}ч база + {bonus}ч бонус)"
                : $"Начислим: {total} часов (1 ₽ = 1 час)";
            BtnCustomLava.Text = $"Оплатить {amount} ₽ ({methodLabel})";
        }
    }

    private void OnPreset99Clicked(object? sender, EventArgs e) =>
        SetCustomAmount(_selectedCurrency is "USD" or "EUR" ? 5 : 99, updateEntry: true, updateSlider: true);

    private void OnPreset250Clicked(object? sender, EventArgs e) =>
        SetCustomAmount(_selectedCurrency is "USD" or "EUR" ? 7 : 250, updateEntry: true, updateSlider: true);

    private void OnPreset300Clicked(object? sender, EventArgs e) =>
        SetCustomAmount(_selectedCurrency is "USD" or "EUR" ? 10 : 300, updateEntry: true, updateSlider: true);

    private void OnPreset500Clicked(object? sender, EventArgs e) =>
        SetCustomAmount(_selectedCurrency is "USD" or "EUR" ? 15 : 500, updateEntry: true, updateSlider: true);

    private void OnPreset1000Clicked(object? sender, EventArgs e) =>
        SetCustomAmount(_selectedCurrency is "USD" or "EUR" ? 20 : 1000, updateEntry: true, updateSlider: true);

    private void OnPreset2000Clicked(object? sender, EventArgs e) =>
        SetCustomAmount(_selectedCurrency is "USD" or "EUR" ? 50 : 2000, updateEntry: true, updateSlider: true);

    private void OnPreset5000Clicked(object? sender, EventArgs e) =>
        SetCustomAmount(_selectedCurrency is "USD" or "EUR" ? 100 : 5000, updateEntry: true, updateSlider: true);

    private void HighlightPresetChip(int amount)
    {
        var bgElevated = (Application.Current?.Resources.TryGetValue("BgElevated", out var bge) == true && bge is Color bgec)
            ? bgec
            : Color.FromArgb("#1e1e2d");
        var accentDim = (Application.Current?.Resources.TryGetValue("AccentDim", out var ad) == true && ad is Color adc)
            ? adc
            : Color.FromArgb("#2d2040");
        var borderSubtle = (Application.Current?.Resources.TryGetValue("BorderSubtle", out var bs) == true && bs is Color bsc)
            ? bsc
            : Color.FromArgb("#33ffffff");
        var accent = (Application.Current?.Resources.TryGetValue("Accent", out var ac) == true && ac is Color acc)
            ? acc
            : Color.FromArgb("#60cdff");

        void UpdateChip(Button chip, bool isSelected)
        {
            chip.BackgroundColor = isSelected ? accentDim : bgElevated;
            chip.BorderColor = isSelected ? accent : borderSubtle;
        }

        if (_selectedCurrency is "USD" or "EUR")
        {
            UpdateChip(ChipPreset99, amount == 5);
            UpdateChip(ChipPreset250, amount == 7);
            UpdateChip(ChipPreset300, amount == 10);
            UpdateChip(ChipPreset500, amount == 15);
            UpdateChip(ChipPreset1000, amount == 20);
            UpdateChip(ChipPreset2000, amount == 50);
            UpdateChip(ChipPreset5000, amount == 100);
        }
        else
        {
            UpdateChip(ChipPreset99, amount == 99);
            UpdateChip(ChipPreset250, amount is 249 or 250);
            UpdateChip(ChipPreset300, amount == 300);
            UpdateChip(ChipPreset500, amount == 500);
            UpdateChip(ChipPreset1000, amount == 1000);
            UpdateChip(ChipPreset2000, amount == 2000);
            UpdateChip(ChipPreset5000, amount == 5000);
        }
    }

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

        var val = e.NewValue;
        int rounded;
        if (_selectedCurrency is "USD" or "EUR")
        {
            var step = val switch
            {
                >= 50 => 10,
                >= 20 => 5,
                _ => 1
            };
            rounded = (int)(Math.Round(val / step) * step);
            rounded = Math.Clamp(rounded, 5, 100);
        }
        else
        {
            var step = val switch
            {
                >= 2000 => 100,
                >= 1000 => 50,
                _ => 10
            };
            rounded = (int)(Math.Round(val / step) * step);
            rounded = Math.Clamp(rounded, 50, 10000);
        }

        if (rounded == _selectedCustomAmount)
        {
            return;
        }

        SetCustomAmount(rounded, updateEntry: true, updateSlider: false);
    }

    private async void OnBuy300ClickedAsync(object? sender, EventArgs e)
    {
        var amount = _selectedCurrency is "USD" or "EUR" ? 5m : 300m;
        await PurchaseProductAsync(ProductId300, amount, Btn300Border, Btn300, Loader300);
    }

    private async void OnBuy500ClickedAsync(object? sender, EventArgs e)
    {
        var amount = _selectedCurrency is "USD" or "EUR" ? 10m : 500m;
        await PurchaseProductAsync(ProductId500, amount, Btn500Border, Btn500, Loader500);
    }

    private async void OnBuy1000ClickedAsync(object? sender, EventArgs e)
    {
        var amount = _selectedCurrency is "USD" or "EUR" ? 20m : 1000m;
        await PurchaseProductAsync(ProductId1000, amount, Btn1000Border, Btn1000, Loader1000);
    }

    private async void OnBuyCustomLavaClickedAsync(object? sender, EventArgs e)
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
                        catch (Exception consumeEx)
                        {
                            //потребление покупки в google play может завершиться позже
                            AppLogger.LogWarning($"[BILLING CONSUME WARNING] {consumeEx.Message}");
                        }

                        await _parent.DisplayAlertAsync(StoreMessages.PaymentSuccessTitle, StoreMessages.PaymentSuccessBody, AccountMessages.ButtonOk);
                        PaymentCompleted?.Invoke(this, EventArgs.Empty);
                    }
                    else
                    {
                        await _parent.DisplayAlertAsync(AccountMessages.AttentionTitle, ApiErrorHandler.ParseGeneralError(error, StoreMessages.PaymentVerifyError), AccountMessages.ButtonOk);
                    }
                }
                else if (purchase is { State: PurchaseState.PaymentPending })
                {
                    await _parent.DisplayAlertAsync(StoreMessages.PaymentPendingTitle, StoreMessages.PaymentPendingBody, AccountMessages.ButtonOk);
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
            var (success, payUrl, error) = await _apiService.GeneratePaymentLinkAsync(amount, _selectedCurrency, _selectedPaymentMethod);
            if (success && !string.IsNullOrEmpty(payUrl))
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
            _ = await _apiService.SyncPendingPaymentsAsync();
            var (success, profile, _) = await _apiService.GetProfileAsync();
            if (success && profile is not null && profile.BalanceSeconds > baselineSeconds)
            {
                StopPaymentPolling();
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    await _parent.TriggerBalanceSyncAsync();
                    await _parent.DisplayAlertAsync(StoreMessages.PaymentSuccessTitle, StoreMessages.PaymentSuccessHours, AccountMessages.ButtonOk);
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
}
