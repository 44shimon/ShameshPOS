using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShmeshPOS.Data;
using ShmeshPOS.Infrastructure;
using ShmeshPOS.Models;
using ShmeshPOS.Services;

namespace ShmeshPOS.ViewModels;

/// <summary>Owns the top-level screen (start-up → login → POS) and routes keyboard input.</summary>
public partial class MainWindowViewModel(
    IServiceProvider services,
    DialogService dialogs,
    IDatabaseInitializer databaseInitializer,
    IProductService products,
    IApplicationControl app,
    ILogger<MainWindowViewModel> logger) : ObservableObject, IKeyboardHandler
{
    [ObservableProperty]
    private object? _currentView;

    public DialogService Dialogs { get; } = dialogs;

    public async Task StartAsync()
    {
        var startup = new StartupViewModel(app);
        startup.RetryRequested += async (_, _) => await InitializeAsync(startup);
        CurrentView = startup;
        await InitializeAsync(startup);
    }

    private async Task InitializeAsync(StartupViewModel startup)
    {
        try
        {
            startup.Status = "Connecting to database…";
            await databaseInitializer.InitializeAsync();
            ShowLogin();
        }
        catch (PosException ex)
        {
            logger.LogError(ex, "Start-up failed");
            startup.ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not connect to or initialise the database");
            startup.ErrorMessage = "Unable to connect to the Shmesh POS database.\nCheck that the server is running and the network is connected, then press Retry.";
        }
    }

    private void ShowLogin()
    {
        (CurrentView as IDisposable)?.Dispose();
        var login = services.GetRequiredService<LoginViewModel>();
        login.LoggedIn += async (_, user) => await ShowPosAsync(login, user);
        CurrentView = login;
    }

    private async Task ShowPosAsync(LoginViewModel login, User user)
    {
        try
        {
            // Reload the catalog on every login so price and product changes are picked up.
            login.IsBusy = true;
            await products.LoadAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load product catalog");
            services.GetRequiredService<IAuthenticationService>().Logout();
            login.ErrorMessage = "Unable to load products. Please try again.";
            return;
        }
        finally
        {
            login.IsBusy = false;
        }

        var pos = services.GetRequiredService<PosViewModel>();
        pos.LogoutRequested += (_, _) => ShowLogin();
        CurrentView = pos;
    }

    public bool HandleKey(Key key, bool textInputFocused) =>
        Dialogs.HandleKey(key, textInputFocused)
        || (CurrentView is IKeyboardHandler handler && handler.HandleKey(key, textInputFocused));
}
