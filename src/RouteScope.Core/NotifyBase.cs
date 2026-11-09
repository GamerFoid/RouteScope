using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RouteScope.Core;

/// <summary>
/// Minimal INotifyPropertyChanged base for the persisted models. They are bound straight
/// to the UI, so a toggle or rename has to light up immediately without a wrapper layer.
/// </summary>
public abstract class NotifyBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected void RaiseAll(params string[] names)
    {
        foreach (var n in names) Raise(n);
    }
}
