using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MinterludeCalc.Overlay.ViewModels;

/// <summary>Small hand-rolled INotifyPropertyChanged base - avoids pulling in CommunityToolkit.Mvvm for this.</summary>
public class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected void Raise([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
