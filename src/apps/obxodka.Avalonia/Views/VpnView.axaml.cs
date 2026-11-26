using obxodka.Avalonia.ViewModels;

namespace obxodka.Avalonia.Views;

public sealed partial class VpnView : UserControl
{
    private MainViewModel? _vm;

    public VpnView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _vm?.TrafficSampleReceived -= OnTrafficSample;
        _vm = DataContext as MainViewModel;
        _vm?.TrafficSampleReceived += OnTrafficSample;
    }

    private void OnTrafficSample(long dl, long ul) => TrafficGraph.PushSample(dl, ul);
}
