using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ShmeshPOS.Models;
using ShmeshPOS.Infrastructure;
using ShmeshPOS.Services;
using ShmeshPOS.Services.Import;

namespace ShmeshPOS.ViewModels.Admin;

/// <param name="EffectiveLimit">The customer's own limit, or the store default when they don't have one.</param>
public sealed record CustomerListItem(Customer Customer, decimal EffectiveLimit)
{
    public bool UsesDefaultLimit => Customer.CreditLimit is null;
    public decimal AvailableCredit => Math.Max(0m, EffectiveLimit - Customer.Balance);
    public bool IsOverLimit => Customer.Balance > EffectiveLimit;
}

public partial class CustomerEditorViewModel : EditorViewModel
{
    private static readonly HashSet<string> UntrackedProperties =
    [
        nameof(History), nameof(HistoryMessage), nameof(AdjustAmountText), nameof(AdjustNote),
    ];

    public CustomerEditorViewModel(Customer? customer, decimal defaultLimit)
    {
        Id = customer?.Id ?? 0;
        Heading = customer?.Name ?? "New Customer";
        DefaultLimit = defaultLimit;
        Balance = customer?.Balance ?? 0m;
        _accountNumber = customer?.AccountNumber ?? "";
        _name = customer?.Name ?? "";
        _phone = customer?.Phone ?? "";
        _email = customer?.Email ?? "";
        _notes = customer?.Notes ?? "";
        _useDefaultLimit = customer?.CreditLimit is null;
        _creditLimitText = (customer?.CreditLimit ?? defaultLimit).ToString("0.00", CultureInfo.CurrentCulture);
        _isActive = customer?.IsActive ?? true;
        StartTracking();
    }

    public int Id { get; }
    public override bool IsNew => Id == 0;
    public string Heading { get; }
    public decimal DefaultLimit { get; }
    public decimal Balance { get; }
    public string DefaultLimitLabel => $"Use the store default ({DefaultLimit:C})";

    [ObservableProperty] private string _accountNumber;
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _phone;
    [ObservableProperty] private string _email;
    [ObservableProperty] private string _notes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseOwnLimit))]
    private bool _useDefaultLimit;

    [ObservableProperty] private string _creditLimitText;
    [ObservableProperty] private bool _isActive;

    public bool UseOwnLimit => !UseDefaultLimit;

    // ---- History and adjustments (not part of the form's unsaved-change tracking) ----

    [ObservableProperty] private IReadOnlyList<CustomerTransaction> _history = [];
    [ObservableProperty] private string? _historyMessage;
    [ObservableProperty] private string _adjustAmountText = "";
    [ObservableProperty] private string _adjustNote = "";

    protected override bool IsTracked(string? propertyName) =>
        propertyName is null || !UntrackedProperties.Contains(propertyName);

    public CustomerInput ToInput()
    {
        decimal? limit = null;
        if (!UseDefaultLimit)
        {
            if (string.IsNullOrWhiteSpace(CreditLimitText)) throw new PosException("Enter the customer's credit limit, or use the store default.");
            limit = ParseDecimal(CreditLimitText, "Enter the credit limit as an amount, e.g. 75.00.");
        }
        return new CustomerInput(Id, AccountNumber, Name, Phone, Email, Notes, limit, IsActive);
    }

    public (decimal Amount, string Note) ToAdjustment()
    {
        if (string.IsNullOrWhiteSpace(AdjustAmountText)) throw new PosException("Enter the adjustment amount.");
        return (ParseDecimal(AdjustAmountText, "Enter the adjustment as an amount, e.g. 5.00 or -5.00."), AdjustNote);
    }
}

public partial class CustomersAdminViewModel(
    ICustomerService customers,
    ISettingsService settings,
    IImportService importer,
    IFileDialogService files,
    IDialogService dialogs,
    ILogger<CustomersAdminViewModel> logger)
    : AdminSectionViewModel<CustomerListItem, CustomerEditorViewModel>(dialogs, logger)
{
    public override bool CanImport => true;

    protected override ImportViewModel CreateImport() => new(
        "Import Customers",
        "Name, or First Name + Last Name (required) · Account Number · Phone · Email · Credit Limit · Balance · Notes · Active.\n" +
        "Existing customers are matched by account number (or by name when there is none) and updated. Blank account numbers are assigned automatically. " +
        "Balance becomes the opening balance of new customers; existing balances are never changed by an import.",
        "customer-import-template.csv",
        importer.CustomerTemplate,
        table => importer.PlanCustomersAsync(table),
        importer,
        files,
        Logger);

    private decimal _defaultLimit = SettingsService.FallbackDefaultCreditLimit;

    public override string Title => "Customers";
    public override string NewLabel => "New Customer";

    protected override async Task<IReadOnlyList<CustomerListItem>> FetchAsync()
    {
        _defaultLimit = await settings.GetDefaultCreditLimitAsync();
        var all = await customers.GetCustomersAsync();
        return all.Select(c => new CustomerListItem(c, c.CreditLimit ?? _defaultLimit)).ToList();
    }

    protected override int IdOf(CustomerListItem item) => item.Customer.Id;
    protected override string NameOf(CustomerListItem item) => item.Customer.Name;
    protected override bool IsActive(CustomerListItem item) => item.Customer.IsActive;

    protected override bool Matches(CustomerListItem item, string filter)
    {
        const StringComparison ic = StringComparison.OrdinalIgnoreCase;
        var c = item.Customer;
        return c.Name.Contains(filter, ic) || c.AccountNumber.Contains(filter, ic)
               || c.Phone?.Contains(filter, ic) == true || c.Email?.Contains(filter, ic) == true;
    }

    protected override CustomerEditorViewModel CreateEditor(CustomerListItem? item) => new(item?.Customer, _defaultLimit);

    protected override async Task<int> SaveEditorAsync(CustomerEditorViewModel editor) =>
        (await customers.SaveCustomerAsync(editor.ToInput())).Id;

    protected override void OnOpened(CustomerEditorViewModel editor, CustomerListItem? item)
    {
        if (item is not null)
            _ = LoadHistoryAsync(editor);
    }

    private async Task LoadHistoryAsync(CustomerEditorViewModel editor)
    {
        try
        {
            editor.History = await customers.GetTransactionsAsync(editor.Id, 50);
            editor.HistoryMessage = editor.History.Count == 0 ? "No account activity yet." : null;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load history for customer {CustomerId}", editor.Id);
            editor.HistoryMessage = "Unable to load account history.";
        }
    }

    [RelayCommand]
    private async Task AdjustBalanceAsync()
    {
        if (Editor is not { IsNew: false } editor || IsBusy) return;

        decimal amount;
        string note;
        try
        {
            (amount, note) = editor.ToAdjustment();
        }
        catch (PosException ex)
        {
            ErrorMessage = ex.Message;
            return;
        }

        var direction = amount > 0 ? $"add {amount:C} to" : $"take {-amount:C} off";
        if (!await Dialogs.ConfirmAsync("Adjust Balance",
                $"This will {direction} {editor.Heading}'s balance.\nReason: {note}", "Adjust", "Cancel"))
            return;

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var account = await customers.AdjustBalanceAsync(editor.Id, amount, note);
            IsBusy = false;
            await LoadAsync(editor.Id);
            StatusMessage = $"Balance adjusted. {account.Name} now owes {account.Balance:C}.";
        }
        catch (PosException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Balance adjustment failed for customer {CustomerId}", editor.Id);
            ErrorMessage = "Unable to adjust the balance. Please try again.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
