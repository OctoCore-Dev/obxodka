namespace obxodka.Helpers;

public static partial class UiAnimations
{
    public enum EntranceType
    {
        SlideUp,
        FadeScale,
        SlideInLeft
    }

    public static readonly BindableProperty AnimateEntranceProperty =
        BindableProperty.CreateAttached(
            "AnimateEntrance",
            typeof(bool),
            typeof(UiAnimations),
            false,
            propertyChanged: OnAnimateEntranceChanged);

    public static readonly BindableProperty EntranceKindProperty =
        BindableProperty.CreateAttached(
            "EntranceKind",
            typeof(EntranceType),
            typeof(UiAnimations),
            EntranceType.SlideUp);

    public static readonly BindableProperty EntranceDelayProperty =
        BindableProperty.CreateAttached(
            "EntranceDelay",
            typeof(int),
            typeof(UiAnimations),
            0);

    public static readonly BindableProperty AutoStaggerProperty =
        BindableProperty.CreateAttached(
            "AutoStagger",
            typeof(bool),
            typeof(UiAnimations),
            false);

    public static readonly BindableProperty EntranceDurationProperty =
        BindableProperty.CreateAttached(
            "EntranceDuration",
            typeof(uint),
            typeof(UiAnimations),
            DurNormal);

    public static readonly BindableProperty EntranceFromYProperty =
        BindableProperty.CreateAttached(
            "EntranceFromY",
            typeof(double),
            typeof(UiAnimations),
            28.0);

    public static readonly BindableProperty EntranceFromXProperty =
        BindableProperty.CreateAttached(
            "EntranceFromX",
            typeof(double),
            typeof(UiAnimations),
            -36.0);

    public static readonly BindableProperty BounceOnClickProperty =
        BindableProperty.CreateAttached(
            "BounceOnClick",
            typeof(bool),
            typeof(UiAnimations),
            false,
            propertyChanged: OnBounceOnClickChanged);

    private static readonly BindableProperty s_isBounceGestureProperty =
        BindableProperty.CreateAttached(
            "IsBounceGesture",
            typeof(bool),
            typeof(UiAnimations),
            false);

    public static readonly BindableProperty SmoothHoverProperty =
        BindableProperty.CreateAttached(
            "SmoothHover",
            typeof(bool),
            typeof(UiAnimations),
            false,
            propertyChanged: OnSmoothHoverChanged);

    private static readonly BindableProperty s_isSmoothHoverAttachedProperty =
        BindableProperty.CreateAttached(
            "IsSmoothHoverAttached",
            typeof(bool),
            typeof(UiAnimations),
            false);

    public static bool GetAnimateEntrance(BindableObject view) => (bool)view.GetValue(AnimateEntranceProperty);
    public static void SetAnimateEntrance(BindableObject view, bool value) => view.SetValue(AnimateEntranceProperty, value);

    public static EntranceType GetEntranceKind(BindableObject view) => (EntranceType)view.GetValue(EntranceKindProperty);
    public static void SetEntranceKind(BindableObject view, EntranceType value) => view.SetValue(EntranceKindProperty, value);

    public static int GetEntranceDelay(BindableObject view) => (int)view.GetValue(EntranceDelayProperty);
    public static void SetEntranceDelay(BindableObject view, int value) => view.SetValue(EntranceDelayProperty, value);

    public static bool GetAutoStagger(BindableObject view) => (bool)view.GetValue(AutoStaggerProperty);
    public static void SetAutoStagger(BindableObject view, bool value) => view.SetValue(AutoStaggerProperty, value);

    public static uint GetEntranceDuration(BindableObject view) => (uint)view.GetValue(EntranceDurationProperty);
    public static void SetEntranceDuration(BindableObject view, uint value) => view.SetValue(EntranceDurationProperty, value);

    public static double GetEntranceFromY(BindableObject view) => (double)view.GetValue(EntranceFromYProperty);
    public static void SetEntranceFromY(BindableObject view, double value) => view.SetValue(EntranceFromYProperty, value);

    public static double GetEntranceFromX(BindableObject view) => (double)view.GetValue(EntranceFromXProperty);
    public static void SetEntranceFromX(BindableObject view, double value) => view.SetValue(EntranceFromXProperty, value);

    public static bool GetSmoothHover(BindableObject view) => (bool)view.GetValue(SmoothHoverProperty);
    public static void SetSmoothHover(BindableObject view, bool value) => view.SetValue(SmoothHoverProperty, value);

    public static bool GetBounceOnClick(BindableObject view) => (bool)view.GetValue(BounceOnClickProperty);
    public static void SetBounceOnClick(BindableObject view, bool value) => view.SetValue(BounceOnClickProperty, value);

    private static int GetSiblingIndex(VisualElement element) =>
        element.Parent is Layout layout ? layout.Children.IndexOf(element) : -1;

    private static void OnAnimateEntranceChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is not VisualElement element)
        {
            return;
        }

        if ((bool)newValue)
        {
            if (!element.IsLoaded)
            {
                element.Opacity = 0;
                var kind = GetEntranceKind(element);
                if (kind != EntranceType.SlideInLeft)
                {
                    if (kind != EntranceType.SlideUp)
                    {
                        if (kind == EntranceType.FadeScale)
                        {
                            element.Scale = 0.82;
                        }
                    }
                    else
                    {
                        element.TranslationY = GetEntranceFromY(element);
                        element.Scale = 0.97;
                    }
                }
                else
                {
                    element.TranslationX = GetEntranceFromX(element);
                }
            }

            element.Loaded -= OnElementLoadedForEntrance;
            element.Loaded += OnElementLoadedForEntrance;

            if (element.IsLoaded)
            {
                _ = CheckAndTriggerLoadedEntranceAsync(element);
            }
        }
        else
        {
            element.Loaded -= OnElementLoadedForEntrance;
            element.Opacity = 1.0;
            element.Scale = 1.0;
            element.TranslationX = 0;
            element.TranslationY = 0;
        }
    }

    private static void OnElementLoadedForEntrance(object? sender, EventArgs e)
    {
        if (sender is not VisualElement element)
        {
            return;
        }

        element.Loaded -= OnElementLoadedForEntrance;
        _ = SafeTriggerLoadedEntranceAsync(element);
    }

    private static async Task SafeTriggerLoadedEntranceAsync(VisualElement element)
    {
        try
        {
            await CheckAndTriggerLoadedEntranceAsync(element);
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning($"[UiAnimations] {ex.Message}");
        }
    }

    private static async Task CheckAndTriggerLoadedEntranceAsync(VisualElement element)
    {
        if (!IsElementVisibleInHierarchy(element))
        {
            element.Opacity = 1.0;
            element.Scale = 1.0;
            element.TranslationX = 0;
            element.TranslationY = 0;
            return;
        }

        await TriggerEntranceAsync(element);
    }

    public static async Task PlayCardsEntranceAsync(
        this VisualElement? container,
        int delayBetweenMs = 35,
        uint duration = DurNormal)
    {
        if (container is null)
        {
            return;
        }

        List<VisualElement> cards = [];
        CollectChildContainers(container, cards);

        if (cards.Count > 0)
        {
            await PlayEntranceCascadeAsync(delayBetweenMs, duration, [.. cards]);
        }
    }

    private static void CollectChildContainers(VisualElement node, List<VisualElement> result)
    {
        if (!node.IsVisible)
        {
            return;
        }

        if (node is not IContentView { Content: VisualElement cvContent })
        {
            if (node is not Layout layout)
            {
                return;
            }

            foreach (var child in layout.Children.OfType<VisualElement>())
            {
                CollectAnimatedElements(child, result);
            }
        }
        else
        {
            CollectAnimatedElements(cvContent, result);
        }
    }

    private static void CollectAnimatedElements(VisualElement element, List<VisualElement> result)
    {
        if (!element.IsVisible)
        {
            return;
        }

        if (GetAnimateEntrance(element))
        {
            result.Add(element);
            return;
        }

        CollectChildContainers(element, result);
    }

    private static bool IsElementVisibleInHierarchy(VisualElement element)
    {
        Element? current = element;
        while (current is VisualElement ve)
        {
            if (!ve.IsVisible)
            {
                return false;
            }

            current = current.Parent;
        }

        return true;
    }

    private static async Task TriggerEntranceAsync(VisualElement element)
    {
        var delay = GetEntranceDelay(element);
        if (delay <= 0 && GetAutoStagger(element))
        {
            var idx = GetSiblingIndex(element);
            if (idx > 0)
            {
                delay = Math.Min(idx * 50, 400);
            }
        }

        if (delay > 0)
        {
            await Task.Delay(delay);
        }

        var duration = GetEntranceDuration(element);
        var kind = GetEntranceKind(element);

        switch (kind)
        {
            case EntranceType.FadeScale:
                await PlayEntranceFadeScaleAsync(element, duration);
                break;
            case EntranceType.SlideInLeft:
                await PlayEntranceSlideInLeftAsync(element, GetEntranceFromX(element), duration);
                break;
            case EntranceType.SlideUp:
            default:
                await PlayEntranceSlideUpAsync(element, GetEntranceFromY(element), duration);
                break;
        }
    }

    private static void OnBounceOnClickChanged(BindableObject bindable, object oldValue, object newValue)
    {
        var enable = (bool)newValue;

        if (bindable is not Button button)
        {
            if (bindable is not View view)
            {
                return;
            }

            var existing = view.GestureRecognizers.OfType<TapGestureRecognizer>()
                .FirstOrDefault(t => (bool)t.GetValue(s_isBounceGestureProperty));

            if (enable)
            {
                if (existing is not null)
                {
                    return;
                }

                var tap = new TapGestureRecognizer();
                tap.SetValue(s_isBounceGestureProperty, true);
                tap.Tapped += async (_, _) => await view.BounceClickAsync();
                view.GestureRecognizers.Add(tap);
            }
            else if (existing is not null)
            {
                _ = view.GestureRecognizers.Remove(existing);
            }
        }
        else
        {
            button.Clicked -= OnButtonClickedBounce;
            if (enable)
            {
                button.Clicked += OnButtonClickedBounce;
            }
        }
    }

    private static void OnSmoothHoverChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is not View view)
        {
            return;
        }

        var enable = (bool)newValue;
        var existing = view.GestureRecognizers.OfType<PointerGestureRecognizer>()
            .FirstOrDefault(p => (bool)p.GetValue(s_isSmoothHoverAttachedProperty));

        if (enable)
        {
            if (existing is not null)
            {
                return;
            }

            var pointer = new PointerGestureRecognizer();
            pointer.SetValue(s_isSmoothHoverAttachedProperty, true);
            pointer.PointerEntered += (_, _) =>
            {
                view.CancelAnimations();
                _ = view.ScaleToAsync(1.018, 140, Easing.CubicOut);
            };
            pointer.PointerExited += (_, _) =>
            {
                view.CancelAnimations();
                _ = view.ScaleToAsync(1.0, 140, Easing.CubicOut);
            };
            view.GestureRecognizers.Add(pointer);
        }
        else if (existing is not null)
        {
            _ = view.GestureRecognizers.Remove(existing);
        }
    }

    private static void OnButtonClickedBounce(object? sender, EventArgs e)
    {
        if (sender is Button { IsEnabled: true } btn)
        {
            _ = SafeBounceClickAsync(btn);
        }
    }

    private static async Task SafeBounceClickAsync(Button btn)
    {
        try
        {
            await btn.BounceClickAsync();
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning($"[UiAnimations] {ex.Message}");
        }
    }
}
