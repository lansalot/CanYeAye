using Avalonia.Controls;
using CanYeAye.App.ViewModels;

namespace CanYeAye.App.Views;

public partial class ScriptingWindow : Window
{
    private readonly ScriptingWindowViewModel? _viewModel;

    public ScriptingWindow()
    {
        InitializeComponent();
    }

    public ScriptingWindow(ScriptingWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Closed += (_, _) => _viewModel.Dispose();
    }
}
