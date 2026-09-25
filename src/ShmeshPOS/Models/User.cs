namespace ShmeshPOS.Models;

public class User
{
    public int Id { get; set; }
    public required string Username { get; set; }
    public required string DisplayName { get; set; }

    /// <summary>PBKDF2 hash of the PIN in the format produced by <see cref="Security.PinHasher"/>. Never the PIN itself.</summary>
    public required string PinHash { get; set; }

    public UserRole Role { get; set; } = UserRole.Cashier;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
}
