using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace CanYeAye.App.Services;

/// <summary>
/// Stable per-ID tint for highlighted rows in the frame grid.
/// </summary>
/// <remarks>
/// The palettes are hand-tuned per theme instead of being derived from one colour with an
/// alpha, because a translucent hue that reads well over near-black is invisible over white
/// (yellow especially). Every swatch keeps the grid's own foreground readable on top: dark
/// rows are deep, desaturated tints and light rows are pale pastels.
/// </remarks>
public static class FrameHighlightPalette
{
    private static readonly Color[] DarkPalette =
    [
        Color.FromRgb(0x4A, 0x26, 0x26), // red
        Color.FromRgb(0x4A, 0x35, 0x20), // orange
        Color.FromRgb(0x46, 0x41, 0x1B), // yellow
        Color.FromRgb(0x30, 0x3F, 0x1E), // lime
        Color.FromRgb(0x1E, 0x3D, 0x29), // green
        Color.FromRgb(0x16, 0x3D, 0x38), // teal
        Color.FromRgb(0x16, 0x37, 0x43), // cyan
        Color.FromRgb(0x1F, 0x2D, 0x4A), // blue
        Color.FromRgb(0x2A, 0x25, 0x47), // indigo
        Color.FromRgb(0x38, 0x24, 0x49), // violet
        Color.FromRgb(0x44, 0x20, 0x40), // magenta
        Color.FromRgb(0x4A, 0x23, 0x30), // pink
    ];

    private static readonly Color[] LightPalette =
    [
        Color.FromRgb(0xFA, 0xD6, 0xD6), // red
        Color.FromRgb(0xFB, 0xE2, 0xC4), // orange
        Color.FromRgb(0xF6, 0xEA, 0xB0), // yellow
        Color.FromRgb(0xE3, 0xF1, 0xC0), // lime
        Color.FromRgb(0xCD, 0xEE, 0xD7), // green
        Color.FromRgb(0xC6, 0xED, 0xE6), // teal
        Color.FromRgb(0xC7, 0xE8, 0xF5), // cyan
        Color.FromRgb(0xD3, 0xDF, 0xF9), // blue
        Color.FromRgb(0xDF, 0xD8, 0xF7), // indigo
        Color.FromRgb(0xEC, 0xD3, 0xF5), // violet
        Color.FromRgb(0xF7, 0xD3, 0xEC), // magenta
        Color.FromRgb(0xFA, 0xD3, 0xE0), // pink
    ];

    /// <summary>Brush for the filter list's swatch and the row background binding.</summary>
    public static IBrush BrushFor(uint id, bool extended) =>
        new SolidColorBrush(ColorFor(id, extended));

    /// <summary>Colour a row with this ID should use under the current theme variant.</summary>
    private static Color ColorFor(uint id, bool extended)
    {
        var palette = IsDark ? DarkPalette : LightPalette;
        return palette[PaletteIndex(id, extended)];
    }

    private static bool IsDark =>
        Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    /// <summary>
    /// Spreads consecutive IDs over the palette so neighbouring IDs do not share a colour.
    /// Stable across runs (no string hashing, which is randomized per process).
    /// </summary>
    private static int PaletteIndex(uint id, bool extended)
    {
        var hash = id * 2654435761u ^ (extended ? 0x85EBCA6Bu : 0u);
        hash ^= hash >> 16;
        hash *= 0x7FEB352Du;
        hash ^= hash >> 15;
        return (int)(hash % (uint)DarkPalette.Length);
    }
}
