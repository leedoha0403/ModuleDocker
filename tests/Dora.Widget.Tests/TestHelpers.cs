using Dora.Widget.Abstractions;
using Dora.Widget.Runtime;

namespace Dora.Widget.Tests;

internal static class T
{
    public static WidgetLayoutProfile Profile(double nat = 100, double comp = 40, double coll = 20) => new()
    {
        NaturalSize = new(200, nat),
        CompactSize = new(200, comp),
        CollapsedSize = new(200, coll),
        MinNaturalSize = new(150, nat),
        MinCompactSize = new(100, comp),
        MinCollapsedSize = new(40, coll)
    };

    public static WidgetManifest Manifest(string id = "dev.test.sample", bool multi = false,
        bool detail = true, bool floating = true) => new()
    {
        Id = id,
        Name = "Sample",
        Version = new Version(1, 0, 0),
        ContractVersion = ContractInfo.Current,
        Layout = Profile(),
        Capabilities = WidgetCapabilities.None,
        AllowMultipleInstances = multi,
        SupportsDetailView = detail,
        SupportsFloating = floating
    };

    public static (WidgetStateMachine sm, ManualTimerScheduler timers) Machine(params string[] ids)
    {
        var timers = new ManualTimerScheduler();
        var sm = new WidgetStateMachine(timers);
        foreach (var id in ids)
            sm.Register(new WidgetRuntimeState { InstanceId = id, WidgetId = "w" });
        return (sm, timers);
    }

    public static readonly TimeSpan Hover = TimeSpan.FromMilliseconds(200);
    public static readonly TimeSpan Release = TimeSpan.FromMilliseconds(300);
}
