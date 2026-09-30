using System.IO;
using DiskUsage.Core;
using DiskUsage.Presentation;
using DiskUsage.Widget;
using Dora.Widget.Abstractions;
using Dora.Widget.Host;
using Dora.Widget.Runtime;

namespace Dora.Widget.Host.Tests;

internal sealed class FakeDisks : IDiskService
{
    public List<DriveReading> Drives { get; set; } = new()
    {
        new DriveReading("C:", "System", 1000, 400),   // 60 %
        new DriveReading("D:", "Data", 2000, 1900)     // 5 %
    };
    public bool Throw;
    public IReadOnlyList<DriveReading> Read() => Throw ? throw new IOException("boom") : Drives.ToList();
}

public class DiskCoreTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(5L * 1024 * 1024 * 1024, "5 GB")]
    [InlineData(-5L, "0 B")]
    public void Byte_format(long bytes, string expected) => Assert.Equal(expected, ByteFormat.Format(bytes));

    [Fact]
    public void Drive_reading_math_and_names()
    {
        var r = new DriveReading("C:", "System", 1000, 250);
        Assert.Equal(750, r.UsedBytes);
        Assert.Equal(75, r.UsedPercent);
        Assert.Equal("C: (System)", r.DisplayName);
        Assert.Equal("D:", new DriveReading("D:", " ", 10, 5).DisplayName);
        Assert.Equal(0, new DriveReading("E:", "", 0, 0).UsedPercent);
        Assert.Equal(0, new DriveReading("F:", "", 100, 500).UsedBytes); // free > total must not go negative
    }

    [Fact]
    public void System_service_reads_at_least_one_real_drive()
    {
        var drives = new SystemDiskService().Read();
        Assert.NotEmpty(drives);
        Assert.All(drives, d => Assert.True(d.TotalBytes > 0));
    }
}

public class DiskViewModelTests
{
    [Fact]
    public void Refresh_lists_drives_and_reports_the_fullest_one()
    {
        Sta.Run(() =>
        {
            var vm = new DiskViewModel(new FakeDisks());
            Assert.Equal(2, vm.Drives.Count);
            Assert.Equal("C:", vm.Worst!.Name);
            Assert.Equal("C: 60%", vm.WorstText);
            Assert.False(vm.HasCritical);
        });
    }

    [Fact]
    public void Critical_state_is_raised_only_on_change()
    {
        Sta.Run(() =>
        {
            var disks = new FakeDisks();
            var vm = new DiskViewModel(disks);
            var events = new List<bool>();
            vm.CriticalChanged += events.Add;

            disks.Drives[0] = new DriveReading("C:", "System", 1000, 50);   // 95 %
            vm.Refresh();
            vm.Refresh();
            Assert.Equal(new[] { true }, events);
            Assert.True(vm.HasCritical);

            disks.Drives[0] = new DriveReading("C:", "System", 1000, 500);
            vm.Refresh();
            Assert.Equal(new[] { true, false }, events);
        });
    }

    [Fact]
    public void Refresh_keeps_selection_and_drops_it_when_the_drive_disappears()
    {
        Sta.Run(() =>
        {
            var disks = new FakeDisks();
            var vm = new DiskViewModel(disks);
            var picked = new List<string>();
            vm.DriveSelected += picked.Add;

            vm.Selected = vm.Drives[1];
            Assert.Equal(new[] { "D:\\" }, picked);

            vm.Refresh();
            Assert.Equal("D:", vm.Selected!.Name);
            Assert.Single(picked); // a refresh must not look like a new selection

            disks.Drives.RemoveAt(1);
            vm.Refresh();
            Assert.Null(vm.Selected);
            Assert.Single(vm.Drives);
        });
    }

    [Fact]
    public void Read_failure_is_reported_not_thrown_and_keeps_old_data()
    {
        Sta.Run(() =>
        {
            var disks = new FakeDisks();
            var vm = new DiskViewModel(disks);
            disks.Throw = true;
            vm.Refresh();
            Assert.Contains("Could not read disks", vm.Status);
            Assert.Equal(2, vm.Drives.Count);
        });
    }

    [Fact]
    public void Silent_selection_does_not_raise_the_event()
    {
        Sta.Run(() =>
        {
            var vm = new DiskViewModel(new FakeDisks());
            var raised = false;
            vm.DriveSelected += _ => raised = true;
            vm.SelectSilently("d:");
            Assert.Equal("D:", vm.Selected!.Name);
            Assert.False(raised);
        });
    }

    [Fact]
    public void Summary_view_renders_all_three_modes_without_error()
    {
        Sta.Run(() =>
        {
            var vm = new DiskViewModel(new FakeDisks());
            var view = new DiskSummaryView(vm);
            foreach (var mode in Enum.GetValues<WidgetDisplayMode>())
            {
                view.OnDisplayModeChanged(mode);
                view.Measure(new System.Windows.Size(260, 200));
                Assert.NotNull(view.ContentTemplate);
                Assert.True(view.DesiredSize.Height > 0, $"{mode} rendered nothing");
            }
            Assert.NotNull(new DiskDetailView(vm));
        });
    }
}

public class DiskWidgetTests
{
    private static (HostController hc, List<(string Topic, object? Payload)> events, List<string> prompts) Make(
        FakeDisks disks, bool allow, IPermissionGrantStore? grants = null)
    {
        var reg = new WidgetRegistry();
        reg.Register(() => new DiskWidget(disks));
        var prompts = new List<string>();
        var hc = new HostController(reg, new InMemoryWidgetStateStore(), new InMemoryLayoutStore(), grants: grants)
        {
            PermissionPrompt = (m, caps) => { prompts.Add(caps.ToString()); return Task.FromResult(allow); }
        };
        var events = new List<(string, object?)>();
        foreach (var topic in new[] { "host.attention", "filesystem.path.selected" })
            hc.Runtime.Events.Subscribe(topic, p => events.Add((topic, p)));
        return (hc, events, prompts);
    }

    [Fact]
    public void Manifest_declares_the_file_system_capability_and_passes_validation()
    {
        var m = new DiskWidget().Manifest;
        Assert.Equal(WidgetCapabilities.FileSystem, m.Capabilities);
        Assert.True(ManifestValidator.IsValid(ManifestValidator.Validate(m)));
        Assert.Empty(ManifestValidator.Validate(m).Where(i => i.Severity == ValidationSeverity.Warning));
    }

    [Fact]
    public void Denied_permission_shows_no_disks_and_no_attention()
    {
        Sta.Run(() =>
        {
            var (hc, events, prompts) = Make(new FakeDisks(), allow: false);
            hc.AddWidgetAsync("dev.leedoha.diskusage.overview").GetAwaiter().GetResult();
            Assert.Single(prompts);
            var vm = (DiskViewModel)((DiskSummaryView)hc.Panel.Chromes.Values.Single().SummaryView).DataContext;
            Assert.Empty(vm.Drives);
            Assert.Contains("Could not read disks", vm.Status);
            Assert.Empty(events);
        });
    }

    [Fact]
    public void Granted_permission_reads_disks_and_is_asked_only_once()
    {
        Sta.Run(() =>
        {
            var grants = new InMemoryPermissionGrantStore();
            var (hc, _, prompts) = Make(new FakeDisks(), allow: true, grants);
            hc.AddWidgetAsync("dev.leedoha.diskusage.overview").GetAwaiter().GetResult();
            var vm = (DiskViewModel)((DiskSummaryView)hc.Panel.Chromes.Values.Single().SummaryView).DataContext;
            Assert.Equal(2, vm.Drives.Count);
            Assert.Single(prompts);

            var (hc2, _, prompts2) = Make(new FakeDisks(), allow: false, grants);
            hc2.AddWidgetAsync("dev.leedoha.diskusage.overview").GetAwaiter().GetResult();
            Assert.Empty(prompts2); // the stored grant is used
            var vm2 = (DiskViewModel)((DiskSummaryView)hc2.Panel.Chromes.Values.Single().SummaryView).DataContext;
            Assert.Equal(2, vm2.Drives.Count);
        });
    }

    [Fact]
    public void Critical_disk_raises_host_attention_and_selection_publishes_the_path()
    {
        Sta.Run(() =>
        {
            var disks = new FakeDisks { Drives = new() { new DriveReading("C:", "", 1000, 20) } };
            var (hc, events, _) = Make(disks, allow: true);
            hc.AddWidgetAsync("dev.leedoha.diskusage.overview").GetAwaiter().GetResult();
            Assert.Contains(events, e => e.Topic == "host.attention" && e.Payload is true);

            var vm = (DiskViewModel)((DiskSummaryView)hc.Panel.Chromes.Values.Single().SummaryView).DataContext;
            vm.Selected = vm.Drives[0];
            Assert.Contains(events, e => e.Topic == "filesystem.path.selected" && (string?)e.Payload == "C:\\");

            disks.Drives[0] = new DriveReading("C:", "", 1000, 800);
            vm.Refresh();
            Assert.Contains(events, e => e.Topic == "host.attention" && e.Payload is false);
        });
    }

    [Fact]
    public void Refresh_command_and_selection_state_round_trip()
    {
        Sta.Run(() =>
        {
            var states = new InMemoryWidgetStateStore();
            var layouts = new InMemoryLayoutStore(); // shared, or the restarted Host would not know the instance id
            var disks = new FakeDisks();
            DiskViewModel Vm(HostController h) => (DiskViewModel)((DiskSummaryView)h.Panel.Chromes.Values.Single().SummaryView).DataContext;

            var reg = new WidgetRegistry();
            reg.Register(() => new DiskWidget(disks));
            var grants = new InMemoryPermissionGrantStore();
            grants.Set("dev.leedoha.diskusage.overview", WidgetCapabilities.FileSystem);
            var hc = new HostController(reg, states, layouts, grants: grants);
            hc.AddWidgetAsync("dev.leedoha.diskusage.overview").GetAwaiter().GetResult();
            var vm = Vm(hc);

            disks.Drives.Add(new DriveReading("E:", "", 100, 50));
            var instance = hc.Runtime.Instances.Single();
            Assert.Equal(2, vm.Drives.Count);
            // commands are namespaced <domain>.<resource>.<action> and registered through the context
            instance.Context.Commands.ExecuteAsync("diskusage.overview.refresh").GetAwaiter().GetResult();
            Assert.Equal(3, vm.Drives.Count);

            vm.Selected = vm.Drives.Single(d => d.Name == "E:");
            hc.ShutdownAsync().GetAwaiter().GetResult();

            var hc2 = new HostController(reg, states, layouts, grants: grants);
            hc2.RestoreAsync().GetAwaiter().GetResult();
            Assert.Equal("E:", Vm(hc2).Selected?.Name);
        });
    }
}
