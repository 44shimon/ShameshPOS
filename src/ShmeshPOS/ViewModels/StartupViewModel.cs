using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShmeshPOS.Infrastructure;

namespace ShmeshPOS.ViewModels;

/// <summary>Shown while connecting to the database, and when start-up fails.</summary>
public partial class StartupViewModel(IApplicationControl app) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    private string _status = "Connecting to database…";

    public bool HasError => ErrorMessage is not null;

    public event EventHandler? RetryRequested;

    [RelayCommand]
    private void Retry()
    {
        ErrorMessage = null;
        RetryRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Exit() => app.ExitApplication();
}
