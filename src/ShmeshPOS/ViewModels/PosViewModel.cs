using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShmeshPOS.Infrastructure;
using ShmeshPOS.Models;
using ShmeshPOS.Security;
using ShmeshPOS.Services;

namespace ShmeshPOS.ViewModels;

public partial class PosViewModel : ObservableObject, IKeyboardHandler, IDisposable
{
    private readonly IProductService _products;
    private readonly IPaymentService _payments;
    private readonly ISaleService _sales;
    private readonly ISessionService _session;
    private readonly IAuthenticationService _auth;
    private readonly IDialogService _dialogs;
    private readonly ICustomerService _customers;
    private readonly IReceiptService _receipts;
    private readonly IHeldSaleService _held;
    private readonly IRefundService _refunds;
    private readonly IShiftService _shifts;
    private readonly IApplicationControl _app;
    private readonly ILogger<PosViewModel> _logger;
    private readonly DispatcherTimer _clockTimer;

    public PosViewModel(
        IProductService products,
        IPaymentService payments,
        ISaleService sales,
        ISessionService session,
        IAuthenticationService auth,
        IDialogService dialogs,
        ICustomerService customers,
        IReceiptService receipts,
        IHeldSaleService held,
        IRefundService refunds,
        IShiftService shifts,
        IApplicationControl app,
        IOptions<PosOptions> options,
        ILogger<PosViewModel> logger)
    {
        _products = products;
        _payments = payments;
        _sales = sales;
        _session = session;
        _auth = auth;
        _dialogs = dialogs;
        _customers = customers;
        _receipts = receipts;
        _held = held;
        _refunds = refunds;
        _shifts = shifts;
        _app = app;
        _logger = logger;

        StoreName = options.Value.StoreName;
        TerminalName = options.Value.TerminalName;
        CashierName = session.CurrentUser?.DisplayName ?? "";

        ReloadCatalog();

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _clockTimer.Tick += (_, _) => OnPropertyChanged(nameof(Now));
        _clockTimer.Start();
    }

    public string StoreName { get; }
    public string TerminalName { get; }
    public string CashierName { get; }
    public DateTime Now => DateTime.Now;

    public CartViewModel Cart { get; } = new();
    public MenuViewModel Menu { get; } = new();
    /// <summary>The products chosen in Administration to appear as buttons on the main screen (up to 10).</summary>
    [ObservableProperty]
    private IReadOnlyList<Product> _mainScreenProducts = [];

    [ObservableProperty]
    private string _searchText = "";

    /// <summary>Search results while the cashier is typing in the search box.</summary>
    [ObservableProperty]
    private IReadOnlyList<Product> _visibleProducts = [];

    [ObservableProperty]
    private PaymentViewModel? _payment;

    [ObservableProperty]
    private QuantityEntryViewModel? _quantityEntry;

    [ObservableProperty]
    private string? _statusMessage;

    public bool IsSearching => SearchText.Trim().Length > 0;

    /// <summary>The view moves keyboard focus to the search box when this fires.</summary>
    public event EventHandler? FocusSearchRequested;
    public event EventHandler? LogoutRequested;
    public event EventHandler? AdministrationRequested;
    public event EventHandler? ReportsRequested;

    public bool HasMainScreenProducts => MainScreenProducts.Count > 0;

    /// <summary>Refreshes the main-screen buttons and any search results from the (reloaded) catalog.</summary>
    public void ReloadCatalog()
    {
        MainScreenProducts = _products.MainScreenProducts;
        OnPropertyChanged(nameof(HasMainScreenProducts));
        RefreshProducts();
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsSearching));
        RefreshProducts();
    }

    private void RefreshProducts() =>
        VisibleProducts = IsSearching ? _products.Search(SearchText) : [];

    // ---- Catalog and cart ------------------------------------------------------------------

    [RelayCommand]
    private void AddProduct(Product? product)
    {
        if (product is null) return;
        var line = Cart.Add(product);
        StatusMessage = null;
        _logger.LogDebug("Added {ProductId} to cart, qty now {Quantity}", product.Id, line.Quantity);
        FocusSearch();
    }

    /// <summary>ENTER in the search box: exact code/SKU/barcode first (scanner input), otherwise the top result.</summary>
    [RelayCommand]
    private void AddFromSearch()
    {
        var text = SearchText.Trim();
        if (text.Length == 0) return;

        var product = _products.FindByLookupKey(text) ?? VisibleProducts.FirstOrDefault();
        if (product is null)
        {
            StatusMessage = $"No product matches “{text}”.";
            return;
        }

        AddProduct(product);
        SearchText = "";
    }

    [RelayCommand]
    private void SelectLine(CartLineViewModel? line)
    {
        if (line is not null) Cart.SelectedLine = line;
        FocusSearch();
    }

    [RelayCommand]
    private void IncreaseQuantity(CartLineViewModel? line)
    {
        if ((line ?? Cart.SelectedLine) is { } l) Cart.Increase(l);
        FocusSearch();
    }

    [RelayCommand]
    private void DecreaseQuantity(CartLineViewModel? line)
    {
        if ((line ?? Cart.SelectedLine) is { } l) Cart.Decrease(l);
        FocusSearch();
    }

    [RelayCommand]
    private void RemoveLine(CartLineViewModel? line)
    {
        if ((line ?? Cart.SelectedLine) is { } l) Cart.Remove(l);
        FocusSearch();
    }

    [RelayCommand]
    private void ChangeQuantity()
    {
        if (Cart.SelectedLine is not { } line) return;

        var entry = new QuantityEntryViewModel(line);
        entry.Closed += (_, quantity) =>
        {
            QuantityEntry = null;
            if (quantity is { } q) Cart.SetQuantity(line, q);
            FocusSearch();
        };
        QuantityEntry = entry;
    }

    [RelayCommand]
    private async Task ClearSaleAsync()
    {
        if (Cart.IsEmpty) return;
        if (await _dialogs.ConfirmAsync("Cancel Sale", "Remove all items and cancel this sale?", "Cancel Sale", "Keep Sale"))
        {
            _logger.LogInformation("Sale cancelled with {ItemCount} items, total {Total}", Cart.ItemCount, Cart.Total);
            Cart.Clear();
            Customer = null;
        }
        FocusSearch();
    }

    [RelayCommand]
    private void FocusSearch() => FocusSearchRequested?.Invoke(this, EventArgs.Empty);

    // ---- F8 payment -------------------------------------------------------------------------

    [RelayCommand]
    private async Task OpenPaymentAsync()
    {
        if (Payment is not null) return;
        if (Cart.IsEmpty)
        {
            StatusMessage = "Add items before taking payment.";
            return;
        }
        if (!_session.HasPermission(Permission.TakePayment))
        {
            await _dialogs.ShowMessageAsync("Not Allowed", "You are not allowed to take payments.");
            return;
        }
        if (!await EnsureShiftOpenAsync())
            return;

        IReadOnlyList<PaymentMethod> methods;
        try
        {
            methods = await _payments.GetPaymentMethodsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load payment methods");
            await _dialogs.ShowMessageAsync("Payment Unavailable", "Unable to load payment methods.\nPlease try again.");
            return;
        }

        if (methods.Count == 0)
        {
            await _dialogs.ShowMessageAsync("Payment Unavailable", "No payment methods are set up.");
            return;
        }

        var payment = new PaymentViewModel(Cart.ToSaleLines(), Cart.Total, methods, Customer, _sales, _dialogs, _logger, Cart.SaleDiscount);
        payment.Cancelled += (_, _) =>
        {
            Payment = null;
            FocusSearch();
        };
        payment.Completed += async (_, sale) =>
        {
            Payment = null;
            Cart.Clear();
            Customer = null;
            SearchText = "";
            StatusMessage = $"Last sale {sale.ReceiptNumber}: {sale.Total:C}";
            await ShowSaleCompleteAsync(sale);
            FocusSearch();
        };
        Payment = payment;
    }

    private async Task ShowSaleCompleteAsync(CompletedSale sale)
    {
        if (sale.TookCash)
            await TryOpenDrawerAsync($"cash sale {sale.ReceiptNumber}");

        var autoPrinted = false;
        try
        {
            if (await _receipts.IsAutoPrintAsync())
            {
                await _receipts.PrintSaleAsync(sale.SaleId);
                autoPrinted = true;
            }
        }
        catch (Exception ex)
        {
            // The sale is saved; a printer problem must not block the next customer.
            _logger.LogError(ex, "Automatic receipt printing failed for sale {SaleId}", sale.SaleId);
        }

        var message = sale.ChangeGiven is { } change
            ? $"CHANGE DUE:  {change:C}\n\nTotal {sale.Total:C}  ·  {sale.PaymentMethodName}\nReceipt {sale.ReceiptNumber}"
            : $"Total {sale.Total:C}  ·  {sale.PaymentMethodName}\nReceipt {sale.ReceiptNumber}";
        if (sale.Discount > 0)
            message += $"\nDiscounts: {sale.Discount:C}";
        if (sale.CustomerBalance is { } balance)
            message += $"\n\nCharged to {sale.CustomerName}.\nNew account balance: {balance:C}";
        if (await _dialogs.ShowMessageWithActionAsync("Sale Complete", message, "Next Customer", autoPrinted ? "Print Again" : "Print Receipt"))
            await PrintReceiptAsync(sale.SaleId);
    }

    private async Task PrintReceiptAsync(long saleId)
    {
        try
        {
            await _receipts.PrintSaleAsync(saleId);
            StatusMessage = "Receipt printed.";
        }
        catch (PosException ex)
        {
            await _dialogs.ShowMessageAsync("Receipt Not Printed", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Printing receipt for sale {SaleId} failed", saleId);
            await _dialogs.ShowMessageAsync("Receipt Not Printed", "The receipt could not be printed. Check the printer and try again.");
        }
    }

    // ---- F4 customer ------------------------------------------------------------------------

    /// <summary>Customer attached to the current sale, if any. Required to charge to an account.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCustomer))]
    private CustomerAccount? _customer;

    [ObservableProperty]
    private CustomerLookupViewModel? _customerLookup;

    public bool HasCustomer => Customer is not null;
    public bool CanUseCustomerAccounts => _session.HasPermission(Permission.UseCustomerAccounts);

    [RelayCommand]
    private async Task OpenCustomerLookupAsync()
    {
        if (CustomerLookup is not null || Payment is not null || QuantityEntry is not null) return;
        if (!CanUseCustomerAccounts)
        {
            await _dialogs.ShowMessageAsync("Not Allowed", "You are not allowed to use customer accounts.");
            return;
        }

        var lookup = new CustomerLookupViewModel(_customers, _payments, _logger);
        lookup.Closed += (_, _) => CloseCustomerLookup();
        lookup.CustomerChosen += (_, account) =>
        {
            Customer = account;
            _logger.LogInformation("Customer {CustomerId} attached to sale", account.Id);
            CloseCustomerLookup();
        };
        lookup.PaymentRecorded += async (_, result) =>
        {
            if (Customer?.Id == result.Account.Id) Customer = result.Account;
            CloseCustomerLookup();
            StatusMessage = $"Account payment {result.Amount:C} from {result.Account.Name}";
            await _dialogs.ShowMessageAsync("Payment Received",
                $"{result.Amount:C} paid by {result.PaymentMethodName}\nfrom {result.Account.Name} (account {result.Account.AccountNumber}).\n\nNew balance: {result.Account.Balance:C}",
                "Done");
            FocusSearch();
        };
        CustomerLookup = lookup;
        await lookup.InitializeAsync();
    }

    private void CloseCustomerLookup()
    {
        CustomerLookup = null;
        FocusSearch();
    }

    [RelayCommand]
    private void RemoveCustomer()
    {
        Customer = null;
        FocusSearch();
    }

    // ---- F3 menu ----------------------------------------------------------------------------

    [RelayCommand]
    private void OpenMenu()
    {
        if (Payment is not null || QuantityEntry is not null || CustomerLookup is not null || HeldSales is not null || Returns is not null
            || DiscountEntry is not null || Shift is not null) return;

        var items = new List<MenuItemViewModel>
        {
            new("1", "New Sale", NewSaleAsync),
            new("2", "Hold Sale", HoldSaleAsync, _session.HasPermission(Permission.HoldSales)),
            new("3", "Retrieve Sale", OpenHeldSalesAsync, _session.HasPermission(Permission.HoldSales)),
            new("4", CurrentShift is null ? "Open Shift" : "Close Shift", CurrentShift is null ? OpenShiftAsync : CloseShiftAsync,
                _session.HasPermission(Permission.RunShifts)),
            new("5", "Open Drawer", OpenDrawerFromMenuAsync, _session.HasPermission(Permission.OpenDrawer)),
            new("6", "Returns", OpenReturnsAsync, _session.HasPermission(Permission.ProcessReturns)),
            new("7", "Reports", () => { ReportsRequested?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; },
                _session.HasPermission(Permission.ViewReports)),
            new("8", "Administration", OpenAdministrationAsync,
                _session.HasPermission(Permission.ManageProducts) || _session.HasPermission(Permission.ManageUsers)),
            new("9", "Logout", LogoutAsync),
        };
        if (_session.HasPermission(Permission.ExitApplication))
            items.Add(new MenuItemViewModel("0", "Exit Shmesh POS", ExitAsync));

        Menu.Open(items);
    }

    private async Task NewSaleAsync()
    {
        if (!Cart.IsEmpty)
            await ClearSaleAsync();
        FocusSearch();
    }

    private Task OpenAdministrationAsync()
    {
        AdministrationRequested?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    // ---- Hold / retrieve ----------------------------------------------------------------------

    [ObservableProperty]
    private HeldSalesViewModel? _heldSales;

    private async Task HoldSaleAsync()
    {
        if (Cart.IsEmpty)
        {
            await _dialogs.ShowMessageAsync("Hold Sale", "There is nothing to hold. Add items first.");
            FocusSearch();
            return;
        }

        var label = await _dialogs.PromptAsync("Hold Sale",
            "Add a note to find it again, e.g. a name or table number (optional).", "Hold Sale");
        if (label is null)
        {
            FocusSearch();
            return;
        }

        try
        {
            await _held.HoldAsync(Cart.ToSaleLines(), Customer?.Id, label, Cart.SaleDiscount);
            StatusMessage = label.Length > 0 ? $"Sale held: “{label}” ({Cart.Total:C})" : $"Sale held ({Cart.Total:C})";
            Cart.Clear();
            Customer = null;
        }
        catch (PosException ex)
        {
            await _dialogs.ShowMessageAsync("Hold Sale", ex.Message);
        }
        FocusSearch();
    }

    private async Task OpenHeldSalesAsync()
    {
        if (!Cart.IsEmpty)
        {
            await _dialogs.ShowMessageAsync("Retrieve Sale", "Finish or hold the current sale first.");
            FocusSearch();
            return;
        }

        var list = new HeldSalesViewModel(_held, _dialogs, _logger);
        list.Closed += (_, _) =>
        {
            HeldSales = null;
            FocusSearch();
        };
        list.Retrieved += async (_, content) =>
        {
            HeldSales = null;
            foreach (var line in content.Lines)
            {
                var added = Cart.Add(new Product { Id = line.ProductId, Name = line.ProductName, Price = line.UnitPrice, TaxRate = line.TaxRate }, line.Quantity);
                Cart.SetDiscount(added, line.Discount);
            }
            Cart.SaleDiscount = content.SaleDiscount;
            if (Cart.HasDiscounts && !CanApplyDiscounts)
            {
                // Discounts need an administrator; a cashier finishing the sale charges the full prices.
                Cart.ClearDiscounts();
                await _dialogs.ShowMessageAsync("Discounts Removed",
                    "This held sale had discounts. Only an administrator can give discounts, so they were removed.");
            }
            if (content.CustomerId is { } customerId)
            {
                try
                {
                    Customer = await _customers.GetAccountAsync(customerId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not reload customer {CustomerId} for a retrieved sale", customerId);
                }
            }
            StatusMessage = content.Label is { Length: > 0 } label ? $"Retrieved “{label}”" : "Held sale retrieved";
            FocusSearch();
        };
        HeldSales = list;
        await list.InitializeAsync();
    }

    // ---- Returns --------------------------------------------------------------------------------

    [ObservableProperty]
    private ReturnsViewModel? _returns;

    private Task OpenReturnsAsync()
    {
        var returns = new ReturnsViewModel(_refunds, _payments, _logger);
        returns.Closed += (_, _) =>
        {
            Returns = null;
            FocusSearch();
        };
        returns.Completed += async (_, refund) =>
        {
            Returns = null;
            StatusMessage = $"Refund {refund.RefundNumber}: {refund.Total:C}";
            if (refund.Kind == PaymentKind.Cash)
                await TryOpenDrawerAsync($"cash refund {refund.RefundNumber}");

            var message = refund.Kind switch
            {
                PaymentKind.Cash => $"GIVE BACK:  {refund.Total:C} cash",
                PaymentKind.Account => $"{refund.Total:C} credited to {refund.CustomerName}'s account.\nNew balance: {refund.CustomerBalance:C}",
                _ => $"Refund {refund.Total:C} on the {refund.PaymentMethodName} terminal.",
            };
            if (await _dialogs.ShowMessageWithActionAsync("Refund Complete", $"{message}\n\nRefund {refund.RefundNumber}", "Done", "Print Refund Slip"))
            {
                try
                {
                    await _receipts.PrintRefundAsync(refund.RefundId);
                }
                catch (Exception ex) when (ex is PosException or InvalidOperationException)
                {
                    await _dialogs.ShowMessageAsync("Slip Not Printed", ex.Message);
                }
            }
            FocusSearch();
        };
        Returns = returns;
        return Task.CompletedTask;
    }

    // ---- Discounts ------------------------------------------------------------------------------

    [ObservableProperty]
    private DiscountViewModel? _discountEntry;

    public bool CanApplyDiscounts => _session.HasPermission(Permission.ApplyDiscounts);

    /// <summary>F6 / Discount button: a discount on the selected item or the whole sale.</summary>
    [RelayCommand]
    private async Task OpenDiscountAsync()
    {
        if (DiscountEntry is not null || Payment is not null || QuantityEntry is not null || Menu.IsOpen) return;
        if (Cart.IsEmpty)
        {
            StatusMessage = "Add items before giving a discount.";
            return;
        }
        if (!CanApplyDiscounts)
        {
            await _dialogs.ShowMessageAsync("Not Allowed", "Only an administrator can give discounts. Ask a manager to log in.");
            FocusSearch();
            return;
        }

        var entry = new DiscountViewModel(Cart);
        entry.Closed += (_, choice) =>
        {
            DiscountEntry = null;
            if (choice is not null)
            {
                if (choice.Line is { } line)
                    Cart.SetDiscount(line, choice.Discount);
                else
                    Cart.SaleDiscount = choice.Discount;
                StatusMessage = choice.Discount is { } d
                    ? $"{d.Label} {choice.Line?.Name ?? "the whole sale"}"
                    : "Discount removed.";
                _logger.LogInformation("Discount {Discount} set on {Target} by user {UserId}",
                    choice.Discount?.Label ?? "none", choice.Line?.Name ?? "whole sale", _session.CurrentUser?.Id);
            }
            FocusSearch();
        };
        DiscountEntry = entry;
    }

    // ---- Shifts ---------------------------------------------------------------------------------

    /// <summary>The shift open on this register; payments need one so the drawer can be counted at closing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShiftText))]
    private ShiftInfo? _currentShift;

    [ObservableProperty]
    private ShiftViewModel? _shift;

    public string ShiftText => CurrentShift is { } s ? $"Shift since {s.OpenedAt:h:mm tt}" : "No shift open";

    /// <summary>After login: offers to open a shift if this register has none.</summary>
    public async Task CheckShiftAsync()
    {
        try
        {
            CurrentShift = await _shifts.GetOpenShiftAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not check for an open shift");
            return;
        }
        if (CurrentShift is null && _session.HasPermission(Permission.RunShifts))
            await OpenShiftAsync();
    }

    /// <summary>True when a shift is open; otherwise starts opening one (the cashier presses Pay again afterwards).</summary>
    private async Task<bool> EnsureShiftOpenAsync()
    {
        if (CurrentShift is not null) return true;
        try
        {
            CurrentShift = await _shifts.GetOpenShiftAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not check for an open shift");
            await _dialogs.ShowMessageAsync("Payment Unavailable", "Unable to check the shift. Check the connection and try again.");
            return false;
        }
        if (CurrentShift is not null) return true;

        if (!_session.HasPermission(Permission.RunShifts))
        {
            await _dialogs.ShowMessageAsync("No Shift Open", "A shift must be opened on this register before taking payment.\nAsk a manager.");
            return false;
        }
        await _dialogs.ShowMessageAsync("Open Shift First", "Count the opening float into the drawer to start the shift, then take the payment.");
        await OpenShiftAsync();
        return false;
    }

    private async Task OpenShiftAsync()
    {
        if (Shift is not null) return;
        var shift = new ShiftViewModel(ShiftStep.Open, _shifts, _receipts, _logger);
        shift.Opened += (_, info) =>
        {
            CurrentShift = info;
            StatusMessage = $"Shift opened with a {info.OpeningFloat:C} float.";
        };
        shift.Closed += (_, _) =>
        {
            Shift = null;
            FocusSearch();
        };
        Shift = shift;
        await TryOpenDrawerAsync("opening float");
    }

    private async Task CloseShiftAsync()
    {
        if (Shift is not null || CurrentShift is null) return;
        if (!Cart.IsEmpty)
        {
            await _dialogs.ShowMessageAsync("Close Shift", "Finish or hold the current sale first.");
            FocusSearch();
            return;
        }

        var shift = new ShiftViewModel(ShiftStep.Close, _shifts, _receipts, _logger, CurrentShift);
        shift.ShiftClosed += (_, report) =>
        {
            CurrentShift = null;
            StatusMessage = report.Difference switch
            {
                > 0 => $"Shift closed: drawer over by {report.Difference:C}.",
                < 0 => $"Shift closed: drawer short by {-report.Difference:C}.",
                _ => "Shift closed: drawer balanced.",
            };
        };
        shift.Closed += (_, _) =>
        {
            Shift = null;
            FocusSearch();
        };
        Shift = shift;
        await TryOpenDrawerAsync("cash count at shift close");
    }


    // ---- Cash drawer ----------------------------------------------------------------------------

    private async Task OpenDrawerFromMenuAsync()
    {
        try
        {
            if (!await _receipts.OpenDrawerAsync("no sale (F3 → Open Drawer)"))
                await _dialogs.ShowMessageAsync("Open Drawer",
                    "No cash drawer is set up on this register. An administrator can turn it on in Administration → Settings.");
        }
        catch (PosException ex)
        {
            await _dialogs.ShowMessageAsync("Open Drawer", ex.Message);
        }
        FocusSearch();
    }

    /// <summary>Opens the drawer for cash in/out. A drawer problem never blocks the sale; it is logged.</summary>
    private async Task TryOpenDrawerAsync(string reason)
    {
        try
        {
            await _receipts.OpenDrawerAsync(reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cash drawer did not open ({Reason})", reason);
            StatusMessage = "The cash drawer did not open. Check the printer and drawer cable.";
        }
    }

    private async Task ComingSoonAsync(string feature)
    {
        await _dialogs.ShowMessageAsync(feature, $"{feature} is not available yet.");
        FocusSearch();
    }

    private async Task LogoutAsync()
    {
        if (!Cart.IsEmpty &&
            !await _dialogs.ConfirmAsync("Logout", "The current sale will be cancelled.\nLog out anyway?", "Logout", "Stay"))
        {
            FocusSearch();
            return;
        }

        _auth.Logout();
        LogoutRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task ExitAsync()
    {
        if (!_session.HasPermission(Permission.ExitApplication)) return;
        if (!await _dialogs.ConfirmAsync("Exit", "Close Shmesh POS on this register?", "Exit", "Cancel"))
        {
            FocusSearch();
            return;
        }

        _logger.LogInformation("Application exit requested by user {UserId}", _session.CurrentUser?.Id);
        _app.ExitApplication();
    }

    // ---- Keyboard ---------------------------------------------------------------------------

    public bool HandleKey(Key key, bool textInputFocused)
    {
        if (QuantityEntry is not null) return QuantityEntry.HandleKey(key, textInputFocused);
        if (DiscountEntry is not null) return DiscountEntry.HandleKey(key, textInputFocused);
        if (Shift is not null) return Shift.HandleKey(key, textInputFocused);
        if (CustomerLookup is not null) return CustomerLookup.HandleKey(key, textInputFocused);
        if (HeldSales is not null) return HeldSales.HandleKey(key, textInputFocused);
        if (Returns is not null) return Returns.HandleKey(key, textInputFocused);
        if (Payment is not null) return Payment.HandleKey(key, textInputFocused);
        if (Menu.IsOpen) return Menu.HandleKey(key, textInputFocused);

        // The search box normally has focus; editing keys only act on the cart when it is empty.
        var editingText = textInputFocused && SearchText.Length > 0;

        switch (key)
        {
            case Key.F2:
                SearchText = "";
                FocusSearch();
                return true;
            case Key.F3:
                OpenMenu();
                return true;
            case Key.F4:
                OpenCustomerLookupCommand.Execute(null);
                return true;
            case Key.F6:
                OpenDiscountCommand.Execute(null);
                return true;
            case Key.F8:
                OpenPaymentCommand.Execute(null);
                return true;
            case Key.Enter when IsSearching:
                AddFromSearch();
                return true;
            case Key.Escape when IsSearching:
                SearchText = "";
                return true;
            case Key.Delete when !editingText:
                RemoveLine(null);
                return true;
            case Key.Add or Key.OemPlus when !editingText:
                IncreaseQuantity(null);
                return true;
            case Key.Subtract or Key.OemMinus when !editingText:
                DecreaseQuantity(null);
                return true;
            case Key.Up when !Cart.IsEmpty:
                MoveSelection(-1);
                return true;
            case Key.Down when !Cart.IsEmpty:
                MoveSelection(1);
                return true;
            default:
                return false;
        }
    }

    private void MoveSelection(int direction)
    {
        var index = Cart.SelectedLine is null ? -1 : Cart.Lines.IndexOf(Cart.SelectedLine);
        index = Math.Clamp(index + direction, 0, Cart.Lines.Count - 1);
        Cart.SelectedLine = Cart.Lines[index];
    }

    public void Dispose() => _clockTimer.Stop();
}
