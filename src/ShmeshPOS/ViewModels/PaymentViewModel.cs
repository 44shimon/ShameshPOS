using System.Collections.ObjectModel;
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

/// <summary>A part of a split payment, taken before the sale is completed.</summary>
public sealed record PartPayment(PaymentMethod Method, decimal Amount)
{
    public string Name => Method.Name;
}

/// <summary>
/// F8 payment overlay for the current cart. The sale can be paid in parts (e.g. some cash, the rest by card):
/// an amount below what is due is added as a part, and the part that covers the rest completes the sale.
/// Nothing is saved until then; all parts are written in the sale's single transaction.
/// </summary>
public partial class PaymentViewModel : ObservableObject, IKeyboardHandler
{
    private readonly ISaleService _saleService;
    private readonly IDialogService _dialogs;
    private readonly ILogger _logger;
    private readonly IReadOnlyList<SaleLineInput> _lines;
    private readonly Discount? _saleDiscount;

    public PaymentViewModel(
        IReadOnlyList<SaleLineInput> lines,
        decimal total,
        IReadOnlyList<PaymentMethod> methods,
        CustomerAccount? customer,
        ISaleService saleService,
        IDialogService dialogs,
        ILogger logger,
        Discount? saleDiscount = null)
    {
        _lines = lines;
        _saleDiscount = saleDiscount;
        _saleService = saleService;
        _dialogs = dialogs;
        _logger = logger;
        Total = total;
        Customer = customer;
        // Charging to an account is only offered once a customer is attached to the sale.
        Methods = methods
            .Where(m => m.Kind != PaymentKind.Account || customer is not null)
            .Select(m => new PaymentMethodOptionViewModel(m)).ToList();
        Parts.CollectionChanged += (_, _) => OnPartsChanged();
        OnPartsChanged();
        SelectMethod(Methods.FirstOrDefault(m => m.Method.Kind == PaymentKind.Cash) ?? Methods.FirstOrDefault());
    }

    public decimal Total { get; }
    public CustomerAccount? Customer { get; }
    public bool HasCustomer => Customer is not null;
    public IReadOnlyList<PaymentMethodOptionViewModel> Methods { get; }

    /// <summary>Parts already taken (split payment), oldest first.</summary>
    public ObservableCollection<PartPayment> Parts { get; } = [];
    public bool HasParts => Parts.Count > 0;

    /// <summary>What is still to be paid.</summary>
    public decimal Remaining => Total - Parts.Sum(p => p.Amount);

    [ObservableProperty]
    private IReadOnlyList<decimal> _quickCashAmounts = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCash), nameof(IsAccount), nameof(IsCardOrOther), nameof(AmountLabel))]
    [NotifyPropertyChangedFor(nameof(AmountApplied), nameof(Change), nameof(AmountShort), nameof(Problem), nameof(CanComplete),
        nameof(CompletesSale), nameof(ActionText))]
    private PaymentMethodOptionViewModel? _selectedMethod;

    /// <summary>Cash received (cash), or the amount for this part (card, other, account). Blank means "all that is due".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EnteredAmount), nameof(AmountApplied), nameof(Change), nameof(AmountShort), nameof(Problem))]
    [NotifyPropertyChangedFor(nameof(CanComplete), nameof(CompletesSale), nameof(ActionText))]
    private string _amountText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanComplete))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    public bool IsCash => SelectedMethod?.Method.Kind == PaymentKind.Cash;
    public bool IsAccount => SelectedMethod?.Method.Kind == PaymentKind.Account;
    public bool IsCardOrOther => SelectedMethod is not null && !IsCash && !IsAccount;
    public string AmountLabel => IsCash ? "CASH RECEIVED" : "AMOUNT (blank = all that is due)";

    public decimal? EnteredAmount =>
        decimal.TryParse(AmountText, NumberStyles.Number, CultureInfo.CurrentCulture, out var value) && value >= 0
            ? SaleCalculator.RoundMoney(value)
            : null;

    /// <summary>What this part pays towards the sale.</summary>
    public decimal? AmountApplied => IsCash
        ? EnteredAmount is { } cash ? Math.Min(cash, Remaining) : Remaining == 0 ? 0m : null
        : EnteredAmount ?? Remaining;

    public decimal? Change => IsCash && EnteredAmount is { } received ? SaleCalculator.CalculateChange(Remaining, received) : null;

    /// <summary>How much more is needed after this part; null once it covers what is due.</summary>
    public decimal? AmountShort => AmountApplied is { } applied && applied < Remaining && applied > 0 ? Remaining - applied : null;

    /// <summary>This part pays everything that is still due.</summary>
    public bool CompletesSale => AmountApplied is { } applied && applied == Remaining;

    public string ActionText => CompletesSale ? "COMPLETE" : AmountApplied is > 0 ? $"ADD {AmountApplied:C}" : "COMPLETE";

    /// <summary>Why this part can't be taken, or null when it can.</summary>
    public string? Problem
    {
        get
        {
            if (SelectedMethod is null) return null;
            if (!IsCash && EnteredAmount is { } amount && amount > Remaining)
                return $"That's more than the {Remaining:C} still due.";
            if (IsAccount && Parts.Any(p => p.Method.Kind == PaymentKind.Account))
                return "The account is already charged for part of this sale.";
            if (IsAccount && Customer is { } c && AmountApplied is { } charge && charge > c.AvailableCredit)
                return $"Over the limit: {c.Name} has only {c.AvailableCredit:C} available (limit {c.CreditLimit:C}). Charge part and pay the rest another way.";
            return null;
        }
    }

    public bool CanComplete => !IsBusy && SelectedMethod is not null && Problem is null
        && AmountApplied is { } applied && (applied > 0 || Remaining == 0)
        && (!IsAccount || Customer is not null);

    /// <summary>Raised after the sale has been committed to the database.</summary>
    public event EventHandler<CompletedSale>? Completed;
    public event EventHandler? Cancelled;

    private void OnPartsChanged()
    {
        QuickCashAmounts = Remaining > 0 ? BuildQuickCashAmounts(Remaining) : [];
        OnPropertyChanged(nameof(HasParts));
        OnPropertyChanged(nameof(Remaining));
        OnPropertyChanged(nameof(AmountApplied));
        OnPropertyChanged(nameof(Change));
        OnPropertyChanged(nameof(AmountShort));
        OnPropertyChanged(nameof(Problem));
        OnPropertyChanged(nameof(CompletesSale));
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(CanComplete));
    }

    [RelayCommand]
    private void SelectMethod(PaymentMethodOptionViewModel? option)
    {
        if (IsBusy || option is null) return;
        foreach (var m in Methods) m.IsSelected = m == option;
        SelectedMethod = option;
        AmountText = "";
        ErrorMessage = null;
    }

    [RelayCommand]
    private void QuickCash(decimal amount)
    {
        if (IsBusy) return;
        SelectMethod(Methods.FirstOrDefault(m => m.Method.Kind == PaymentKind.Cash));
        AmountText = amount.ToString("0.00", CultureInfo.CurrentCulture);
    }

    [RelayCommand]
    private void Keypad(string key)
    {
        if (IsBusy || SelectedMethod is null) return;
        var decimalSeparator = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
        AmountText = key switch
        {
            "BKSP" => AmountText.Length > 0 ? AmountText[..^1] : "",
            "CLR" => "",
            "." when AmountText.Contains(decimalSeparator) => AmountText,
            "." => (AmountText.Length == 0 ? "0" : AmountText) + decimalSeparator,
            _ when AmountText.Length >= 9 => AmountText,
            _ => AmountText + key,
        };
    }

    [RelayCommand]
    private async Task RemovePartAsync(PartPayment? part)
    {
        if (IsBusy || part is null) return;
        if (part.Method.Kind != PaymentKind.Cash && !await _dialogs.ConfirmAsync("Remove Payment",
                $"Remove {part.Name} {part.Amount:C}?\nIf the card was already charged, reverse it on the card terminal.", "Remove", "Keep"))
            return;
        Parts.Remove(part);
        ErrorMessage = null;
    }

    /// <summary>COMPLETE / Enter: takes this part, and completes the sale when it covers what is due.</summary>
    [RelayCommand]
    private async Task CompleteAsync()
    {
        if (!CanComplete || SelectedMethod is null || AmountApplied is not { } applied) return;

        if (!CompletesSale)
        {
            Parts.Add(new PartPayment(SelectedMethod.Method, applied));
            AmountText = "";
            ErrorMessage = null;
            // Most splits are some cash, then the rest on a card: suggest the next method.
            if (IsCash) SelectMethod(Methods.FirstOrDefault(m => m.Method.Kind == PaymentKind.Card) ?? SelectedMethod);
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var payments = Parts.Select(p => new PaymentInput(p.Method.Id, p.Amount, p.Method.Kind == PaymentKind.Cash ? p.Amount : null)).ToList();
            payments.Add(IsCash
                ? new PaymentInput(SelectedMethod.Method.Id, applied, EnteredAmount ?? applied)
                : new PaymentInput(SelectedMethod.Method.Id, applied));
            var sale = await _saleService.CompleteSaleAsync(new CompleteSaleRequest(_lines, payments, Customer?.Id, _saleDiscount));
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
    private async Task CancelAsync()
    {
        // Once the sale is being written, the cashier must wait for the result.
        if (IsBusy) return;
        if (Parts.Any(p => p.Method.Kind is PaymentKind.Card or PaymentKind.Other) && !await _dialogs.ConfirmAsync("Cancel Payment",
                "Part of this sale was already paid by card.\nReverse it on the card terminal, then cancel.", "Cancel Payment", "Keep Paying"))
            return;
        Cancelled?.Invoke(this, EventArgs.Empty);
    }

    public bool HandleKey(Key key, bool textInputFocused)
    {
        switch (key)
        {
            case Key.Escape:
                CancelCommand.Execute(null);
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
        // Leave editing keys to the amount box; swallow other shortcuts so the POS underneath stays put.
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
