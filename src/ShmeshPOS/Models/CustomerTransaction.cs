namespace ShmeshPOS.Models;

public enum CustomerTransactionType
{
    /// <summary>A sale charged to the account.</summary>
    Charge,

    /// <summary>The customer paid money towards the account.</summary>
    Payment,

    /// <summary>A manual correction by an administrator.</summary>
    Adjustment,
}

/// <summary>
/// One line of a customer's account history. The account balance is always the sum of these amounts;
/// each row also records the balance it left behind, so the history reads like a statement.
/// </summary>
public class CustomerTransaction
{
    public long Id { get; set; }

    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public CustomerTransactionType Type { get; set; }

    /// <summary>Change to the amount owed: positive for a charge, negative for a payment.</summary>
    public decimal Amount { get; set; }

    public decimal BalanceAfter { get; set; }

    /// <summary>The sale that was charged, for <see cref="CustomerTransactionType.Charge"/>.</summary>
    public long? SaleId { get; set; }
    public Sale? Sale { get; set; }

    /// <summary>How the customer paid, for <see cref="CustomerTransactionType.Payment"/>.</summary>
    public int? PaymentMethodId { get; set; }
    public PaymentMethod? PaymentMethod { get; set; }

    public string? Note { get; set; }

    public int UserId { get; set; }
    public User? User { get; set; }

    public string? TerminalName { get; set; }
    public DateTime CreatedAt { get; set; }
}
