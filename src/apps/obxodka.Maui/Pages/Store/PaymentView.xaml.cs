namespace obxodka.Pages.Store;

public sealed partial class PaymentView : IAdaptivePage
{
    private ApiService _apiService = null!;
    private long _baselineBalanceSeconds;
    private bool? _isThreeColumnPlans;
    private bool _isUpdatingAmountControls;
    private MainPage _parent = null!;
    private int _pollingGeneration;
    private string _selectedCurrency = "RUB";
    private int _selectedCustomAmount = 300;
    private string _selectedPaymentMethod = TariffCalculator.MethodSbp;

    public PaymentView()
    {
        InitializeComponent();
        SetCurrency("RUB");
    }

    public event EventHandler? PaymentCompleted;
    public event EventHandler? PaymentCancelled;

    public void NotifyPaymentCompleted() => PaymentCompleted?.Invoke(this, EventArgs.Empty);
    public void NotifyPaymentCancelled() => PaymentCancelled?.Invoke(this, EventArgs.Empty);

    public void OnThemeChanged() =>
        MainThread.BeginInvokeOnMainThread(UpdateCardOpacity);

    public void Initialize(MainPage parent, ApiService apiService)
    {
        _parent = parent;
        _apiService = apiService;
#if ANDROID
        _ = RefreshGooglePlayCatalogAsync();
#endif
    }

    public async Task PlayEntranceAnimationAsync()
    {
        Opacity = 1;
        TranslationY = 0;
        await this.PlayCardsEntranceAsync(40, 260);
    }

    public void LoadPaymentPage()
    {
        ResetPaymentView();
#if ANDROID
        _ = RefreshGooglePlayCatalogAsync();
#endif
    }

    //единая адаптация экрана пополнения по ssot-снимку геометрии окна без ручного widthrequest
    public void ApplyViewport(AdaptiveViewport viewport)
    {
        if (RootPaymentGrid is null || PaymentCardsStack is null)
        {
            return;
        }

        var isCompact = viewport.IsCompact;
        var baseSidePad = isCompact ? 16.0 : viewport.IsExpanded ? 28.0 : 20.0;
        var sidePad = viewport.ComputeHorizontalPadding(baseSidePad, 940.0);
        var topPad = DeviceInfo.Platform == DevicePlatform.Android || DeviceInfo.Platform == DevicePlatform.iOS
            ? Math.Max(viewport.SafeTop + 14.0, isCompact ? 42.0 : 24.0)
            : isCompact ? 16.0 : 20.0;
        var bottomPad = !viewport.UseSidebar
            ? Math.Max(110.0, viewport.SafeBottom + 96.0)
            : Math.Max(28.0, viewport.SafeBottom + 16.0);

        RootPaymentGrid.Padding = new Thickness(sidePad, topPad, sidePad, 0);
        PaymentCardsStack.HorizontalOptions = LayoutOptions.Fill;
        PaymentCardsStack.Padding = new Thickness(0, 0, 0, bottomPad);

        ApplyTariffGridColumns(viewport.IsExpanded || viewport.IsShortScreen && viewport.ContentWidth >= 660.0);
    }

    //переключение сетки карточек тарифов между 3 колонками и вертикальной стопкой по ширине
    private void ApplyTariffGridColumns(bool useThreeColumns)
    {
        if (TariffPlansGrid is null || _isThreeColumnPlans == useThreeColumns)
        {
            return;
        }

        _isThreeColumnPlans = useThreeColumns;
        TariffPlansGrid.ColumnDefinitions.Clear();
        TariffPlansGrid.RowDefinitions.Clear();

        if (useThreeColumns)
        {
            TariffPlansGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            TariffPlansGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            TariffPlansGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            TariffPlansGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            TariffPlansGrid.ColumnSpacing = 16;
            TariffPlansGrid.RowSpacing = 0;

            Grid.SetRow(Card300, 0);
            Grid.SetColumn(Card300, 0);
            Grid.SetRow(Card500, 0);
            Grid.SetColumn(Card500, 1);
            Grid.SetRow(Card1000, 0);
            Grid.SetColumn(Card1000, 2);
        }
        else
        {
            TariffPlansGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            TariffPlansGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            TariffPlansGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            TariffPlansGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            TariffPlansGrid.ColumnSpacing = 0;
            TariffPlansGrid.RowSpacing = 14;

            Grid.SetRow(Card300, 0);
            Grid.SetColumn(Card300, 0);
            Grid.SetRow(Card500, 1);
            Grid.SetColumn(Card500, 0);
            Grid.SetRow(Card1000, 2);
            Grid.SetColumn(Card1000, 0);
        }
    }

    public void UpdateCardOpacity()
    {
        var bgSurface = Application.Current?.Resources.TryGetValue("BgSurface", out var bg) == true && bg is Color bgColor
            ? bgColor
            : Color.FromArgb("#161622");
        var borderSubtle = Application.Current?.Resources.TryGetValue("BorderSubtle", out var bs) == true && bs is Color bsc
            ? bsc
            : Color.FromArgb("#282838");

        Card300.BackgroundColor = bgSurface;
        Card300.Stroke = borderSubtle;
        Card500.BackgroundColor = bgSurface;
        Card500.Stroke = borderSubtle;
        Card1000.BackgroundColor = bgSurface;
        Card1000.Stroke = borderSubtle;
        CardGoogleExtra.BackgroundColor = bgSurface;
        CardGoogleExtra.Stroke = borderSubtle;
        CardConstructor.BackgroundColor = bgSurface;
        CardConstructor.Stroke = borderSubtle;
    }

    internal void ResetPaymentView()
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
}
