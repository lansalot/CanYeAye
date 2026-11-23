using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CanYeAye.App.Controls;
using CanYeAye.App.Services;
using CanYeAye.App.ViewModels;
using CanYeAye.Core.Dbc;

namespace CanYeAye.App.Views;

/// <summary>
/// The signal editor (dbc/dbcsignaleditor): live edits on the shared DBC model, the 8x8 LSB0 bit
/// grid for picking the start bit, and the value table.
/// </summary>
public partial class SignalEditorWindow : Window
{
    private readonly SignalEditorViewModel _viewModel;
    private readonly WindowGeometry _geometry;
    private bool _loading;
    private bool _refreshQueued;

    public SignalEditorWindow()
    {
        InitializeComponent();
        _viewModel = null!;
        _geometry = null!;
    }

    public SignalEditorWindow(DbcFile file, DbcMessage message, DbcSignal signal, WindowGeometry? geometry = null)
    {
        InitializeComponent();

        _viewModel = new SignalEditorViewModel(file, message, signal);
        _geometry = geometry ?? new WindowGeometry();
        DataContext = _viewModel;
        _viewModel.Changed += OnSignalChanged;
        _geometry.Apply(this);

        Loaded += (_, _) => RefreshAll();
        Closed += (_, _) =>
        {
            _viewModel.Changed -= OnSignalChanged;
            _geometry.Capture(this);
        };
    }

    /// <summary>
    /// Deferred: a combo box raises SelectionChanged from inside its own selection model update,
    /// and rebuilding the item list synchronously there throws in Avalonia.
    /// </summary>
    private void OnSignalChanged()
    {
        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _refreshQueued = false;
            RefreshAll();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void RefreshAll()
    {
        _loading = true;
        try
        {
            var signal = _viewModel.Signal;

            NameBox.Text = signal.Name;
            BitLengthBox.Text = signal.SignalSize.ToString(CultureInfo.InvariantCulture);
            IntelCheck.IsChecked = signal.IntelByteOrder;
            TypeCombo.SelectedIndex = signal.ValueType switch
            {
                DbcSignalValueType.SignedInt => 1,
                DbcSignalValueType.SingleFloat => 2,
                DbcSignalValueType.DoubleFloat => 3,
                DbcSignalValueType.String => 4,
                _ => 0,
            };
            ScaleBox.Text = signal.Factor.ToString(CultureInfo.InvariantCulture);
            BiasBox.Text = signal.Bias.ToString(CultureInfo.InvariantCulture);
            MinBox.Text = signal.Min.ToString(CultureInfo.InvariantCulture);
            MaxBox.Text = signal.Max.ToString(CultureInfo.InvariantCulture);
            UnitBox.Text = signal.UnitName;
            CommentBox.Text = signal.Comment;

            FillReceiverCombo(signal);
            FillMultiplexParentCombo(signal);

            NoneRadio.IsChecked = !signal.IsMultiplexor && !signal.IsMultiplexed;
            MultiplexedRadio.IsChecked = signal.IsMultiplexed && !signal.IsMultiplexor;
            MultiplexorRadio.IsChecked = signal.IsMultiplexor && !signal.IsMultiplexed;
            ExtendedRadio.IsChecked = signal.IsMultiplexor && signal.IsMultiplexed;

            MultiplexValuesBox.Text = DbcEditor.FormatMultiplexValues(signal);
            MultiplexValuesBox.IsEnabled = signal.IsMultiplexed;
            MultiplexParentCombo.IsEnabled = signal.IsMultiplexed;

            BitGrid.SetState(_viewModel.Message, signal);
            BitSummary.Text =
                $"Start bit {signal.StartBit} ({(signal.IntelByteOrder ? "LSB first" : "MSB first")}), " +
                $"{signal.SignalSize} bit(s), {(signal.SignalSize + 7) / 8} byte(s)";
            ErrorText.Text = _viewModel.ErrorText;
        }
        finally
        {
            _loading = false;
        }
    }

    private void FillReceiverCombo(DbcSignal signal)
    {
        var names = _viewModel.Nodes.Select(node => node.Name).ToList();
        var existing = ReceiverCombo.Items.Cast<object?>().OfType<string>().ToList();
        if (!names.SequenceEqual(existing, StringComparer.Ordinal))
        {
            ReceiverCombo.Items.Clear();
            foreach (var name in names)
            {
                ReceiverCombo.Items.Add(name);
            }
        }

        var selected = signal.Receiver?.Name;
        if (selected is not null && ReceiverCombo.Items.Contains(selected))
        {
            ReceiverCombo.SelectedItem = selected;
        }
        else if (ReceiverCombo.ItemCount > 0)
        {
            ReceiverCombo.SelectedIndex = 0;
        }
    }

    private void FillMultiplexParentCombo(DbcSignal signal)
    {
        var names = new List<string> { "(none)" };
        names.AddRange(_viewModel.Message.Signals.Values
            .Where(candidate => candidate != signal && candidate.IsMultiplexor)
            .Select(candidate => candidate.Name));

        var existing = MultiplexParentCombo.Items.Cast<object?>().OfType<string>().ToList();
        if (!names.SequenceEqual(existing, StringComparer.Ordinal))
        {
            MultiplexParentCombo.Items.Clear();
            foreach (var name in names)
            {
                MultiplexParentCombo.Items.Add(name);
            }
        }

        var current = signal.MultiplexParent?.Name ?? "(none)";
        MultiplexParentCombo.SelectedItem = MultiplexParentCombo.Items.Contains(current) ? current : "(none)";
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
        RefreshAll();
    }

    private void OnBitLengthLostFocus(object? sender, RoutedEventArgs e) => CommitBitLength();

    private void OnBitLengthKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitBitLength();
            e.Handled = true;
        }
    }

    private void CommitBitLength()
    {
        if (_loading)
        {
            return;
        }

        _viewModel.CommitBitLength(BitLengthBox.Text ?? string.Empty);
        RefreshAll();
    }

    private void OnByteOrderChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _viewModel.CommitByteOrder(IntelCheck.IsChecked == true);
    }

    private void OnTypeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _viewModel.CommitValueType(TypeCombo.SelectedIndex);
        RefreshAll();
    }

    private void OnScaleLostFocus(object? sender, RoutedEventArgs e) => CommitScale();

    private void OnScaleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitScale();
            e.Handled = true;
        }
    }

    private void CommitScale()
    {
        if (_loading)
        {
            return;
        }

        _viewModel.CommitDouble(ScaleBox.Text ?? string.Empty, (signal, value) => signal.Factor = value);
        RefreshAll();
    }

    private void OnBiasLostFocus(object? sender, RoutedEventArgs e) => CommitBias();

    private void OnBiasKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitBias();
            e.Handled = true;
        }
    }

    private void CommitBias()
    {
        if (_loading)
        {
            return;
        }

        _viewModel.CommitDouble(BiasBox.Text ?? string.Empty, (signal, value) => signal.Bias = value);
        RefreshAll();
    }

    private void OnMinLostFocus(object? sender, RoutedEventArgs e) => CommitMin();

    private void OnMinKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitMin();
            e.Handled = true;
        }
    }

    private void CommitMin()
    {
        if (_loading)
        {
            return;
        }

        _viewModel.CommitDouble(MinBox.Text ?? string.Empty, (signal, value) => signal.Min = value);
        RefreshAll();
    }

    private void OnMaxLostFocus(object? sender, RoutedEventArgs e) => CommitMax();

    private void OnMaxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitMax();
            e.Handled = true;
        }
    }

    private void CommitMax()
    {
        if (_loading)
        {
            return;
        }

        _viewModel.CommitDouble(MaxBox.Text ?? string.Empty, (signal, value) => signal.Max = value);
        RefreshAll();
    }

    private void OnUnitLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _viewModel.CommitUnit(UnitBox.Text ?? string.Empty);
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

    private void OnReceiverChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _viewModel.CommitReceiver(ReceiverCombo.SelectedItem as string);
    }

    private void OnMultiplexModeChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string mode, IsChecked: true })
        {
            return;
        }

        var parsed = Enum.TryParse<DbcMultiplexMode>(mode, out var value) ? value : DbcMultiplexMode.None;
        _viewModel.CommitMultiplexMode(parsed);
        RefreshAll();
    }

    private void OnMultiplexValuesLostFocus(object? sender, RoutedEventArgs e) => CommitMultiplexValues();

    private void OnMultiplexValuesKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitMultiplexValues();
            e.Handled = true;
        }
    }

    private void CommitMultiplexValues()
    {
        if (_loading)
        {
            return;
        }

        _viewModel.CommitMultiplexValues(MultiplexValuesBox.Text ?? string.Empty);
        RefreshAll();
    }

    private void OnMultiplexParentChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        if (MultiplexParentCombo.SelectedItem is not string name || name == "(none)")
        {
            _viewModel.ClearMultiplexParent();
        }
        else
        {
            _viewModel.CommitMultiplexParent(name);
        }
    }

    private void OnBitClicked(object? sender, BitGridClickEventArgs e)
    {
        if (e.RightButton)
        {
            if (_viewModel.FindSignalAtBit(e.Bit) is { } owner && owner != _viewModel.Signal)
            {
                _viewModel.SetSignal(owner);
                RefreshAll();
            }

            return;
        }

        _viewModel.SetStartBit(e.Bit);
        RefreshAll();
    }

    private void OnValueCellEditEnded(object? sender, DataGridCellEditEndedEventArgs e)
    {
        _viewModel.SyncRows();
        ErrorText.Text = _viewModel.ErrorText;
    }

    private void OnDeleteValue(object? sender, RoutedEventArgs e)
    {
        if (ValueGrid.SelectedItem is DbcValueRow row)
        {
            _viewModel.DeleteRow(row);
        }
    }

    private void OnShowHex(object? sender, RoutedEventArgs e) => _viewModel.SetHexMode(true);

    private void OnShowDecimal(object? sender, RoutedEventArgs e) => _viewModel.SetHexMode(false);
}
