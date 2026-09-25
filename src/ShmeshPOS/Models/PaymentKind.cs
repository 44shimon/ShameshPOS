namespace ShmeshPOS.Models;

/// <summary>
/// How the POS handles a payment method. Cash asks for the amount tendered and gives change;
/// Account charges the sale to the attached customer's account, within their credit limit.
/// </summary>
public enum PaymentKind
{
    Cash,
    Card,
    Other,
    Account,
}
