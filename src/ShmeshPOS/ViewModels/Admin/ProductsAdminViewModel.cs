using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ShmeshPOS.Models;
using ShmeshPOS.Infrastructure;
using ShmeshPOS.Services;
using ShmeshPOS.Services.Import;

namespace ShmeshPOS.ViewModels.Admin;

public partial class ProductEditorViewModel : EditorViewModel
{
    private static readonly HashSet<string> UntrackedProperties =
    [
        nameof(History), nameof(HistoryMessage), nameof(StockAmountText), nameof(StockNote),
    ];

    public ProductEditorViewModel(Product? product, IReadOnlyList<Category> categories)
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
        StockQuantity = product?.StockQuantity ?? 0;
        WasTrackingStock = product?.TrackStock ?? false;
        IsLowOnStock = product?.IsLowOnStock ?? false;
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
    [ObservableProperty] private bool _trackStock;
    [ObservableProperty] private string _reorderLevelText;

    // ---- Stock on hand, deliveries, counts and history (not part of the form's unsaved-change tracking) ----

    public int StockQuantity { get; }
    public bool IsLowOnStock { get; }

    /// <summary>Deliveries and counts need tracking to be saved first, so the stock panel follows the saved setting.</summary>
    public bool WasTrackingStock { get; }

    [ObservableProperty] private IReadOnlyList<StockMovement> _history = [];
    [ObservableProperty] private string? _historyMessage;
    [ObservableProperty] private string _stockAmountText = "";
    [ObservableProperty] private string _stockNote = "";

    protected override bool IsTracked(string? propertyName) =>
        propertyName is null || !UntrackedProperties.Contains(propertyName);

    public ProductInput ToInput()
    {
        var category = Category ?? throw new PosException("Choose a category.");
        if (string.IsNullOrWhiteSpace(PriceText)) throw new PosException("Enter a price.");
        var price = ParseDecimal(PriceText, "Enter the price as a number, e.g. 2.50.");
        var taxPercent = string.IsNullOrWhiteSpace(TaxPercentText) ? 0m : ParseDecimal(TaxPercentText, "Enter the tax rate as a percentage, e.g. 8.875.");
        var sortOrder = ParseInt(SortOrderText, "Sort order must be a whole number.");
        int? reorderLevel = string.IsNullOrWhiteSpace(ReorderLevelText)
            ? null
            : ParseInt(ReorderLevelText, "The low-stock level must be a whole number, 0 or more.");
        return new ProductInput(Id, Name, category.Id, Code, Sku, Barcode, price, taxPercent / 100m, sortOrder, IsActive, ShowOnMainScreen,
            TrackStock, reorderLevel);
    }

    /// <summary>The quantity typed for a delivery or a count.</summary>
    public int ToStockQuantity(string error)
    {
        if (string.IsNullOrWhiteSpace(StockAmountText)) throw new PosException(error);
        return ParseInt(StockAmountText, error);
    }
}

public partial class ProductsAdminViewModel(
    ICatalogAdminService catalog,
    IStockService stock,
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

    /// <summary>e.g. "7 of 10 on the main screen  ·  2 low on stock", shown above the list.</summary>
    public string MainScreenSummary { get; private set; } = "";
    public override string NewLabel => "New Product";

    protected override async Task<IReadOnlyList<Product>> FetchAsync()
    {
        _categories = await catalog.GetCategoriesAsync();
        var products = await catalog.GetProductsAsync();
        var low = products.Count(p => p.IsActive && p.IsLowOnStock);
        MainScreenSummary = $"{products.Count(p => p.ShowOnMainScreen)} of {CatalogAdminService.MaxMainScreenItems} on the main screen"
                            + (low > 0 ? $"   ·   {low} low on stock" : "");
        OnPropertyChanged(nameof(MainScreenSummary));
        return products;
    }

    protected override int IdOf(Product item) => item.Id;
    protected override string NameOf(Product item) => item.Name;
    protected override bool IsActive(Product item) => item.IsActive;

    protected override bool Matches(Product item, string filter)
    {
        const StringComparison ic = StringComparison.OrdinalIgnoreCase;
        return item.Name.Contains(filter, ic)
               || item.Category?.Name.Contains(filter, ic) == true
               || item.Code?.Contains(filter, ic) == true
               || item.Sku?.Contains(filter, ic) == true
               || item.Barcode?.Contains(filter, ic) == true
               || (item.IsLowOnStock && "low stock".Contains(filter, ic));
    }

    protected override ProductEditorViewModel CreateEditor(Product? item) => new(item, _categories);

    protected override async Task<int> SaveEditorAsync(ProductEditorViewModel editor) =>
        (await catalog.SaveProductAsync(editor.ToInput())).Id;

    protected override void OnOpened(ProductEditorViewModel editor, Product? item)
    {
        if (item is { TrackStock: true })
            _ = LoadHistoryAsync(editor);
    }

    private async Task LoadHistoryAsync(ProductEditorViewModel editor)
    {
        try
        {
            editor.History = await stock.GetMovementsAsync(editor.Id);
            editor.HistoryMessage = editor.History.Count == 0 ? "No stock changes yet. Count the shelf to set the starting quantity." : null;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load stock history for product {ProductId}", editor.Id);
            editor.HistoryMessage = "Unable to load stock history.";
        }
    }

    /// <summary>Adds a delivery to the quantity on hand.</summary>
    [RelayCommand]
    private Task ReceiveStockAsync() => PostStockAsync(received: true);

    /// <summary>Sets the quantity on hand to what was counted on the shelf.</summary>
    [RelayCommand]
    private Task CountStockAsync() => PostStockAsync(received: false);

    private async Task PostStockAsync(bool received)
    {
        if (Editor is not { IsNew: false, WasTrackingStock: true } editor || IsBusy) return;
        if (editor.IsDirty)
        {
            ErrorMessage = "Save or undo your changes to this product first.";
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            var quantity = editor.ToStockQuantity(received
                ? "Enter how many were received (a whole number above zero)."
                : "Enter the quantity counted (a whole number, 0 or more).");
            var onHand = received
                ? await stock.ReceiveAsync(editor.Id, quantity, editor.StockNote)
                : await stock.CountAsync(editor.Id, quantity, editor.StockNote);
            IsBusy = false;
            await LoadAsync(editor.Id);
            StatusMessage = received
                ? $"Received {quantity} × “{editor.Heading}”. {onHand} on hand."
                : $"Counted “{editor.Heading}”: {onHand} on hand.";
        }
        catch (PosException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Stock change failed for product {ProductId}", editor.Id);
            ErrorMessage = "Unable to update the stock. Please try again.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
