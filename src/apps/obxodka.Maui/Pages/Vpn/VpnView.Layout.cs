//using только для сплит тунеля хватит удалять и натыкатся потостоянно на одно и тоже 5ый раз, рома ты тупой, ии не поможет ты сам это сделал
using Microsoft.Maui.Controls.Shapes;
using Path = System.IO.Path;

namespace obxodka.Pages;

//адаптивная верстка, отступы под safe area и реакция на смену темы
public sealed partial class VpnView
{
    //актуализация прозрачности карточек под активную тему
    public void UpdateCardOpacity()
    {
        var bgSurface = GetAppColor("BgSurface", Color.FromArgb("#161622"));
        var bgElevated = GetAppColor("BgElevated", Color.FromArgb("#202030"));

        CardIp.BackgroundColor = bgSurface;
        Card2.BackgroundColor = bgSurface;
        Card5.BackgroundColor = bgSurface;
        Card6.BackgroundColor = bgSurface;
        Card1Wrapper.BackgroundColor = bgSurface;
        ServerSelectorButton.BackgroundColor = bgElevated;
    }

    private double _lastAllocatedW = -1;
    private double _lastAllocatedH = -1;
    private double _currentTopInset;

    //учет отступа статусбара для мобильных экранов
    public void SetHeaderTopInset(double top)
    {
        _currentTopInset = top;
        if (DeviceInfo.Platform == DevicePlatform.Android && DeviceInfo.Idiom == DeviceIdiom.Phone)
        {
            RootLayoutGrid?.Padding = new Thickness(8, 0, 8, 0);

            if (ContentGrid != null)
            {
                var topAir = Math.Max(top + 16, 50);
                ContentGrid.Padding = new Thickness(0, topAir, 0, 96);
            }
        }
        else if (DeviceInfo.Idiom == DeviceIdiom.Phone && RootLayoutGrid != null)
        {
            RootLayoutGrid.Padding = new Thickness(8, Math.Max(top + 4, 8), 8, 0);
        }

        if (_lastAllocatedW > 0 && _lastAllocatedH > 0)
        {
            ApplyDynamicLayout(_lastAllocatedW, _lastAllocatedH);
        }
    }

    //реакция на изменение размеров контейнера экрана
    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (Math.Abs(_lastAllocatedW - width) > 1 || Math.Abs(_lastAllocatedH - height) > 1)
        {
            _lastAllocatedW = width;
            _lastAllocatedH = height;
            ApplyDynamicLayout(width, height);
        }
    }

    //адаптивная перестройка сетки под мобильные и широкие экраны
    private void ApplyDynamicLayout(double width, double height)
    {
        var availWidth = width - (RootLayoutGrid?.Padding.HorizontalThickness ?? 0);
        if (availWidth > 0)
        {
            ApplyCardWidth(availWidth);
        }

        var isDesktopOrTablet = DeviceInfo.Idiom == DeviceIdiom.Desktop || DeviceInfo.Idiom == DeviceIdiom.Tablet;
        var isWide = AdaptiveLayoutHelper.IsWideLayout(width, isDesktopOrTablet);

        if (ContentGrid is not null)
        {
            if (isWide)
            {
                if (ContentGrid.ColumnDefinitions.Count != 2)
                {
                    ContentGrid.ColumnDefinitions.Clear();
                    ContentGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(5, GridUnitType.Star)));
                    ContentGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(4, GridUnitType.Star)));
                    ContentGrid.RowDefinitions.Clear();
                    ContentGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
                    ContentGrid.ColumnSpacing = 28;
                    ContentGrid.RowSpacing = 0;
                    Grid.SetColumn(Card1Wrapper, 0);
                    Grid.SetRow(Card1Wrapper, 0);
                    Grid.SetColumn(TopCardsGrid, 1);
                    Grid.SetRow(TopCardsGrid, 0);
                    TopCardsGrid.RowDefinitions.Clear();
                    TopCardsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                    TopCardsGrid.RowDefinitions.Add(new RowDefinition(new GridLength(2, GridUnitType.Star)));
                    TopCardsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
                }

                Card1Wrapper.MinimumHeightRequest = -1;
                Card1ContentGrid.Padding = new Thickness(28, 32);
                GraphContainer.IsVisible = true;
                GraphContainer.HeightRequest = -1;
                Card5.HeightRequest = -1;
                Card6.HeightRequest = -1;
                ButtonContainerGrid.WidthRequest = 280;
                ButtonContainerGrid.HeightRequest = 280;
                ButtonOuterRing.WidthRequest = 260;
                ButtonOuterRing.HeightRequest = 260;
                ButtonOuterRing.StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(130) };
                ButtonMiddleRing.WidthRequest = 224;
                ButtonMiddleRing.HeightRequest = 224;
                ButtonMiddleRing.StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(112) };
                ConnectButtonCore.WidthRequest = 180;
                ConnectButtonCore.HeightRequest = 180;
                ConnectButtonCore.StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(90) };
                ConnectIcon.IconSize = 44;
                ConnectButtonText.FontSize = 15;
                TrafficGraphCanvas.InvalidateSurface();
                return;
            }
            else
            {
                if (ContentGrid.ColumnDefinitions.Count != 1)
                {
                    ContentGrid.ColumnDefinitions.Clear();
                    ContentGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                    ContentGrid.RowDefinitions.Clear();
                    ContentGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                    ContentGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
                    ContentGrid.ColumnSpacing = 0;
                    ContentGrid.RowSpacing = 10;
                    Grid.SetColumn(TopCardsGrid, 0);
                    Grid.SetRow(TopCardsGrid, 0);
                    Grid.SetColumn(Card1Wrapper, 0);
                    Grid.SetRow(Card1Wrapper, 1);
                    TopCardsGrid.RowDefinitions.Clear();
                    TopCardsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                    TopCardsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                    TopCardsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                }
            }
        }

        var (buttonSize, graphHeight, _) = AdaptiveLayoutHelper.CalculateVpnViewDimensions(
            width,
            height,
            _currentTopInset,
            isDesktopOrTablet);

        var isShortScreen = AdaptiveLayoutHelper.IsShortScreen(height);
        if (isShortScreen)
        {
            Card1Wrapper.MinimumHeightRequest = -1;
            Card1ContentGrid.Padding = new Thickness(12, 6, 12, 16);
            TopCardsGrid.RowSpacing = 6;
            if (ContentGrid is { } cgShort)
            {
                cgShort.RowSpacing = 6;
            }
            Card5.HeightRequest = 48;
            Card6.HeightRequest = 48;
            GraphContainer.IsVisible = false;
            GraphContainer.HeightRequest = 0;
        }
        else
        {
            Card1ContentGrid.Padding = new Thickness(16, 14, 16, 26);
            TopCardsGrid.RowSpacing = 10;
            if (ContentGrid is { } cgNormal)
            {
                cgNormal.RowSpacing = 10;
            }
            Card5.HeightRequest = 64;
            Card6.HeightRequest = 64;
            GraphContainer.IsVisible = true;
            GraphContainer.HeightRequest = graphHeight;

            var topCardsH = TopCardsGrid.Height > 0 ? TopCardsGrid.Height : (48.0 + 48.0 + 64.0 + graphHeight + 30.0);
            var estimatedAvailableForCard1 = height - _currentTopInset - 100.0 - topCardsH;
            var targetCard1MinHeight = Math.Clamp(Math.Round(estimatedAvailableForCard1), 340.0, 540.0);
            Card1Wrapper.MinimumHeightRequest = targetCard1MinHeight;
        }

        ButtonContainerGrid.WidthRequest = buttonSize;
        ButtonContainerGrid.HeightRequest = buttonSize;

        var outerRingSize = Math.Round(buttonSize * 0.935);
        ButtonOuterRing.WidthRequest = outerRingSize;
        ButtonOuterRing.HeightRequest = outerRingSize;
        ButtonOuterRing.StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(outerRingSize / 2.0) };

        var middleRingSize = Math.Round(buttonSize * 0.828);
        ButtonMiddleRing.WidthRequest = middleRingSize;
        ButtonMiddleRing.HeightRequest = middleRingSize;
        ButtonMiddleRing.StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(middleRingSize / 2.0) };

        var coreSize = Math.Round(buttonSize * 0.707);
        ConnectButtonCore.WidthRequest = coreSize;
        ConnectButtonCore.HeightRequest = coreSize;
        ConnectButtonCore.StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(coreSize / 2.0) };

        ConnectIcon.IconSize = (int)Math.Max(22, Math.Round(buttonSize * 0.165));
        ConnectButtonText.FontSize = Math.Max(10, Math.Round(buttonSize * 0.058));

        GraphContainer.HeightRequest = graphHeight;
        TrafficGraphCanvas.InvalidateSurface();
    }

    //перерисовка канвасов и обновление стилей при смене темы
    public void OnThemeChanged()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            LoaderCanvas?.InvalidateSurface();
            OuterAura?.InvalidateSurface();
            TrafficGraphCanvas?.InvalidateSurface();
            UpdateRayIndicator();

            UpdateCardOpacity();
            PopulateServerList();

            var borderSubtle = GetAppColor("BorderSubtle", Color.FromArgb("#282838"));
            var borderMedium = GetAppColor("BorderMedium", Color.FromArgb("#363650"));

            CardIp.Stroke = borderSubtle;
            Card2.Stroke = borderSubtle;
            Card5.Stroke = borderSubtle;
            Card6.Stroke = borderSubtle;
            Card1Wrapper.Stroke = borderSubtle;
            ServerSelectorButton.Stroke = borderMedium;

            var currentState = _vpnService?.CurrentState ?? AppVpnState.Disconnected;
            if (currentState == AppVpnState.Disconnected)
            {
                StatusLabel.Text = GetStatusTextForState(AppVpnState.Disconnected, VpnStatusMessages.Disconnected);
                ConnectButtonText.Text = GetButtonTextForState(AppVpnState.Disconnected, VpnStatusMessages.StartAction);
            }
            else if (currentState == AppVpnState.Connected)
            {
                StatusLabel.Text = GetStatusTextForState(AppVpnState.Connected, VpnStatusMessages.Protected);
                ConnectButtonText.Text = GetButtonTextForState(AppVpnState.Connected, VpnStatusMessages.StopAction);
            }

            var hasCustom = !string.IsNullOrEmpty(_buttonImageIdlePath) ||
                            !string.IsNullOrEmpty(_buttonImageConnectingPath) ||
                            !string.IsNullOrEmpty(_buttonImageActivePath) ||
                            !string.IsNullOrEmpty(_buttonImageErrorPath) ||
                            _buttonAnimatedCurrent != null;

            if (hasCustom)
            {
                ConnectButtonCore.BackgroundColor = Colors.Transparent;
                ConnectButtonCore.Stroke = Colors.Transparent;
                return;
            }

            if (currentState == AppVpnState.Disconnected)
            {
                _ = UIAnimations.SetVpnDisconnectedAsync(ConnectIcon, StatusLabel, OuterAura);
                ConnectButtonCore.ClearValue(BackgroundColorProperty);
                ConnectButtonCore.ClearValue(Border.StrokeProperty);
                ConnectButtonCore.SetDynamicResource(BackgroundColorProperty, "BgElevated");
                ConnectButtonCore.SetDynamicResource(Border.StrokeProperty, "BorderMedium");
            }
            else if (currentState == AppVpnState.Connected)
            {
                _ = UIAnimations.SetVpnConnectedAsync(ConnectIcon, StatusLabel, OuterAura);
                var accentColor = GetAppColor("Accent", s_cyanAccent);
                var activeOv = ThemeManager.Instance?.ActiveTheme?.Vfx?.ActiveOverrides;
                if (!string.IsNullOrWhiteSpace(activeOv?.AccentColor) && Color.TryParse(activeOv.AccentColor, out var ovColor))
                {
                    accentColor = ovColor;
                    StatusLabel.TextColor = ovColor;
                    ConnectIcon.IconColor = ovColor;
                }
                ConnectButtonCore.BackgroundColor = accentColor.WithAlpha(0.12f);
                ConnectButtonCore.Stroke = accentColor;
            }
        });
    }

    //получение цвета из ресурсов приложения с фоллбэком
    private static Color GetAppColor(string key, Color fallback)
    {
        try
        {
            if (Application.Current?.Resources != null &&
                Application.Current.Resources.TryGetValue(key, out var val) &&
                val is Color color)
            {
                return color;
            }
        }
        catch { }
        return fallback;
    }
}
