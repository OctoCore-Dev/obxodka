namespace obxodka.Pages.Store;

public sealed partial class PaymentView
{
    private ApiService _apiService = null!;
    private long _baselineBalanceSeconds;
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
        IView[] scrollCards = [PendingPaymentBanner, Card300, Card500, Card1000, CardGoogleExtra, CardConstructor, PaymentInfoCard];
        CardsScrollView.Scrolled += (_, _) =>
            CardStackScrollHelper.ApplyToChildren(
                CardsScrollView,
                scrollCards,
                65.0,
                0.92,
                2.5);
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

    public void SetHeaderTopInset(double top)
    {
        if (DeviceInfo.Idiom == DeviceIdiom.Phone && RootPaymentGrid is not null)
        {
            RootPaymentGrid.Padding = new Thickness(16, Math.Max(top + 16, 16), 16, 100);
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
