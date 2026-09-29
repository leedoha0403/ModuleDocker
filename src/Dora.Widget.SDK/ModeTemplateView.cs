using System.Windows;
using System.Windows.Controls;
using Dora.Widget.Abstractions;

namespace Dora.Widget.SDK;

/// <summary>
/// Summary view helper: one shared ViewModel (Content/DataContext) plus a template per display mode.
/// The Host calls <see cref="OnDisplayModeChanged"/>; the widget never selects the mode itself.
/// </summary>
public class ModeTemplateView : ContentControl, IDisplayModeAware
{
    public static readonly DependencyProperty NaturalTemplateProperty = Register(nameof(NaturalTemplate));
    public static readonly DependencyProperty CompactTemplateProperty = Register(nameof(CompactTemplate));
    public static readonly DependencyProperty CollapsedTemplateProperty = Register(nameof(CollapsedTemplate));

    public static readonly DependencyProperty DisplayModeProperty = DependencyProperty.Register(
        nameof(DisplayMode), typeof(WidgetDisplayMode), typeof(ModeTemplateView),
        new PropertyMetadata(WidgetDisplayMode.Compact, (d, _) => ((ModeTemplateView)d).Apply()));

    private static DependencyProperty Register(string name) =>
        DependencyProperty.Register(name, typeof(DataTemplate), typeof(ModeTemplateView),
            new PropertyMetadata(null, (d, _) => ((ModeTemplateView)d).Apply()));

    public DataTemplate? NaturalTemplate { get => (DataTemplate?)GetValue(NaturalTemplateProperty); set => SetValue(NaturalTemplateProperty, value); }
    public DataTemplate? CompactTemplate { get => (DataTemplate?)GetValue(CompactTemplateProperty); set => SetValue(CompactTemplateProperty, value); }
    public DataTemplate? CollapsedTemplate { get => (DataTemplate?)GetValue(CollapsedTemplateProperty); set => SetValue(CollapsedTemplateProperty, value); }

    public WidgetDisplayMode DisplayMode
    {
        get => (WidgetDisplayMode)GetValue(DisplayModeProperty);
        set => SetValue(DisplayModeProperty, value);
    }

    public ModeTemplateView()
    {
        // Bind Content to DataContext so callers only need to set the ViewModel.
        Loaded += (_, _) => { if (Content is null) Content = DataContext; };
        DataContextChanged += (_, e) => { if (ContentIsViewModel) Content = e.NewValue; };
    }

    private bool ContentIsViewModel => Content is null || ReferenceEquals(Content, DataContext);

    public void OnDisplayModeChanged(WidgetDisplayMode mode)
    {
        DisplayMode = mode;
        if (DataContext is WidgetViewModelBase vm) vm.DisplayMode = mode;
    }

    private void Apply()
    {
        // Fall back to the next richer/leaner template if one is missing, so a view never renders blank.
        ContentTemplate = DisplayMode switch
        {
            WidgetDisplayMode.Natural => NaturalTemplate ?? CompactTemplate ?? CollapsedTemplate,
            WidgetDisplayMode.Compact => CompactTemplate ?? NaturalTemplate ?? CollapsedTemplate,
            _ => CollapsedTemplate ?? CompactTemplate ?? NaturalTemplate
        };
    }
}
