using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ShmeshPOS.Models;
using ShmeshPOS.Infrastructure;
using ShmeshPOS.Security;
using ShmeshPOS.Services;
using ShmeshPOS.Services.Import;

namespace ShmeshPOS.ViewModels.Admin;

public partial class ProductEditorViewModel : EditorViewModel
{
    private static readonly HashSet<string> UntrackedProperties =
    [
        nameof(History), nameof(HistoryMessage), nameof(StockAmountText), nameof(StockNote),
    ];

    public ProductEditorViewModel(Product? product, IReadOnlyList<Category> categories, bool canManageStock = false)
    {
        Id = product?.Id ?? 0;
        Heading = product?.Name ?? "New Product";
        Categories = categories;
        _name = product?.Name ?? "";
        _category = categories.FirstOrDefault(c => c.Id == product?.CategoryId) ?? categories.FirstOrDefault(c => c.IsActive);
        _code = product?.Code ?? "";
        _sku = product?.Sku ?? "";
        _barcode = product?.Barcode ?? "";
        _priceText = product?.Price.ToString("0.00", CultureInfo.CurrentCulture) ?? "";
        _taxPercentText = ((product?.TaxRate ?? 0m) * 100m).ToString("0.###", CultureInfo.CurrentCulture);
        _sortOrderText = (product?.SortOrder ?? 0).ToString(CultureInfo.CurrentCulture);
        _isActive = product?.IsActive ?? true;
        _showOnMainScreen = product?.ShowOnMainScreen ?? false;
        _trackStock = product?.TrackStock ?? false;
        _reorderLevelText = product?.ReorderLevel?.ToString(CultureInfo.CurrentCulture) ?? "";
        SavedTrackStock = product?.TrackStock ?? false;
        StockQuantity = product?.StockQuantity ?? 0;
        IsLowOnStock = product?.IsLowOnStock ?? false;
        CanManageStock = canManageStock;
        StartTracking();
    }

    public int Id { get; }
    public override bool IsNew => Id == 0;
    public string Heading { get; }
    public IReadOnlyList<Category> Categories { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private Category? _category;
    [ObservableProperty] private string _code;
    [ObservableProperty] private string _sku;
    [ObservableProperty] private string _barcode;
    [ObservableProperty] private string _priceText;
    [ObservableProperty] private string _taxPercentText;
    [ObservableProperty] private string _sortOrderText;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _showOnMainScreen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStockPanel), nameof(ShowSaveToTrackHint))]
    private bool _trackStock;

    [ObservableProperty] private string _reorderLevelText;

    // ---- Stock on hand, deliveries, counts and history (not part of the form's unsaved-change tracking) ----

    /// <summary>Whether stock is tracked in the database; deliveries and counts need it saved first.</summary>
    public bool SavedTrackStock { get; }
    public int StockQuantity { get; }
    public bool IsLowOnStock { get; }
    public bool CanManageStock { get; }

    /// <summary>Quantity, receive/count and history: only for a saved product that already tracks stock.</summary>
    public bool ShowStockPanel => !IsNew && SavedTrackStock && TrackStock;

    public bool ShowSaveToTrackHint => TrackStock && !ShowStockPanel;

    [ObservableProperty] private IReadOnlyList<StockMovement> _history = [];
    [ObservableProperty] private string? _historyMessage;
    [ObservableProperty] private string _stockAmountText = "";
    [ObservableProperty] private string _stockNote = "";

    protected override bool IsTracked(string? propertyName) =>
        propertyName is null || !UntrackedProperties.Contains(propertyName);

    public (int Quantity, string Note) ToStockEntry()
    {
        if (string.IsNullOrWhiteSpace(StockAmountText)) throw new PosException("Enter a quantity.");
        return (ParseInt(StockAmountText, "Enter the quantity as a whole number, e.g. 24."), StockNote);
    }

    public ProductInput ToInput()
    {
        var category = Category ?? throw new PosException("Choose a category.");
        if (string.IsNullOrWhiteSpace(PriceText)) throw new PosException("Enter a price.");
        var price = ParseDecimal(PriceText, "Enter the price as a number, e.g. 2.50.");
        var taxPercent = string.IsNullOrWhiteSpace(TaxPercentText) ? 0m : ParseDecimal(TaxPercentText, "Enter the tax rate as a percentage, e.g. 8.875.");
        var sortOrder = ParseInt(SortOrderText, "Sort order must be a whole number.");
        int? reorderLevel = TrackStock && !string.IsNullOrWhiteSpace(ReorderLevelText)
            ? ParseInt(ReorderLevelText, "The low-stock level must be a whole number, 0 or more.")
            : null;
        return new ProductInput(Id, Name, category.Id, Code, Sku, Barcode, price, taxPercent / 100m, sortOrder, IsActive, ShowOnMainScreen,
            TrackStock, reorderLevel);
    }
}

public partial class ProductsAdminViewModel(
    ICatalogAdminService catalog,
    IStockService stock,
    ISessionService session,
    IImportService importer,
    IFileDialogService files,
    IDialogService dialogs,
    ILogger<ProductsAdminViewModel> logger)
    : AdminSectionViewModel<Product, ProductEditorViewModel>(dialogs, logger)
{
    public override bool CanImport => true;

    protected override ImportViewModel CreateImport() => new(
        "Import Products",
        "Name (required) · Price (required for new items) · Category · Tax Rate · Code · SKU · Barcode · Active · Button Order.\n" +
        "Common headings also work, e.g. Item, Description, Retail, Dept, PLU, UPC. Existing products are matched by code, SKU, barcode or name and updated; missing categories are created.\n" +
        "Tip: in Excel, format barcode and code columns as Text so leading zeros (e.g. 0490…) are kept.",
        "product-import-template.csv",
        importer.ProductTemplate,
        table => importer.PlanProductsAsync(table),
        importer,
        files,
        Logger);

    private IReadOnlyList<Category> _categories = [];

    public override string Title => "Products";

    /// <summary>e.g. "7 of 10 on the main screen", shown above the list.</summary>
    public string MainScreenSummary { get; private set; } = "";

    /// <summary>e.g. "3 low on stock", shown above the list; empty when nothing is low.</summary>
    public string LowStockSummary { get; private set; } = "";
    public override string NewLabel => "New Product";

    protected override async Task<IReadOnlyList<Product>> FetchAsync()
    {
        _categories = await catalog.GetCategoriesAsync();
        var products = await catalog.GetProductsAsync();
        MainScreenSummary = $"{products.Count(p => p.ShowOnMainScreen)} of {CatalogAdminService.MaxMainScreenItems} on the main screen";
        var low = products.Count(p => p.IsActive && p.IsLowOnStock);
        LowStockSummary = low == 0 ? "" : $"{low} low on stock — type “low” in the search box to list them";
        OnPropertyChanged(nameof(MainScreenSummary));
        OnPropertyChanged(nameof(LowStockSummary));
        return products;
    }

    protected override int IdOf(Product item) => item.Id;
    protected override string NameOf(Product item) => item.Name;
    protected override bool IsActive(Product item) => item.IsActive;

    protected override bool Matches(Product item, string filter)
    {
        const StringComparison ic = StringComparison.OrdinalIgnoreCase;
        if (filter.Equals("low", ic) && item.IsLowOnStock) return true;
        return item.Name.Contains(filter, ic)
               || item.Category?.Name.Contains(filter, ic) == true
               || item.Code?.Contains(filter, ic) == true
               || item.Sku?.Contains(filter, ic) == true
               || item.Barcode?.Contains(filter, ic) == true;
    }

    protected override ProductEditorViewModel CreateEditor(Product? item) =>
        new(item, _categories, session.HasPermission(Permission.ManageStock));

    protected override async Task<int> SaveEditorAsync(ProductEditorViewModel editor) =>
        (await catalog.SaveProductAsync(editor.ToInput())).Id;

    protected override void OnOpened(ProductEditorViewModel editor, Product? item)
    {
        if (editor.ShowStockPanel && editor.CanManageStock)
            _ = LoadHistoryAsync(editor);
    }

    private async Task LoadHistoryAsync(ProductEditorViewModel editor)
    {
        try
        {
            editor.History = await stock.GetMovementsAsync(editor.Id);
            editor.HistoryMessage = editor.History.Count == 0 ? "No stock changes yet." : null;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load stock history for product {ProductId}", editor.Id);
            editor.HistoryMessage = "Unable to load stock history.";
        }
    }

    /// <summary>Adds a delivery to the quantity on hand.</summary>
    [RelayCommand]
    private Task ReceiveStockAsync() => PostStockAsync(receive: true);

    /// <summary>Sets the quantity on hand to what was counted.</summary>
    [RelayCommand]
    private Task CountStockAsync() => PostStockAsync(receive: false);

    private async Task PostStockAsync(bool receive)
    {
        if (Editor is not { ShowStockPanel: true, CanManageStock: true } editor || IsBusy) return;
        if (editor.IsDirty)
        {
            ErrorMessage = "Save or undo your other changes first.";
            return;
        }

        int quantity;
        string note;
        try
        {
            (quantity, note) = editor.ToStockEntry();
        }
        catch (PosException ex)
        {
            ErrorMessage = ex.Message;
            return;
        }

        if (!receive && quantity != editor.StockQuantity &&
            !await Dialogs.ConfirmAsync("Stock Count",
                $"Set {editor.Heading} to {quantity} on hand?\nThe register thinks there are {editor.StockQuantity}; the difference ({quantity - editor.StockQuantity:+0;-0}) will be recorded.",
                "Set Count", "Cancel"))
            return;

        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            var after = receive
                ? await stock.ReceiveAsync(editor.Id, quantity, note)
                : await stock.CountAsync(editor.Id, quantity, note);
            IsBusy = false;
            await LoadAsync(editor.Id);
            StatusMessage = receive
                ? $"Received {quantity}. {editor.Heading} now has {after} on hand."
                : $"Count saved. {editor.Heading} now has {after} on hand.";
        }
        catch (PosException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Stock {Action} failed for product {ProductId}", receive ? "delivery" : "count", editor.Id);
            ErrorMessage = "Unable to save the stock change. Please try again.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
