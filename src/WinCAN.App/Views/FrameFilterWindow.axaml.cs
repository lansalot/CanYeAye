using Avalonia.Controls;

namespace WinCAN.App.Views;

/// <summary>Float-mode host for the frame filter; docking lives in the main window.</summary>
public partial class FrameFilterWindow : Window
{
    private FrameFilterView? _view;

    public FrameFilterWindow()
    {
        InitializeComponent();
    }

    public FrameFilterWindow(FrameFilterView view)
        : this()
    {
        Attach(view);
    }

    public void Attach(FrameFilterView view)
    {
        _view = view;
        Host.Content = view;
    }

    public void Detach()
    {
        if (ReferenceEquals(Host.Content, _view))
        {
            Host.Content = null;
        }

        _view = null;
    }
}
