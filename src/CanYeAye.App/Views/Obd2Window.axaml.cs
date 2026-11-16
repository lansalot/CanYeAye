using Avalonia.Controls;
using CanYeAye.App.ViewModels;

namespace CanYeAye.App.Views;

public partial class Obd2Window : Window
{
    private readonly Obd2WindowViewModel? _viewModel;

    public Obd2Window()
    {
        InitializeComponent();
    }

    public Obd2Window(Obd2WindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Closed += (_, _) => _viewModel.Dispose();
    }
}
