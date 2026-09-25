using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using ShmeshPOS.Models;
using ShmeshPOS.Infrastructure;
using ShmeshPOS.Services;
using ShmeshPOS.Services.Import;

namespace ShmeshPOS.ViewModels.Admin;

public partial class ProductEditorViewModel : EditorViewModel
{
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

    public ProductInput ToInput()
    {
        var category = Category ?? throw new PosException("Choose a category.");
        if (string.IsNullOrWhiteSpace(PriceText)) throw new PosException("Enter a price.");
        var price = ParseDecimal(PriceText, "Enter the price as a number, e.g. 2.50.");
        var taxPercent = string.IsNullOrWhiteSpace(TaxPercentText) ? 0m : ParseDecimal(TaxPercentText, "Enter the tax rate as a percentage, e.g. 8.875.");
        var sortOrder = ParseInt(SortOrderText, "Sort order must be a whole number.");
        return new ProductInput(Id, Name, category.Id, Code, Sku, Barcode, price, taxPercent / 100m, sortOrder, IsActive, ShowOnMainScreen);
    }
}

public class ProductsAdminViewModel(
    ICatalogAdminService catalog,
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
    public override string NewLabel => "New Product";

    protected override async Task<IReadOnlyList<Product>> FetchAsync()
    {
        _categories = await catalog.GetCategoriesAsync();
        var products = await catalog.GetProductsAsync();
        MainScreenSummary = $"{products.Count(p => p.ShowOnMainScreen)} of {CatalogAdminService.MaxMainScreenItems} on the main screen";
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
               || item.Barcode?.Contains(filter, ic) == true;
    }

    protected override ProductEditorViewModel CreateEditor(Product? item) => new(item, _categories);

    protected override async Task<int> SaveEditorAsync(ProductEditorViewModel editor) =>
        (await catalog.SaveProductAsync(editor.ToInput())).Id;
}
