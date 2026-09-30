using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

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
                    <Setter TargetName="Chrome" Property="BorderBrush" Value="#3FDDB2"/>
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

/// <summary>Palette shared by every Host surface (matches the AIUsage Monitor dark theme).</summary>
internal static class HostBrushes
{
    /// <summary>Widget card background (inset, darker than the panel).</summary>
    public static readonly Brush Surface = Freeze("#101315");
    /// <summary>Hairline borders.</summary>
    public static readonly Brush Border = Freeze("#2A3336");
    /// <summary>Accent: focus ring, bars, handle, drop marker.</summary>
    public static readonly Brush Focus = Freeze("#3FDDB2");
    public static readonly Brush Drop = Focus;
    public static readonly Brush Pinned = Freeze("#F2A93B");
    /// <summary>Host panel background.</summary>
    public static readonly Brush Background = Freeze("#171B1D");
    public static readonly Brush Text = Freeze("#F4F7F5");
    public static readonly Brush Muted = Freeze("#A9B3BD");
    public static readonly Brush Detach = Freeze("#D64545");
    /// <summary>Dark teal used behind the header icon.</summary>
    public static readonly Brush IconBack = Freeze("#0F2A2B");

    private static Brush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
