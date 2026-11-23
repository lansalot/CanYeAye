using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using CanYeAye.App.Services;
using CanYeAye.App.ViewModels;
using CanYeAye.Core;
using CanYeAye.Core.Dbc;

namespace CanYeAye.App.Views;

/// <summary>The message editor (dbc/dbcmessageeditor).</summary>
public partial class MessageEditorWindow : Window
{
    private readonly MessageEditorViewModel _viewModel;
    private readonly WindowGeometry _geometry;
    private bool _loading;

    public MessageEditorWindow()
    {
        InitializeComponent();
        _viewModel = null!;
        _geometry = null!;
    }

    public MessageEditorWindow(DbcFile file, DbcMessage message, WindowGeometry? geometry = null)
    {
        InitializeComponent();

        _viewModel = new MessageEditorViewModel(file, message);
        _geometry = geometry ?? new WindowGeometry();
        DataContext = _viewModel;
        _viewModel.Changed += OnMessageChanged;
        _geometry.Apply(this);

        Loaded += (_, _) => RefreshAll();
        Closed += (_, _) =>
        {
            _viewModel.Changed -= OnMessageChanged;
            _geometry.Capture(this);
        };
    }

    private void OnMessageChanged() => RefreshAll();

    private void RefreshAll()
    {
        _loading = true;
        try
        {
            var message = _viewModel.Message;

            FrameIdBox.Text = "0x" + CanIdParser.FormatId(message.Id, message.IsExtended);
            ExtendedCheck.IsChecked = message.IsExtended;
            NameBox.Text = message.Name;
            LengthBox.Text = message.Length.ToString(CultureInfo.InvariantCulture);
            CanFdCheck.IsChecked = message.IsCanFd;
            BrsCheck.IsChecked = message.IsCanFd && message.BitRateSwitch;
            BrsCheck.IsEnabled = message.IsCanFd;

            TextColorPicker.Color = Parse(message.ForegroundColor, Colors.Black);
            BackgroundColorPicker.Color = Parse(message.BackgroundColor, Colors.White);

            SenderCombo.Items.Clear();
            foreach (var node in _viewModel.Nodes)
            {
                SenderCombo.Items.Add(node.Name);
            }

            SenderCombo.SelectedItem = message.Sender?.Name;
            SenderCombo.Text = message.Sender?.Name ?? string.Empty;
            CommentBox.Text = message.Comment;
            ErrorText.Text = _viewModel.ErrorText;

            _viewModel.RefreshSample();
        }
        finally
        {
            _loading = false;
        }
    }

    private static Color Parse(string? value, Color fallback) =>
        Color.TryParse(value, out var colour) ? colour : fallback;

    private void OnFrameIdLostFocus(object? sender, RoutedEventArgs e) => CommitFrameId();

    private void OnFrameIdKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitFrameId();
            e.Handled = true;
        }
    }

    private void CommitFrameId()
    {
        if (_loading)
        {
            return;
        }

        _viewModel.CommitId(FrameIdBox.Text ?? string.Empty);
        RefreshAll();
    }

    private void OnExtendedChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _viewModel.SetExtended(ExtendedCheck.IsChecked == true);
    }

    private void OnNameLostFocus(object? sender, RoutedEventArgs e) => CommitName();

    private void OnNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitName();
            e.Handled = true;
        }
    }

    private void CommitName()
    {
        if (_loading)
        {
            return;
        }

        _viewModel.CommitName(NameBox.Text ?? string.Empty);
    }

    private void OnLengthLostFocus(object? sender, RoutedEventArgs e) => CommitLength();

    private void OnLengthKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitLength();
            e.Handled = true;
        }
    }

    private void CommitLength()
    {
        if (_loading)
        {
            return;
        }

        _viewModel.CommitLength(LengthBox.Text ?? string.Empty);
    }

    private void OnCanFdChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _viewModel.SetCanFd(CanFdCheck.IsChecked == true);
        RefreshAll();
    }

    private void OnBrsChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _viewModel.SetBitRateSwitch(BrsCheck.IsChecked == true);
    }

    private void OnTextColorChanged(object? sender, ColorChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _viewModel.SetColor(foreground: true, e.NewColor);
    }

    private void OnBackgroundColorChanged(object? sender, ColorChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _viewModel.SetColor(foreground: false, e.NewColor);
    }

    private void OnSenderChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        if (SenderCombo.SelectedItem is string name)
        {
            _viewModel.CommitSender(name);
        }
    }

    private void OnSenderLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _viewModel.CommitSender(SenderCombo.Text);
        RefreshAll();
    }

    private void OnCommentLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _viewModel.CommitComment(CommentBox.Text ?? string.Empty);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
