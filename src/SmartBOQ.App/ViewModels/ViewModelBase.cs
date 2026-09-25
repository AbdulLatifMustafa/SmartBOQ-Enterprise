using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SmartBOQ.App.ViewModels;

/// <summary>
/// Abstract base class implementing INotifyPropertyChanged for all MVVM ViewModels.
/// Provides standardized property mutation tracking and UI binding notifications.
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    private bool _isBusy;
    private string _title = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Indicates whether the ViewModel is executing an asynchronous operation.
    /// </summary>
    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    /// <summary>
    /// Title of the view or active tab.
    /// </summary>
    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    /// <summary>
    /// Compares old and new values, sets field, and raises PropertyChanged if changed.
    /// </summary>
    protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
        {
            return false;
        }

        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>
    /// Raises the PropertyChanged event for data binding.
    /// </summary>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
