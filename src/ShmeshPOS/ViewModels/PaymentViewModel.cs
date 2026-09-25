using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ShmeshPOS.Infrastructure;
using ShmeshPOS.Models;
using ShmeshPOS.Services;

namespace ShmeshPOS.ViewModels;

public partial class PaymentMethodOptionViewModel(PaymentMethod method) : ObservableObject
{
    public PaymentMethod Method { get; } = method;
    public string Name => Method.Name;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>F8 payment overlay for the current cart.</summary>
public partial class PaymentViewModel : ObservableObject, IKeyboardHandler
{
    private readonly ISaleService _saleService;
    private readonly ILogger _logger;
    private readonly IReadOnlyList<SaleLineInput> _lines;

    public PaymentViewModel(
        IReadOnlyList<SaleLineInput> lines,
        decimal total,
        IReadOnlyList<PaymentMethod> methods,
        CustomerAccount? customer,
        ISaleService saleService,
        ILogger logger)
    {
        _lines = lines;
        _saleService = saleService;
        _logger = logger;
        Total = total;
        Customer = customer;
        // Charging to an account is only offered once a customer is attached to the sale.
        Methods = methods
            .Where(m => m.Kind != PaymentKind.Account || customer is not null)
            .Select(m => new PaymentMethodOptionViewModel(m)).ToList();
        QuickCashAmounts = BuildQuickCashAmounts(total);
        SelectMethod(Methods.FirstOrDefault(m => m.Method.Kind == PaymentKind.Cash) ?? Methods.FirstOrDefault());
    }

    public decimal Total { get; }
    public CustomerAccount? Customer { get; }
    public bool HasCustomer => Customer is not null;

    /// <summary>Why the account can't take this charge, or null when it can.</summary>
    public string? AccountProblem => IsAccount && Customer is { } c && Total > c.AvailableCredit
        ? $"Over the limit: {c.Name} has only {c.AvailableCredit:C} available (limit {c.CreditLimit:C})."
        : null;
    public IReadOnlyList<PaymentMethodOptionViewModel> Methods { get; }
    public IReadOnlyList<decimal> QuickCashAmounts { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCash), nameof(IsAccount), nameof(IsCardOrOther), nameof(AccountProblem))]
    [NotifyPropertyChangedFor(nameof(CanComplete))]
    private PaymentMethodOptionViewModel? _selectedMethod;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CashReceived))]
    [NotifyPropertyChangedFor(nameof(Change))]
    [NotifyPropertyChangedFor(nameof(AmountShort))]
    [NotifyPropertyChangedFor(nameof(CanComplete))]
    private string _cashReceivedText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanComplete))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    public bool IsCash => SelectedMethod?.Method.Kind == PaymentKind.Cash;
    public bool IsAccount => SelectedMethod?.Method.Kind == PaymentKind.Account;
    public bool IsCardOrOther => SelectedMethod is not null && !IsCash && !IsAccount;

    public decimal? CashReceived =>
        decimal.TryParse(CashReceivedText, NumberStyles.Number, CultureInfo.CurrentCulture, out var value) && value >= 0
            ? SaleCalculator.RoundMoney(value)
            : null;

    public decimal? Change => CashReceived is { } received ? SaleCalculator.CalculateChange(Total, received) : null;

    /// <summary>How much more cash is needed; null once the amount is covered or nothing is entered.</summary>
    public decimal? AmountShort => CashReceived is { } received && received < Total ? Total - received : null;

    public bool CanComplete => !IsBusy && SelectedMethod is not null
        && (!IsCash || Change is not null)
        && (!IsAccount || (Customer is not null && AccountProblem is null));

    /// <summary>Raised after the sale has been committed to the database.</summary>
    public event EventHandler<CompletedSale>? Completed;
    public event EventHandler? Cancelled;

    [RelayCommand]
    private void SelectMethod(PaymentMethodOptionViewModel? option)
    {
        if (IsBusy || option is null) return;
        foreach (var m in Methods) m.IsSelected = m == option;
        SelectedMethod = option;
        ErrorMessage = null;
    }

    [RelayCommand]
    private void QuickCash(decimal amount)
    {
        if (IsBusy) return;
        SelectMethod(Methods.FirstOrDefault(m => m.Method.Kind == PaymentKind.Cash));
        CashReceivedText = amount.ToString("0.00", CultureInfo.CurrentCulture);
    }

    [RelayCommand]
    private void Keypad(string key)
    {
        if (IsBusy || !IsCash) return;
        var decimalSeparator = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
        CashReceivedText = key switch
        {
            "BKSP" => CashReceivedText.Length > 0 ? CashReceivedText[..^1] : "",
            "CLR" => "",
            "." when CashReceivedText.Contains(decimalSeparator) => CashReceivedText,
            "." => (CashReceivedText.Length == 0 ? "0" : CashReceivedText) + decimalSeparator,
            _ when CashReceivedText.Length >= 9 => CashReceivedText,
            _ => CashReceivedText + key,
        };
    }

    [RelayCommand]
    private async Task CompleteAsync()
    {
        if (!CanComplete || SelectedMethod is null) return;

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var request = new CompleteSaleRequest(_lines, SelectedMethod.Method.Id, IsCash ? CashReceived : null, Customer?.Id);
            var sale = await _saleService.CompleteSaleAsync(request);
            Completed?.Invoke(this, sale);
        }
        catch (PosException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error completing payment");
            ErrorMessage = "Unable to complete the sale.\nPlease try again.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        // Once the sale is being written, the cashier must wait for the result.
        if (IsBusy) return;
        Cancelled?.Invoke(this, EventArgs.Empty);
    }

    public bool HandleKey(Key key, bool textInputFocused)
    {
        switch (key)
        {
            case Key.Escape:
                Cancel();
                return true;
            case Key.Enter:
                CompleteCommand.Execute(null);
                return true;
            case Key.F8:
                // Never let a second F8 press submit the payment.
                return true;
        }

        if (!textInputFocused && KeyInput.ToDigit(key) is { } digit)
        {
            Keypad(digit.ToString());
            return true;
        }
        // Leave editing keys to the cash box; swallow other shortcuts so the POS underneath stays put.
        return !textInputFocused || key is >= Key.F1 and <= Key.F24;
    }

    /// <summary>Exact amount plus the likely bills above it, e.g. $7.25 → 7.25, 8, 10, 20, 50.</summary>
    public static IReadOnlyList<decimal> BuildQuickCashAmounts(decimal total)
    {
        var amounts = new SortedSet<decimal> { total };
        foreach (var step in new[] { 1m, 5m, 10m, 20m, 50m, 100m })
            amounts.Add(Math.Ceiling(total / step) * step);
        return amounts.Take(5).ToList();
    }
}
