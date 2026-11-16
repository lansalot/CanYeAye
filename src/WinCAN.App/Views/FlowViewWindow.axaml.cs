using Avalonia.Controls;
using Avalonia.Input;
using WinCAN.App.ViewModels;

namespace WinCAN.App.Views;

public partial class FlowViewWindow : Window
{
    private readonly FlowViewWindowViewModel? _viewModel;

    public FlowViewWindow()
    {
        InitializeComponent();
    }

    public FlowViewWindow(FlowViewWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Closed += (_, _) => _viewModel.Dispose();
    }

    private void OnBitClicked(object? sender, int bit) => _viewModel?.ToggleTrigger(bit);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_viewModel is null || TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox)
        {
            base.OnKeyDown(e);
            return;
        }

        switch (e.Key)
        {
            case Key.R:
                _viewModel.TogglePlayback();
                e.Handled = true;
                break;
            case Key.T:
                _viewModel.StepBackCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Y:
                _viewModel.StepForwardCommand.Execute(null);
                e.Handled = true;
                break;
        }

        base.OnKeyDown(e);
    }
}
