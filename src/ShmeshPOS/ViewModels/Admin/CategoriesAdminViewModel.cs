using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ShmeshPOS.Models;
using ShmeshPOS.Services;

namespace ShmeshPOS.ViewModels.Admin;

public sealed record CategoryListItem(Category Category, int ProductCount, int ActiveProductCount);

public partial class CategoryEditorViewModel : EditorViewModel
{
    /// <summary>Tile accent colors offered to the administrator; chosen to stay readable on white tiles.</summary>
    public static readonly IReadOnlyList<string> Palette =
    [
        "#2563EB", "#0891B2", "#059669", "#65A30D", "#D97706",
        "#EA580C", "#DC2626", "#DB2777", "#7C3AED", "#475569",
    ];

    public CategoryEditorViewModel(Category? category)
    {
        Id = category?.Id ?? 0;
        Heading = category?.Name ?? "New Category";
        _name = category?.Name ?? "";
        _color = category?.Color ?? Palette[0];
        _sortOrderText = (category?.SortOrder ?? 0).ToString(CultureInfo.CurrentCulture);
        _isActive = category?.IsActive ?? true;
        StartTracking();
    }

    public int Id { get; }
    public override bool IsNew => Id == 0;
    public string Heading { get; }
    public IReadOnlyList<string> Colors => Palette;

    [ObservableProperty] private string _name;
    [ObservableProperty] private string? _color;
    [ObservableProperty] private string _sortOrderText;
    [ObservableProperty] private bool _isActive;

    [RelayCommand]
    private void SelectColor(string color) => Color = color;

    public CategoryInput ToInput() =>
        new(Id, Name, Color, ParseInt(SortOrderText, "Sort order must be a whole number."), IsActive);
}

public class CategoriesAdminViewModel(ICatalogAdminService catalog, IDialogService dialogs, ILogger<CategoriesAdminViewModel> logger)
    : AdminSectionViewModel<CategoryListItem, CategoryEditorViewModel>(dialogs, logger)
{
    public override string Title => "Categories";
    public override string NewLabel => "New Category";

    protected override async Task<IReadOnlyList<CategoryListItem>> FetchAsync()
    {
        var categories = await catalog.GetCategoriesAsync();
        var products = await catalog.GetProductsAsync();
        return categories
            .Select(c => new CategoryListItem(
                c,
                products.Count(p => p.CategoryId == c.Id),
                products.Count(p => p.CategoryId == c.Id && p.IsActive)))
            .ToList();
    }

    protected override int IdOf(CategoryListItem item) => item.Category.Id;
    protected override string NameOf(CategoryListItem item) => item.Category.Name;
    protected override bool IsActive(CategoryListItem item) => item.Category.IsActive;
    protected override bool Matches(CategoryListItem item, string filter) =>
        item.Category.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);

    protected override CategoryEditorViewModel CreateEditor(CategoryListItem? item) => new(item?.Category);

    protected override async Task<int> SaveEditorAsync(CategoryEditorViewModel editor) =>
        (await catalog.SaveCategoryAsync(editor.ToInput())).Id;
}
