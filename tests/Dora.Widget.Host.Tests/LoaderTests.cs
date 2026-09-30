using System.IO;
using Dora.Widget.Abstractions;
using Dora.Widget.Runtime;

namespace Dora.Widget.Host.Tests;

public class LoaderTests
{
    [Fact]
    public void Finds_widgets_in_a_plugin_folder_and_ignores_missing_folders()
    {
        var reg = new WidgetRegistry();
        Assert.Equal(0, WidgetAssemblyLoader.LoadInto(reg, Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid())).Registered);

        // DiskUsage.Widget.dll is a project reference of this test project, so it sits next to the tests.
        var result = WidgetAssemblyLoader.LoadInto(reg, AppContext.BaseDirectory);
        Assert.True(result.Registered >= 1, string.Join("; ", result.Errors));
        Assert.True(reg.TryGetManifest("dev.leedoha.diskusage.overview", out var m));
        Assert.Equal(WidgetCapabilities.FileSystem, m.Capabilities);
    }

    [Fact]
    public void A_broken_plugin_is_reported_and_does_not_stop_the_others()
    {
        var dir = Path.Combine(Path.GetTempPath(), "md-plugins-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "bad"));
            File.WriteAllText(Path.Combine(dir, "bad", "Broken.Widget.dll"), "this is not an assembly");
            Directory.CreateDirectory(Path.Combine(dir, "good"));
            foreach (var f in Directory.GetFiles(AppContext.BaseDirectory, "DiskUsage.*.dll"))
                File.Copy(f, Path.Combine(dir, "good", Path.GetFileName(f)));

            var reg = new WidgetRegistry();
            var result = WidgetAssemblyLoader.LoadInto(reg, dir);
            Assert.Contains(result.Errors, e => e.StartsWith("Broken.Widget.dll"));
            Assert.True(reg.TryGetManifest("dev.leedoha.diskusage.overview", out _));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Registering_the_same_widget_twice_is_an_error_not_a_crash()
    {
        var reg = new WidgetRegistry();
        WidgetAssemblyLoader.LoadInto(reg, AppContext.BaseDirectory);
        var again = WidgetAssemblyLoader.LoadInto(reg, AppContext.BaseDirectory);
        Assert.Contains(again.Errors, e => e.Contains("already registered"));
    }
}
