using Dora.Widget.Abstractions;

namespace Dora.Widget.Runtime;

public sealed record LayoutItem(
    string InstanceId,
    WidgetLayoutProfile Profile,
    WidgetInteractionState Interaction);

public sealed record LayoutSlot(
    string InstanceId,
    WidgetDisplayMode Mode,
    double Top,
    double Width,
    double Height);

public sealed record LayoutResult(
    IReadOnlyList<LayoutSlot> Slots,
    double TotalHeight,
    bool NeedsScrolling);

/// <summary>
/// Decides the effective display mode of every docked widget (spec section 14).
/// Pure logic: no UI dependencies.
/// </summary>
public sealed class AdaptiveLayoutManager
{
    private readonly double _spacing;

    public AdaptiveLayoutManager(double spacing = 4) => _spacing = spacing;

    /// <param name="items">Docked widgets in dock order. Dragging items must carry their previous state.</param>
    public LayoutResult Compute(WidgetSize available, IReadOnlyList<LayoutItem> items)
    {
        var modes = new WidgetDisplayMode[items.Count];

        // 1-3: pinned and focused -> Natural, others -> Compact.
        for (var i = 0; i < items.Count; i++)
        {
            modes[i] = Rank(items[i].Interaction) >= Rank(WidgetInteractionState.Focused)
                ? WidgetDisplayMode.Natural
                : WidgetDisplayMode.Compact;
            modes[i] = FitWidth(items[i].Profile, modes[i], available.Width);
        }

        // 4: not enough height -> downgrade non-priority widgets (lowest priority first, later items first).
        if (TotalHeight(items, modes) > available.Height)
        {
            var order = Enumerable.Range(0, items.Count)
                .Where(i => Rank(items[i].Interaction) < Rank(WidgetInteractionState.Focused))
                .OrderBy(i => Rank(items[i].Interaction))
                .ThenByDescending(i => i)
                .ToList();

            foreach (var i in order)
            {
                if (modes[i] == WidgetDisplayMode.Compact)
                {
                    modes[i] = WidgetDisplayMode.Collapsed;
                    if (TotalHeight(items, modes) <= available.Height) break;
                }
            }
        }

        var total = TotalHeight(items, modes);

        var slots = new List<LayoutSlot>(items.Count);
        var top = 0d;
        for (var i = 0; i < items.Count; i++)
        {
            var size = items[i].Profile.PreferredSize(modes[i]);
            slots.Add(new LayoutSlot(items[i].InstanceId, modes[i], top, available.Width, size.Height));
            top += size.Height + _spacing;
        }

        // 5: still insufficient -> scrolling.
        return new LayoutResult(slots, total, total > available.Height);
    }

    /// <summary>Priority: Dragging &gt; Pinned &gt; Focused &gt; HoverPending &gt; Idle.</summary>
    public static int Rank(WidgetInteractionState s) => s switch
    {
        WidgetInteractionState.Dragging => 5,
        WidgetInteractionState.Pinned => 4,
        WidgetInteractionState.Focused or WidgetInteractionState.FocusReleasePending => 3,
        WidgetInteractionState.HoverPending => 2,
        _ => 1
    };

    /// <summary>Never allocate a mode whose declared minimum width exceeds the Host width.</summary>
    private static WidgetDisplayMode FitWidth(WidgetLayoutProfile p, WidgetDisplayMode mode, double width)
    {
        while (mode > WidgetDisplayMode.Collapsed && p.MinimumSize(mode).Width > width)
            mode--;
        return mode;
    }

    private double TotalHeight(IReadOnlyList<LayoutItem> items, WidgetDisplayMode[] modes)
    {
        if (items.Count == 0) return 0;
        var sum = 0d;
        for (var i = 0; i < items.Count; i++)
            sum += items[i].Profile.PreferredSize(modes[i]).Height;
        return sum + _spacing * (items.Count - 1);
    }
}
