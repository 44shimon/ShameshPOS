using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ShmeshPOS.Services;

namespace ShmeshPOS.ViewModels.Admin;

/// <summary>Store-wide settings shared by every register.</summary>
public partial class SettingsAdminViewModel(ISettingsService settings, IDialogService dialogs, ILogger<SettingsAdminViewModel> logger)
    : ObservableObject, IAdminSection
{
    private string _savedDefaultCreditLimit = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string _defaultCreditLimitText = "";

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _statusMessage;

    public bool IsDirty => DefaultCreditLimitText != _savedDefaultCreditLimit;

    ICommand IAdminSection.SaveCommand => SaveCommand;
    ImportViewModel? IAdminSection.Import => null;

    public async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            var limit = await settings.GetDefaultCreditLimitAsync();
            _savedDefaultCreditLimit = limit.ToString("0.00", CultureInfo.CurrentCulture);
            DefaultCreditLimitText = _savedDefaultCreditLimit;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load settings");
            ErrorMessage = "Unable to load settings. Please try again.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<bool> ConfirmDiscardAsync() =>
        !IsDirty || await dialogs.ConfirmAsync("Unsaved Changes", "Discard the changes you made?", "Discard", "Keep Editing");

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsBusy) return;
        ErrorMessage = null;
        StatusMessage = null;

        var text = DefaultCreditLimitText.Trim().Replace(CultureInfo.CurrentCulture.NumberFormat.CurrencySymbol, "");
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var limit))
        {
            ErrorMessage = "Enter the default credit limit as an amount, e.g. 50.00.";
            return;
        }

        IsBusy = true;
        try
        {
            await settings.SaveDefaultCreditLimitAsync(limit);
            _savedDefaultCreditLimit = limit.ToString("0.00", CultureInfo.CurrentCulture);
            DefaultCreditLimitText = _savedDefaultCreditLimit;
            StatusMessage = "Settings saved.";
        }
        catch (PosException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save settings");
            ErrorMessage = "Unable to save. Please try again.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
