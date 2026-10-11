namespace ShmeshPOS.Models;

/// <summary>
/// One register's cash drawer from opening (with the float counted in) to closing (with the cash counted out).
/// Only one shift per register can be open. Cash taken and paid out on the register while the shift is open
/// gives the expected cash; the count at closing shows whether the drawer is over or short.
/// </summary>
public class Shift
{
    public int Id { get; set; }

    public required string TerminalName { get; set; }

    public int OpenedById { get; set; }
    public User? OpenedBy { get; set; }
    public DateTime OpenedAt { get; set; }

    /// <summary>Cash put in the drawer at the start, for change.</summary>
    public decimal OpeningFloat { get; set; }

    public int? ClosedById { get; set; }
    public User? ClosedBy { get; set; }
    public DateTime? ClosedAt { get; set; }

    /// <summary>Opening float plus cash in, less cash out, worked out when the shift was closed.</summary>
    public decimal? ExpectedCash { get; set; }

    /// <summary>Cash counted in the drawer at closing.</summary>
    public decimal? CountedCash { get; set; }

    public string? Note { get; set; }

    public bool IsOpen => ClosedAt is null;

    /// <summary>Counted less expected: positive when the drawer is over, negative when it is short.</summary>
    public decimal? Difference => CountedCash - ExpectedCash;
}
