using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AgenTally.UI.Infrastructure;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected bool SetCollectionIfChanged<T>(
        ref ObservableCollection<T> field,
        IEnumerable<T> values,
        [CallerMemberName] string? propertyName = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        T[] snapshot = values.ToArray();
        if (field.Count == snapshot.Length && field.SequenceEqual(snapshot))
        {
            return false;
        }

        field = new ObservableCollection<T>(snapshot);
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
