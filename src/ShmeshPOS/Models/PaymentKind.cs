namespace ShmeshPOS.Models;

/// <summary>How the POS handles a payment method. Cash asks for the amount tendered and gives change.</summary>
public enum PaymentKind
{
    Cash,
    Card,
    Other,
}
