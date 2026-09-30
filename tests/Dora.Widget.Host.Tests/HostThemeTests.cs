using System.Windows;
using System.Windows.Media;
using Dora.Widget.Host;

namespace Dora.Widget.Host.Tests;

public class HostThemeTests
{
    [Fact]
    public void Publishes_the_documented_keys_with_the_documented_types()
    {
        Sta.Run(() =>
        {
            var resources = new ResourceDictionary();
            HostTheme.Publish(resources);

            Assert.Equal(Color.FromRgb(0x3F, 0xDD, 0xB2), ((SolidColorBrush)resources["ModuleDock.Brush.Accent"]).Color);
            Assert.Equal(Color.FromRgb(0xF4, 0xF7, 0xF5), ((SolidColorBrush)resources["ModuleDock.Brush.Text"]).Color);
            Assert.IsType<FontFamily>(resources["ModuleDock.Font.Family"]);
            Assert.Equal(12.0, (double)resources["ModuleDock.Font.SizeBody"]);
            Assert.Equal(new CornerRadius(8), (CornerRadius)resources["ModuleDock.Radius.Card"]);
            Assert.All(HostTheme.Resources.Keys, k => Assert.StartsWith(HostTheme.Prefix, k));
        });
    }

    [Fact]
    public void Switching_the_theme_publishes_new_frozen_brushes()
    {
        Sta.Run(() =>
        {
            try
            {
                HostTheme.Apply(Dora.Widget.Runtime.HostWindow.HostThemeMode.Dark);
                var resources = new ResourceDictionary();
                HostTheme.Publish(resources);
                var text = (SolidColorBrush)resources["ModuleDock.Brush.Text"];
                Assert.Equal(Color.FromRgb(0xF4, 0xF7, 0xF5), text.Color);

                HostTheme.Apply(Dora.Widget.Runtime.HostWindow.HostThemeMode.Light);
                Assert.False(HostTheme.IsDark);
                Assert.True(text.IsFrozen);
                HostTheme.Publish(resources);
                var light = (SolidColorBrush)resources["ModuleDock.Brush.Text"];
                Assert.NotEqual(Color.FromRgb(0xF4, 0xF7, 0xF5), light.Color);
            }
            finally { HostTheme.Apply(Dora.Widget.Runtime.HostWindow.HostThemeMode.Dark); }
        });
    }
}
