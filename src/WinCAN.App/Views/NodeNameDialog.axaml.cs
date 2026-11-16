using Avalonia.Controls;
using Avalonia.Interactivity;
using WinCAN.Core.Analysis;

namespace WinCAN.App.Views;

/// <summary>Small touch-friendly prompt used to name a J1939 node from the graph.</summary>
public partial class NodeNameDialog : Window
{
    public NodeNameDialog()
    {
        InitializeComponent();
    }

    public NodeNameDialog(int address, string? name)
        : this()
    {
        AddressText.Text = $"Name node {J1939NodeNames.FormatAddress(address)}";
        NameBox.Text = name ?? string.Empty;
        Opened += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void OnOkClick(object? sender, RoutedEventArgs e) =>
        Close(NameBox.Text?.Trim() ?? string.Empty);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
