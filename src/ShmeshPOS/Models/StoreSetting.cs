namespace ShmeshPOS.Models;

/// <summary>
/// Store-wide setting shared by every register, stored as text by key. Typed access goes through
/// <see cref="Services.ISettingsService"/>; per-register settings stay in appsettings.json.
/// </summary>
public class StoreSetting
{
    public required string Key { get; set; }
    public required string Value { get; set; }
    public DateTime UpdatedAt { get; set; }
}
