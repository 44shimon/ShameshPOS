namespace ShmeshPOS.Models;

public class Category
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Optional accent color for product tiles, as a hex string such as <c>#2563EB</c>.</summary>
    public string? Color { get; set; }

    public List<Product> Products { get; set; } = [];
}
