using Avalonia.Controls;
using CanYeAye.App.ViewModels;

namespace CanYeAye.App.Views;

public partial class ProgressWindow : Window
{
    public ProgressWindow()
    {
        InitializeComponent();
    }

    public ProgressWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
