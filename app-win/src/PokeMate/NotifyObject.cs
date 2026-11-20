using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PokeMate;

/// 맥의 ObservableObject 자리. 화면 쪽은 PropertyChanged 를 받아 다시 그린다 (UI 스레드에서만 바꾼다).
public abstract class NotifyObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
