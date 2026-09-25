namespace ShmeshPOS.Models;

/// <summary>A customer with a charge account. Purchases charged to the account raise the balance; payments lower it.</summary>
public class Customer
{
    public int Id { get; set; }

    /// <summary>Short number printed on the customer's card or list, used to look them up at the register.</summary>
    public required string AccountNumber { get; set; }

    public required string Name { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Notes { get; set; }

    /// <summary>Amount the customer owes. Negative means they have credit on the account.</summary>
    public decimal Balance { get; set; }

    /// <summary>Most the customer may owe. Null uses the store-wide default from settings.</summary>
    public decimal? CreditLimit { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
