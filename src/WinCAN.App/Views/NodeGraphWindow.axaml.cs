using Avalonia.Controls;
using Avalonia.Interactivity;
using WinCAN.App.ViewModels;
using WinCAN.Core.Settings;

namespace WinCAN.App.Views;

public partial class NodeGraphWindow : Window
{
    private readonly NodeGraphViewModel? _viewModel;

    public NodeGraphWindow()
    {
        InitializeComponent();
    }

    public NodeGraphWindow(NodeGraphViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        GraphControl.NodeNameRequested += OnNodeNameRequested;
        Closed += (_, _) => _viewModel.Dispose();
    }

    public void ApplyLayout(IEnumerable<NodeGraphPosition> layout) =>
        GraphControl.ApplyLayout(layout.Select(position => (position.Address, position.X, position.Y)));

    public IReadOnlyList<NodeGraphPosition> CaptureLayout() =>
        GraphControl.CaptureLayout()
            .Select(entry => new NodeGraphPosition(entry.Address, entry.X, entry.Y))
            .ToList();

    public void ApplyView(double zoom, double panX, double panY) =>
        GraphControl.ApplyView(zoom, panX, panY);

    public (double Zoom, double PanX, double PanY) CaptureView() => GraphControl.CaptureView();

    private async void OnNodeNameRequested(object? sender, int address)
    {
        if (_viewModel is null)
        {
            return;
        }

        var dialog = new NodeNameDialog(address, _viewModel.GetNodeName(address));
        var name = await dialog.ShowDialog<string?>(this);
        if (name is not null)
        {
            _viewModel.SetNodeName(address, name);
        }
    }

    private void OnZoomInClick(object? sender, RoutedEventArgs e) => GraphControl.ZoomIn();

    private void OnZoomOutClick(object? sender, RoutedEventArgs e) => GraphControl.ZoomOut();

    private void OnRelayoutClick(object? sender, RoutedEventArgs e) => GraphControl.ResetView();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
