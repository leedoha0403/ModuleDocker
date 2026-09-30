using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Dora.Widget.Runtime.HostWindow;

namespace Dora.Widget.Host;

/// <summary>
/// Shared look of the Host (dark panel, rounded bordered buttons, teal accent), modelled on the
/// AIUsage Monitor widget so both apps feel like one family.
/// </summary>
internal static class HostStyles
{
    private static Style? _button;

    /// <summary>Rounded, bordered button whose border turns accent-coloured on hover.</summary>
    public static Style Button => _button ??= (Style)XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
          <Setter Property="MinHeight" Value="26"/>
          <Setter Property="MinWidth" Value="30"/>
          <Setter Property="Padding" Value="8,3"/>
          <Setter Property="FontSize" Value="12"/>
          <Setter Property="FontWeight" Value="SemiBold"/>
          <Setter Property="Cursor" Value="Hand"/>
          <Setter Property="Focusable" Value="False"/>
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="Button">
                <Border x:Name="Chrome" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                        BorderThickness="1" CornerRadius="8" Padding="{TemplateBinding Padding}">
                  <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True">
                    <Setter TargetName="Chrome" Property="BorderBrush" Value="{DynamicResource ModuleDock.Brush.Accent}"/>
                  </Trigger>
                  <Trigger Property="IsEnabled" Value="False">
                    <Setter Property="Opacity" Value="0.48"/>
                  </Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """);

    public static Button MakeButton(string content, string tooltip, RoutedEventHandler? click = null)
    {
        var b = new Button
        {
            Content = content,
            ToolTip = tooltip,
            Style = Button,
            Background = HostBrushes.Background,
            BorderBrush = HostBrushes.Border,
            Foreground = HostBrushes.Text
        };
        if (click != null) b.Click += click;
        return b;
    }
}

/// <summary>
/// Palette shared by every Host surface. The brush instances are stable and live-recoloured by
/// <see cref="HostTheme.Apply"/>, so everything that holds one (including hosted widgets reading
/// Host UI) follows a theme change without being rebuilt. Brushes are per UI thread
/// because unfrozen WPF brushes are dispatcher-affine.
/// </summary>
internal static class HostBrushes
{
    private sealed class Set
    {
        public readonly SolidColorBrush Surface = new(), Border = new(), Focus = new(), Pinned = new(),
            Background = new(), Text = new(), Muted = new(), Detach = new(), IconBack = new();
    }

    [ThreadStatic] private static Set? _set;

    private static Set Current => _set ??= Create();

    private static Set Create()
    {
        var set = new Set();
        Recolor(HostTheme.IsDark ? HostPalette.Dark : HostPalette.Light, set);
        return set;
    }

    /// <summary>Recolours this thread's brushes for the given palette.</summary>
    internal static void Recolor(HostPalette p) => Recolor(p, Current);

    private static void Recolor(HostPalette p, Set s)
    {
        s.Background.Color = p.Background; s.Surface.Color = p.Surface; s.Border.Color = p.Border;
        s.Text.Color = p.Text; s.Muted.Color = p.Muted; s.Focus.Color = p.Accent;
        s.Pinned.Color = p.Warning; s.Detach.Color = p.Danger; s.IconBack.Color = p.AccentBack;
    }

    /// <summary>Widget card background (inset, darker than the panel in dark theme).</summary>
    public static Brush Surface => Current.Surface;
    /// <summary>Hairline borders.</summary>
    public static Brush Border => Current.Border;
    /// <summary>Accent: focus ring, bars, handle, drop marker.</summary>
    public static Brush Focus => Current.Focus;
    public static Brush Drop => Current.Focus;
    public static Brush Pinned => Current.Pinned;
    /// <summary>Host panel background.</summary>
    public static Brush Background => Current.Background;
    public static Brush Text => Current.Text;
    public static Brush Muted => Current.Muted;
    public static Brush Detach => Current.Detach;
    /// <summary>Tinted backdrop behind the header icon.</summary>
    public static Brush IconBack => Current.IconBack;
}

/// <summary>One complete colour set of the Host theme.</summary>
internal sealed record HostPalette(
    Color Background, Color Surface, Color Border, Color Text, Color Muted,
    Color Accent, Color Warning, Color Danger, Color AccentBack)
{
    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static HostPalette Dark { get; } = new(
        C("#171B1D"), C("#101315"), C("#2A3336"), C("#F4F7F5"), C("#A9B3BD"),
        C("#3FDDB2"), C("#F2A93B"), C("#D64545"), C("#0F2A2B"));

    public static HostPalette Light { get; } = new(
        C("#F3F6F7"), C("#FFFFFF"), C("#D3DBDF"), C("#1A2124"), C("#5A6670"),
        C("#0E9F7E"), C("#C77F0A"), C("#C23B3B"), C("#D5F2E9"));
}

/// <summary>
/// The theme children inherit (see WIDGET_DESIGN_GUIDE.md). Published once into Application.Resources under fixed
/// "ModuleDock.*" keys; widgets read them with DynamicResource and fall back to their own values when absent.
/// On a theme change the published brushes are replaced, so DynamicResource consumers update live.
/// </summary>
public static class HostTheme
{
    public const string Prefix = "ModuleDock.";

    private static HostThemeMode _mode = HostThemeMode.System;
    private static bool _dark = true;

    /// <summary>The user's choice (System follows Windows' "app mode").</summary>
    public static HostThemeMode Mode => _mode;
    /// <summary>What is actually shown right now.</summary>
    public static bool IsDark => _dark;

    /// <summary>Raised after the effective theme changed; windows use it to refresh their title bar.</summary>
    public static event Action? Changed;

    /// <summary>Windows' "choose your default app mode" (true = dark). Defaults to dark when unreadable.</summary>
    internal static bool SystemPrefersDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int v || v == 0;
        }
        catch { return true; }
    }

    /// <summary>Sets the mode, recolours the shared brushes and notifies listeners.</summary>
    public static void Apply(HostThemeMode mode)
    {
        _mode = mode;
        var dark = mode switch { HostThemeMode.Dark => true, HostThemeMode.Light => false, _ => SystemPrefersDark() };
        var changed = dark != _dark;
        _dark = dark;
        HostBrushes.Recolor(dark ? HostPalette.Dark : HostPalette.Light);
        if (!changed) return;
        _token++;
        if (System.Windows.Application.Current is { } app) Publish(app.Resources);
        Changed?.Invoke();
    }

    /// <summary>Re-evaluates the system theme; a no-op unless the mode is System.</summary>
    public static void RefreshSystem()
    {
        if (_mode == HostThemeMode.System) Apply(HostThemeMode.System);
    }

    private static int _token;

    /// <summary>
    /// Changes on every theme change. A module that copies ModuleDock.* values into its own keys can bind a DP to this
    /// key with SetResourceReference and re-copy when it changes.
    /// </summary>
    public const string TokenKey = "ModuleDock.Theme.Token";

    /// <summary>
    /// Published brushes are frozen snapshots: Application.Resources freezes Freezables anyway, so they cannot be
    /// recoloured in place. A theme change replaces them (see <see cref="Publish"/>) and DynamicResource consumers follow.
    /// </summary>
    public static IReadOnlyDictionary<string, object> Resources => new Dictionary<string, object>
    {
        ["ModuleDock.Brush.Background"] = Snapshot(HostBrushes.Background),
        ["ModuleDock.Brush.Surface"] = Snapshot(HostBrushes.Surface),
        ["ModuleDock.Brush.Border"] = Snapshot(HostBrushes.Border),
        ["ModuleDock.Brush.Text"] = Snapshot(HostBrushes.Text),
        ["ModuleDock.Brush.Muted"] = Snapshot(HostBrushes.Muted),
        ["ModuleDock.Brush.Accent"] = Snapshot(HostBrushes.Focus),
        ["ModuleDock.Brush.Warning"] = Snapshot(HostBrushes.Pinned),
        ["ModuleDock.Brush.Danger"] = Snapshot(HostBrushes.Detach),
        ["ModuleDock.Brush.AccentBack"] = Snapshot(HostBrushes.IconBack),
        ["ModuleDock.Font.Family"] = new System.Windows.Media.FontFamily("Segoe UI, Malgun Gothic, Segoe UI Symbol"),
        ["ModuleDock.Font.SizeBody"] = 12.0,
        ["ModuleDock.Font.SizeTitle"] = 15.0,
        ["ModuleDock.Radius.Card"] = new System.Windows.CornerRadius(8),
        ["ModuleDock.Padding.Card"] = new System.Windows.Thickness(14),
        [TokenKey] = _token,
    };

    private static Brush Snapshot(Brush live)
    {
        var b = new SolidColorBrush(((SolidColorBrush)live).Color);
        b.Freeze();
        return b;
    }

    public static void Publish(System.Windows.ResourceDictionary target)
    {
        foreach (var (key, value) in Resources) target[key] = value;
    }
}

/// <summary>The ModuleDock app icon (embedded), for window title bars and the taskbar.</summary>
internal static class HostIcon
{
    [ThreadStatic] private static System.Windows.Media.ImageSource? _frame; // decoders are dispatcher-affine

    public static System.Windows.Media.ImageSource? Frame
    {
        get
        {
            if (_frame != null) return _frame;
            try
            {
                using var stream = typeof(HostIcon).Assembly.GetManifestResourceStream("ModuleDock.ico");
                if (stream == null) return null;
                var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(stream,
                    System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                // WPF picks the closest frame for each use; hand it the whole set via the largest frame.
                var best = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
                best.Freeze();
                return _frame = best;
            }
            catch { return null; }
        }
    }
}
