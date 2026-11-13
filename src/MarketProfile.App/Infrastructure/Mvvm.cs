using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace MarketProfile.App.Infrastructure;

/// <summary>
/// Klasa bazowa dla modeli widoku (ViewModel) – powiadamia widok o zmianie właściwości,
/// dzięki czemu bindingi w XAML odświeżają się automatycznie.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Ustawia wartość pola i powiadamia widok, jeśli wartość się zmieniła.</summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// Prosta implementacja komendy (np. dla przycisku) wykonującej metodę asynchroniczną.
/// Na czas wykonywania komenda jest blokowana, żeby nie uruchomić jej dwa razy.
/// </summary>
public sealed class AsyncRelayCommand(Func<Task> execute) : ICommand
{
    private bool _isRunning;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isRunning;

    public async void Execute(object? parameter)
    {
        _isRunning = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);

        try
        {
            await execute();
        }
        finally
        {
            _isRunning = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
