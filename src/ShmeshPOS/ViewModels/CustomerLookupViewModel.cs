using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ShmeshPOS.Infrastructure;
using ShmeshPOS.Models;
using ShmeshPOS.Services;

namespace ShmeshPOS.ViewModels;

public partial class CustomerResultViewModel(CustomerAccount account) : ObservableObject
{
    public CustomerAccount Account { get; } = account;

    [ObservableProperty]
    private bool _isHighlighted;
}

/// <summary>
/// F4 overlay: find a customer to attach to the sale, or take a payment towards their account.
/// </summary>
public partial class CustomerLookupViewModel : ObservableObject, IKeyboardHandler
{
    private readonly ICustomerService _customers;
    private readonly IPaymentService _payments;
    private readonly ILogger _logger;
    private CancellationTokenSource? _searchCts;

    public CustomerLookupViewModel(ICustomerService customers, IPaymentService payments, ILogger logger)
    {
        _customers = customers;
        _payments = payments;
        _logger = logger;
    }

    // ---- Search ----------------------------------------------------------------------------

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private IReadOnlyList<CustomerResultViewModel> _results = [];

    [ObservableProperty]
    private string? _message;

    private int _highlight = -1;

    // ---- Account payment -------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPaymentMode))]
    private CustomerAccount? _payingCustomer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PaymentAmount), nameof(CanConfirmPayment), nameof(BalanceAfterPayment))]
    private string _paymentAmountText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirmPayment))]
    private PaymentMethodOptionViewModel? _paymentMethod;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirmPayment))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    public IReadOnlyList<PaymentMethodOptionViewModel> PaymentMethods { get; private set; } = [];

    public bool IsPaymentMode => PayingCustomer is not null;

    public decimal? PaymentAmount =>
        decimal.TryParse(PaymentAmountText, NumberStyles.Number, CultureInfo.CurrentCulture, out var v) && v > 0
            ? SaleCalculator.RoundMoney(v)
            : null;

    public decimal? BalanceAfterPayment => PayingCustomer is { } c && PaymentAmount is { } a ? c.Balance - a : null;

    public bool CanConfirmPayment => !IsBusy && PaymentAmount is not null && PaymentMethod is not null;

    public event EventHandler<CustomerAccount>? CustomerChosen;
    public event EventHandler<AccountPaymentResult>? PaymentRecorded;
    public event EventHandler? Closed;

    public async Task InitializeAsync()
    {
        try
        {
            var methods = await _payments.GetPaymentMethodsAsync();
            PaymentMethods = methods.Where(m => m.Kind != PaymentKind.Account)
                .Select(m => new PaymentMethodOptionViewModel(m)).ToList();
            OnPropertyChanged(nameof(PaymentMethods));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load payment methods for account payments");
        }
        await SearchAsync("", CancellationToken.None);
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        _ = DebouncedSearchAsync(value, _searchCts.Token);
    }

    private async Task DebouncedSearchAsync(string text, CancellationToken token)
    {
        try
        {
            await Task.Delay(200, token);
            await SearchAsync(text, token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SearchAsync(string text, CancellationToken token)
    {
        try
        {
            var found = await _customers.SearchAsync(text, 20, token);
            token.ThrowIfCancellationRequested();
            Results = found.Select(a => new CustomerResultViewModel(a)).ToList();
            _highlight = -1;
            MoveHighlight(1);
            Message = Results.Count == 0
                ? (text.Trim().Length == 0 ? "No customer accounts yet. An administrator can add them in Administration." : $"No customer matches “{text.Trim()}”.")
                : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PosException ex)
        {
            Message = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Customer search failed");
            Message = "Unable to search customers. Please try again.";
        }
    }

    private void MoveHighlight(int direction)
    {
        if (Results.Count == 0) return;
        if (_highlight >= 0 && _highlight < Results.Count) Results[_highlight].IsHighlighted = false;
        _highlight = Math.Clamp(_highlight + direction, 0, Results.Count - 1);
        Results[_highlight].IsHighlighted = true;
    }

    [RelayCommand]
    private void Choose(CustomerResultViewModel? result)
    {
        if (result is null) return;
        CustomerChosen?.Invoke(this, result.Account);
    }

    [RelayCommand]
    private void StartPayment(CustomerResultViewModel? result)
    {
        if (result is null) return;
        PayingCustomer = result.Account;
        PaymentAmountText = "";
        ErrorMessage = null;
        SelectPaymentMethod(PaymentMethods.FirstOrDefault(m => m.Method.Kind == PaymentKind.Cash) ?? PaymentMethods.FirstOrDefault());
    }

    [RelayCommand]
    private void SelectPaymentMethod(PaymentMethodOptionViewModel? option)
    {
        if (option is null || IsBusy) return;
        foreach (var m in PaymentMethods) m.IsSelected = m == option;
        PaymentMethod = option;
    }

    [RelayCommand]
    private void PayFullBalance()
    {
        if (PayingCustomer is { Balance: > 0 } c)
            PaymentAmountText = c.Balance.ToString("0.00", CultureInfo.CurrentCulture);
    }

    [RelayCommand]
    private void Keypad(string key)
    {
        if (IsBusy) return;
        var separator = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
        PaymentAmountText = key switch
        {
            "BKSP" => PaymentAmountText.Length > 0 ? PaymentAmountText[..^1] : "",
            "CLR" => "",
            "." when PaymentAmountText.Contains(separator) => PaymentAmountText,
            "." => (PaymentAmountText.Length == 0 ? "0" : PaymentAmountText) + separator,
            _ when PaymentAmountText.Length >= 9 => PaymentAmountText,
            _ => PaymentAmountText + key,
        };
    }

    [RelayCommand]
    private async Task ConfirmPaymentAsync()
    {
        if (!CanConfirmPayment || PayingCustomer is null || PaymentMethod is null) return;

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var result = await _customers.ReceivePaymentAsync(PayingCustomer.Id, PaymentAmount!.Value, PaymentMethod.Method.Id);
            PaymentRecorded?.Invoke(this, result);
        }
        catch (PosException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Account payment failed");
            ErrorMessage = "Unable to record the payment. Please try again.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Back()
    {
        if (IsBusy) return;
        if (IsPaymentMode)
            PayingCustomer = null;
        else
            Closed?.Invoke(this, EventArgs.Empty);
    }

    public bool HandleKey(Key key, bool textInputFocused)
    {
        switch (key)
        {
            case Key.Escape:
                Back();
                return true;
            case Key.Enter:
                if (IsPaymentMode) ConfirmPaymentCommand.Execute(null);
                else if (_highlight >= 0 && _highlight < Results.Count) Choose(Results[_highlight]);
                return true;
            case Key.Up when !IsPaymentMode:
                MoveHighlight(-1);
                return true;
            case Key.Down when !IsPaymentMode:
                MoveHighlight(1);
                return true;
            case >= Key.F1 and <= Key.F24:
                return true;
            default:
                return false;
        }
    }
}
