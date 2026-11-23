using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CanYeAye.App.ViewModels;

namespace CanYeAye.App.Views;

public partial class DbcManagerWindow : Window
{
    private DbcEditorWindow? _editor;

    public DbcManagerWindow()
    {
        InitializeComponent();
    }

    public DbcManagerWindow(DbcManagerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnEditClick(object? sender, RoutedEventArgs e) => OpenEditor();

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e) => OpenEditor();

    private void OpenEditor()
    {
        if (DataContext is not DbcManagerViewModel viewModel || viewModel.SelectedFile is not { } entry)
        {
            return;
        }

        if (_editor is { IsVisible: true })
        {
            _editor.Activate();
            return;
        }

        _editor = new DbcEditorWindow(entry.File, viewModel.NotifyEdited, viewModel.Windows);
        _editor.Closed += (_, _) =>
        {
            _editor = null;
            viewModel.NotifyEdited();
        };
        _editor.Show(this);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
