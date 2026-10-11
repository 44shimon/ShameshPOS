using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ShmeshPOS.Infrastructure;
using ShmeshPOS.Models;
using ShmeshPOS.Services;

namespace ShmeshPOS.ViewModels;

public partial class ReturnLineViewModel(ReturnableLine line, Action changed) : ObservableObject
{
    public ReturnableLine Line { get; } = line;
    public bool CanReturn => Line.Remaining > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Amount))]
    private int _quantity;

    /// <summary>Refund for this line including tax, exactly as the refund will be recorded.</summary>
    public decimal Amount
    {
        get
        {
            var (total, tax) = IRefundService.AmountsFor(Line, Quantity);
            return total + tax;
        }
    }

    partial void OnQuantityChanged(int value) => changed();

    [RelayCommand]
    private void Increase()
    {
        if (Quantity < Line.Remaining) Quantity++;
    }

    [RelayCommand]
    private void Decrease()
    {
        if (Quantity > 0) Quantity--;
    }
}

/// <summary>
/// F3 → Returns: find the sale by receipt number, choose what comes back, choose how the money goes back.
/// </summary>
public partial class ReturnsViewModel(IRefundService refunds, IPaymentService payments, ILogger logger)
    : ObservableObject, IKeyboardHandler
{
    [ObservableProperty] private string _receiptText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSale), nameof(IsFindStage), nameof(HasPreviousRefunds))]
    private ReturnableSale? _sale;

    [ObservableProperty] private IReadOnlyList<ReturnLineViewModel> _lines = [];
    [ObservableProperty] private IReadOnlyList<PaymentMethodOptionViewModel> _methods = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanComplete))]
    private PaymentMethodOptionViewModel? _method;

    [ObservableProperty] private string _reason = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanComplete))]
    private decimal _refundTotal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanComplete))]
    private bool _isBusy;

    [ObservableProperty] private string? _errorMessage;

    public bool HasSale => Sale is not null;
    public bool IsFindStage => Sale is null;
    public bool HasPreviousRefunds => Sale is { AlreadyRefunded: > 0 };
    public bool CanComplete => !IsBusy && Method is not null && RefundTotal > 0;
    public string OriginalPayments => Sale is null ? "" : string.Join(", ", Sale.Payments.Select(p => $"{p.Name} {p.Amount:C}"));

    public event EventHandler<CompletedRefund>? Completed;
    public event EventHandler? Closed;

    [RelayCommand]
    private async Task FindAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var sale = await refunds.FindSaleAsync(ReceiptText);
            var all = await payments.GetPaymentMethodsAsync();

            // Offer the original payment methods first, then the rest; accounts only if the sale had a customer.
            var originalIds = sale.Payments.Select(p => p.PaymentMethodId).ToList();
            Methods = all
                .Where(m => m.Kind != PaymentKind.Account || sale.CustomerId is not null)
                .OrderBy(m => originalIds.Contains(m.Id) ? 0 : 1).ThenBy(m => m.SortOrder)
                .Select(m => new PaymentMethodOptionViewModel(m))
                .ToList();
            SelectMethod(Methods.FirstOrDefault());

            Lines = sale.Lines.Select(l => new ReturnLineViewModel(l, Recalculate)).ToList();
            Sale = sale;
            OnPropertyChanged(nameof(OriginalPayments));
            Recalculate();
            if (sale.Lines.All(l => l.Remaining == 0))
                ErrorMessage = "Everything on this receipt has already been returned.";
        }
        catch (PosException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Finding sale for return failed");
            ErrorMessage = "Unable to find that sale. Please try again.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ReturnAll()
    {
        foreach (var line in Lines) line.Quantity = line.Line.Remaining;
    }

    [RelayCommand]
    private void SelectMethod(PaymentMethodOptionViewModel? option)
    {
        if (option is null || IsBusy) return;
        foreach (var m in Methods) m.IsSelected = m == option;
        Method = option;
    }

    [RelayCommand]
    private async Task CompleteAsync()
    {
        if (!CanComplete || Sale is null || Method is null) return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var request = new RefundRequest(Sale.SaleId,
                Lines.Where(l => l.Quantity > 0).Select(l => new RefundLineRequest(l.Line.SaleItemId, l.Quantity)).ToList(),
                Method.Method.Id, Reason);
            Completed?.Invoke(this, await refunds.RefundAsync(request));
        }
        catch (PosException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Refund failed");
            ErrorMessage = "Unable to complete the refund. Please try again.";
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
        if (Sale is not null)
        {
            Sale = null;
            Lines = [];
            ErrorMessage = null;
            RefundTotal = 0;
        }
        else
        {
            Closed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Recalculate() => RefundTotal = Lines.Sum(l => l.Amount);

    public bool HandleKey(Key key, bool textInputFocused)
    {
        switch (key)
        {
            case Key.Escape:
                Back();
                return true;
            case Key.Enter when IsFindStage:
                FindCommand.Execute(null);
                return true;
            case >= Key.F1 and <= Key.F24:
                return true;
            default:
                // Refunds are completed with the button only, never by a stray Enter.
                return key == Key.Enter;
        }
    }
}
