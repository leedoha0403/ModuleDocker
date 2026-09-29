using System.ComponentModel;
using System.Runtime.CompilerServices;
using Dora.Widget.Abstractions;

namespace Dora.Widget.SDK;

/// <summary>Base class for the single feature ViewModel shared by docked, floating and detail surfaces.</summary>
public abstract class WidgetViewModelBase : INotifyPropertyChanged
{
    private WidgetDisplayMode _displayMode = WidgetDisplayMode.Compact;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Host-selected mode; widgets only render it and never choose it.</summary>
    public WidgetDisplayMode DisplayMode
    {
        get => _displayMode;
        internal set => SetField(ref _displayMode, value);
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
