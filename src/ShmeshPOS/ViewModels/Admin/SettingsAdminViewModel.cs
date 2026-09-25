using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShmeshPOS.Infrastructure;
using ShmeshPOS.Services;

namespace ShmeshPOS.ViewModels.Admin;

/// <summary>
/// Store settings (shared by every register): customer credit limit and receipt text.
/// Register settings (this machine only): receipt printer and automatic printing. Plus backups.
/// </summary>
public partial class SettingsAdminViewModel : ObservableObject, IAdminSection
{
    public const string DefaultPrinterLabel = "Windows default printer";

    private readonly ISettingsService _settings;
    private readonly IReceiptPrinter _receiptPrinter;
    private readonly IBackupService _backups;
    private readonly IDialogService _dialogs;
    private readonly ILogger<SettingsAdminViewModel> _logger;
    private string _saved = "";

    public SettingsAdminViewModel(ISettingsService settings, IReceiptPrinter printer, IBackupService backups,
        IDialogService dialogs, IOptions<PosOptions> options, ILogger<SettingsAdminViewModel> logger)
    {
        _settings = settings;
        _receiptPrinter = printer;
        _backups = backups;
        _dialogs = dialogs;
        _logger = logger;
        TerminalName = options.Value.TerminalName;
    }

    public string TerminalName { get; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsDirty))] private string _defaultCreditLimitText = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsDirty))] private string _receiptHeader = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsDirty))] private string _receiptFooter = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsDirty))] private string _printer = DefaultPrinterLabel;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsDirty))] private bool _autoPrint;

    [ObservableProperty] private IReadOnlyList<string> _printers = [DefaultPrinterLabel];

    [ObservableProperty] private string _lastBackupText = "";
    [ObservableProperty] private bool _isBackingUp;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _statusMessage;

    public string BackupFolder => _backups.Folder;

    public bool IsDirty => Snapshot() != _saved;

    ICommand IAdminSection.SaveCommand => SaveCommand;
    ImportViewModel? IAdminSection.Import => null;

    public async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            Printers = [DefaultPrinterLabel, .. _receiptPrinter.InstalledPrinters()];
            var limit = await _settings.GetDefaultCreditLimitAsync();
            var receipt = await _settings.GetReceiptSettingsAsync();
            var print = await _settings.GetPrintSettingsAsync(TerminalName);

            DefaultCreditLimitText = limit.ToString("0.00", CultureInfo.CurrentCulture);
            ReceiptHeader = string.Join(Environment.NewLine, receipt.Header);
            ReceiptFooter = string.Join(Environment.NewLine, receipt.Footer);
            if (print.PrinterName is { } name && !Printers.Contains(name))
                Printers = [.. Printers, name]; // configured printer that isn't installed right now
            Printer = print.PrinterName ?? DefaultPrinterLabel;
            AutoPrint = print.AutoPrint;
            _saved = Snapshot();
            OnPropertyChanged(nameof(IsDirty));
            RefreshLastBackup();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load settings");
            ErrorMessage = "Unable to load settings. Please try again.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<bool> ConfirmDiscardAsync() =>
        !IsDirty || await _dialogs.ConfirmAsync("Unsaved Changes", "Discard the changes you made?", "Discard", "Keep Editing");

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
            await _settings.SaveDefaultCreditLimitAsync(limit);
            await _settings.SaveReceiptSettingsAsync(ReceiptText());
            await _settings.SavePrintSettingsAsync(TerminalName, new TerminalPrintSettings(PrinterName(), AutoPrint));
            DefaultCreditLimitText = limit.ToString("0.00", CultureInfo.CurrentCulture);
            _saved = Snapshot();
            OnPropertyChanged(nameof(IsDirty));
            StatusMessage = "Settings saved.";
        }
        catch (PosException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings");
            ErrorMessage = "Unable to save. Please try again.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Prints a sample receipt with the header/footer and printer currently on screen (saved or not).</summary>
    [RelayCommand]
    private void PrintTest()
    {
        ErrorMessage = null;
        StatusMessage = null;
        var sample = new ReceiptData("TEST", DateTime.Now, "Test", TerminalName, null, null, null,
            [new ReceiptLine("Test item", 2, 1.25m, 2.50m)], 2.50m, 0m, 2.50m,
            [new ReceiptPayment("Cash", 2.50m, 5.00m, 2.50m)]);
        try
        {
            _receiptPrinter.Print(ReceiptFormatter.Receipt(sample, ReceiptText()), PrinterName(), "Test receipt");
            StatusMessage = $"Test receipt sent to {Printer}.";
        }
        catch (PosException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task BackupNowAsync()
    {
        if (IsBackingUp) return;
        IsBackingUp = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            var result = await _backups.BackupNowAsync();
            StatusMessage = $"Backup saved: {System.IO.Path.GetFileName(result.Path)} ({result.SizeBytes / 1024.0:N0} KB).";
            RefreshLastBackup();
        }
        catch (PosException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual backup failed");
            ErrorMessage = "The backup failed. See the log for details.";
        }
        finally
        {
            IsBackingUp = false;
        }
    }

    private void RefreshLastBackup()
    {
        var last = _backups.LastBackup();
        LastBackupText = last is null
            ? "No backups yet on this computer."
            : $"Last backup: {last.Time:ddd d MMM yyyy, h:mm tt} ({last.SizeBytes / 1024.0:N0} KB)";
    }

    private ReceiptSettings ReceiptText() => new(Lines(ReceiptHeader), Lines(ReceiptFooter));

    private static IReadOnlyList<string> Lines(string text) =>
        text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).ToList();

    private string? PrinterName() => Printer == DefaultPrinterLabel || string.IsNullOrWhiteSpace(Printer) ? null : Printer;

    private string Snapshot() => string.Join("|", DefaultCreditLimitText, ReceiptHeader, ReceiptFooter, Printer, AutoPrint);
}
