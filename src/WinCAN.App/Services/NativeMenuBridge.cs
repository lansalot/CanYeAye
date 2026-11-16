using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace WinCAN.App.Services;

/// <summary>
/// Platform glue for menus. On macOS the window's in-window <see cref="Menu"/> is mirrored into
/// the global menu bar (<see cref="NativeMenu"/>) and hidden; on Windows/Linux nothing changes.
/// The XAML menu stays the single source of truth: native items forward clicks to the original
/// <see cref="MenuItem"/> (so Command bindings and Click handlers run unchanged) and track its
/// header, checked, enabled and gesture state.
/// </summary>
internal static class NativeMenuBridge
{
    /// <summary>Cmd on macOS, Ctrl elsewhere.</summary>
    public static KeyModifiers CommandModifier =>
        OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    /// <summary>
    /// Assigns a platform shortcut (Cmd/Ctrl + <paramref name="key"/>). The gesture is shown in
    /// the menu everywhere; on macOS the native menu handles the key, elsewhere a hot key does.
    /// </summary>
    public static void SetShortcut(MenuItem item, Key key, KeyModifiers extra = KeyModifiers.None)
    {
        var gesture = new KeyGesture(key, CommandModifier | extra);
        item.InputGesture = gesture;
        if (!OperatingSystem.IsMacOS())
        {
            item.HotKey = gesture;
        }
    }

    /// <summary>
    /// Call after code-built submenus have been filled; later rebuilds are tracked.
    /// On macOS, exports <paramref name="menu"/> to the global menu bar for
    /// <paramref name="window"/> and hides the in-window copy. Items in <paramref name="skip"/>
    /// (e.g. Exit, which macOS provides as Quit in the application menu) are left out.
    /// </summary>
    public static void Attach(Window window, Menu menu, params MenuItem[] skip)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var root = new NativeMenu();
        foreach (var item in menu.Items)
        {
            if (item is MenuItem menuItem && !skip.Contains(menuItem))
            {
                root.Items.Add(Mirror(menuItem, skip));
            }
        }

        NativeMenu.SetMenu(window, root);
        menu.IsVisible = false;
    }

    private static NativeMenuItem Mirror(MenuItem source, MenuItem[] skip)
    {
        var native = new NativeMenuItem
        {
            Header = CleanHeader(source.Header),
            ToggleType = source.ToggleType,
            IsChecked = source.IsChecked,
            IsEnabled = source.IsEffectivelyEnabled,
            Gesture = source.InputGesture,
        };

        source.PropertyChanged += (_, e) =>
        {
            if (e.Property == MenuItem.HeaderProperty)
            {
                native.Header = CleanHeader(source.Header);
            }
            else if (e.Property == MenuItem.IsCheckedProperty)
            {
                native.IsChecked = source.IsChecked;
            }
            else if (e.Property == InputElement.IsEffectivelyEnabledProperty)
            {
                native.IsEnabled = source.IsEffectivelyEnabled;
            }
            else if (e.Property == MenuItem.InputGestureProperty)
            {
                native.Gesture = source.InputGesture;
            }
        };

        if (source.Items.Count > 0)
        {
            var submenu = new NativeMenu();
            native.Menu = submenu;
            Populate(submenu, source, skip);
            ((INotifyCollectionChanged)source.Items).CollectionChanged += (_, _) => Populate(submenu, source, skip);
        }
        else
        {
            native.Click += (_, _) => Activate(source);
        }

        return native;
    }

    private static void Populate(NativeMenu target, MenuItem source, MenuItem[] skip)
    {
        target.Items.Clear();
        foreach (var child in source.Items)
        {
            switch (child)
            {
                case Separator:
                    if (target.Items.Count > 0 && target.Items[^1] is not NativeMenuItemSeparator)
                    {
                        target.Items.Add(new NativeMenuItemSeparator());
                    }

                    break;
                case MenuItem item when !skip.Contains(item):
                    target.Items.Add(Mirror(item, skip));
                    break;
            }
        }

        while (target.Items.Count > 0 && target.Items[^1] is NativeMenuItemSeparator)
        {
            target.Items.RemoveAt(target.Items.Count - 1);
        }
    }

    // Same order as Avalonia's DefaultMenuInteractionHandler: update toggle state, then click.
    private static void Activate(MenuItem source)
    {
        if (!source.IsEffectivelyEnabled)
        {
            return;
        }

        if (source.ToggleType == MenuItemToggleType.CheckBox)
        {
            source.IsChecked = !source.IsChecked;
        }
        else if (source.ToggleType == MenuItemToggleType.Radio && !source.IsChecked)
        {
            source.IsChecked = true;
        }

        source.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }

    /// <summary>Strips access-key underscores ("E_xit" → "Exit", "__" → "_").</summary>
    internal static string? CleanHeader(object? header)
    {
        if (header is not string text)
        {
            return header?.ToString();
        }

        var builder = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '_')
            {
                if (i + 1 < text.Length && text[i + 1] == '_')
                {
                    builder.Append('_');
                    i++;
                }

                continue;
            }

            builder.Append(text[i]);
        }

        return builder.ToString();
    }
}

