namespace CanYeAye.Core;

/// <summary>
/// Grid visibility rules shared by the frame display and "Save Filtered Log". A frame is shown
/// only when both its frame ID and its bus are explicitly enabled; an optional search text must
/// also match the ID label (which the app builds from the ID, J1939 node name and DBC message).
/// </summary>
public static class FrameVisibility
{
    public static bool IsVisible(
        CanFrame frame,
        IReadOnlyDictionary<uint, bool> idFilters,
        IReadOnlyDictionary<byte, bool> busFilters,
        string? searchText = null,
        Func<CanFrame, string>? labelSelector = null)
    {
        if (!idFilters.TryGetValue(frame.FrameId, out var idVisible) || !idVisible)
        {
            return false;
        }

        if (!busFilters.TryGetValue(frame.Bus, out var busVisible) || !busVisible)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(searchText))
        {
            var label = labelSelector?.Invoke(frame);
            if (label is null ||
                !label.Contains(searchText, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Filters a snapshot, preserving capture order (what "Save Filtered Log" writes).</summary>
    public static List<CanFrame> SelectVisible(
        IReadOnlyList<CanFrame> frames,
        IReadOnlyDictionary<uint, bool> idFilters,
        IReadOnlyDictionary<byte, bool> busFilters,
        string? searchText = null,
        Func<CanFrame, string>? labelSelector = null)
    {
        var visible = new List<CanFrame>(frames.Count);
        foreach (var frame in frames)
        {
            if (IsVisible(frame, idFilters, busFilters, searchText, labelSelector))
            {
                visible.Add(frame);
            }
        }

        return visible;
    }
}
