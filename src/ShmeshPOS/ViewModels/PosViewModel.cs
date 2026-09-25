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

public partial class CategoryFilterViewModel(int? id, string name, string? color) : ObservableObject
{
    public int? Id { get; } = id;
    public string Name { get; } = name;
    public string? Color { get; } = color;

    [ObservableProperty]
    private bool _isSelected;
}

public partial class PosViewModel : ObservableObject, IKeyboardHandler, IDisposable
{
    private readonly IProductService _products;
    private readonly IPaymentService _payments;
    private readonly ISaleService _sales;
    private readonly ISessionService _session;
    private readonly IAuthenticationService _auth;
    private readonly IDialogService _dialogs;
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
        _app = app;
        _logger = logger;

        StoreName = options.Value.StoreName;
        TerminalName = options.Value.TerminalName;
        CashierName = session.CurrentUser?.DisplayName ?? "";

        Categories =
        [
            new CategoryFilterViewModel(null, "All", null),
            .. products.Categories.Select(c => new CategoryFilterViewModel(c.Id, c.Name, c.Color)),
        ];
        SelectCategory(Categories[0]);

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
    public IReadOnlyList<CategoryFilterViewModel> Categories { get; }

    [ObservableProperty]
    private CategoryFilterViewModel? _selectedCategory;

    [ObservableProperty]
    private string _searchText = "";

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

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsSearching));
        RefreshProducts();
    }

    private void RefreshProducts() =>
        VisibleProducts = IsSearching ? _products.Search(SearchText) : _products.Search(null, SelectedCategory?.Id);

    // ---- Catalog and cart ------------------------------------------------------------------

    [RelayCommand]
    private void SelectCategory(CategoryFilterViewModel? category)
    {
        if (category is null) return;
        foreach (var c in Categories) c.IsSelected = c == category;
        SelectedCategory = category;
        SearchText = "";
        RefreshProducts();
        FocusSearch();
    }

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

        var payment = new PaymentViewModel(Cart.ToSaleLines(), Cart.Total, methods, _sales, _logger);
        payment.Cancelled += (_, _) =>
        {
            Payment = null;
            FocusSearch();
        };
        payment.Completed += async (_, sale) =>
        {
            Payment = null;
            Cart.Clear();
            SearchText = "";
            StatusMessage = $"Last sale {sale.ReceiptNumber}: {sale.Total:C}";
            await ShowSaleCompleteAsync(sale);
            FocusSearch();
        };
        Payment = payment;
    }

    private Task ShowSaleCompleteAsync(CompletedSale sale)
    {
        var message = sale.ChangeGiven is { } change
            ? $"CHANGE DUE:  {change:C}\n\nTotal {sale.Total:C}  ·  {sale.PaymentMethodName}\nReceipt {sale.ReceiptNumber}"
            : $"Total {sale.Total:C}  ·  {sale.PaymentMethodName}\nReceipt {sale.ReceiptNumber}";
        return _dialogs.ShowMessageAsync("Sale Complete", message, "Next Customer");
    }

    // ---- F3 menu ----------------------------------------------------------------------------

    [RelayCommand]
    private void OpenMenu()
    {
        if (Payment is not null || QuantityEntry is not null) return;

        var items = new List<MenuItemViewModel>
        {
            new("1", "New Sale", NewSaleAsync),
            new("2", "Hold Sale", () => ComingSoonAsync("Hold Sale")),
            new("3", "Retrieve Sale", () => ComingSoonAsync("Retrieve Sale")),
            new("4", "Product Lookup", () => { SearchText = ""; FocusSearch(); return Task.CompletedTask; }),
            new("5", "Open Drawer", () => ComingSoonAsync("Open Drawer")),
            new("6", "Returns", () => ComingSoonAsync("Returns")),
            new("7", "Reports", () => ComingSoonAsync("Reports"), _session.HasPermission(Permission.ViewReports)),
            new("8", "Settings", () => ComingSoonAsync("Settings and product management"), _session.HasPermission(Permission.ManageSettings)),
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
