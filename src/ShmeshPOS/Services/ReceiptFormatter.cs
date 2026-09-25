using System.Globalization;
using System.Text;

namespace ShmeshPOS.Services;

/// <summary>Store-wide receipt text. Header lines print centred at the top, footer lines at the bottom.</summary>
public sealed record ReceiptSettings(IReadOnlyList<string> Header, IReadOnlyList<string> Footer);

/// <summary>
/// Formats receipts and reports as fixed-width text lines, the native layout of thermal receipt printers
/// (42 columns on 80 mm paper). The same lines are shown on screen as a preview and sent to the printer.
/// </summary>
public static class ReceiptFormatter
{
    public const int Width = 42;

    public static IReadOnlyList<string> Receipt(ReceiptData r, ReceiptSettings settings, int width = Width)
    {
        var lines = new List<string>();
        foreach (var h in settings.Header.Where(h => h.Length > 0)) lines.AddRange(Center(h, width));
        if (lines.Count > 0) lines.Add("");

        lines.Add(Pair($"Receipt {r.ReceiptNumber}", r.Time.ToString("g", CultureInfo.CurrentCulture), width));
        lines.Add(Pair($"Cashier: {r.Cashier}", r.Terminal, width));
        if (r.CustomerName is not null) lines.Add(Fit($"Customer: {r.CustomerName} #{r.CustomerAccount}", width));
        lines.Add(new string('-', width));

        foreach (var item in r.Lines)
        {
            var amount = Money(item.LineTotal);
            if (item.Quantity == 1)
            {
                lines.Add(Pair(item.Name, amount, width));
            }
            else
            {
                lines.Add(Fit(item.Name, width));
                lines.Add(Pair($"  {item.Quantity} x {Money(item.UnitPrice)}", amount, width));
            }
        }

        lines.Add(new string('-', width));
        lines.Add(Pair("Subtotal", Money(r.Subtotal), width));
        if (r.Tax != 0) lines.Add(Pair("Tax", Money(r.Tax), width));
        lines.Add(Pair("TOTAL", Money(r.Total), width));
        lines.Add("");

        foreach (var p in r.Payments)
        {
            lines.Add(Pair(p.Method, Money(p.Amount), width));
            if (p.CashReceived is { } received) lines.Add(Pair("  Cash received", Money(received), width));
            if (p.Change is { } change) lines.Add(Pair("  Change", Money(change), width));
        }
        if (r.CustomerBalanceAfter is { } balance)
            lines.Add(Pair("Account balance now", Money(balance), width));

        var footer = settings.Footer.Where(f => f.Length > 0).ToList();
        if (footer.Count > 0)
        {
            lines.Add("");
            foreach (var f in footer) lines.AddRange(Center(f, width));
        }
        return lines;
    }

    /// <summary>End-of-day (Z) / period report for the receipt printer.</summary>
    public static IReadOnlyList<string> Summary(SalesSummary s, string storeName, string printedBy, DateTime printedAt, int width = Width)
    {
        var lines = new List<string>();
        lines.AddRange(Center(storeName, width));
        lines.AddRange(Center(SameDay(s) ? "END OF DAY REPORT" : "SALES REPORT", width));
        lines.AddRange(Center(Period(s), width));
        lines.Add(new string('=', width));

        lines.Add(Pair("Sales", s.SaleCount.ToString(CultureInfo.CurrentCulture), width));
        lines.Add(Pair("Items sold", s.ItemsSold.ToString(CultureInfo.CurrentCulture), width));
        lines.Add(Pair("Subtotal", Money(s.Subtotal), width));
        lines.Add(Pair("Tax", Money(s.Tax), width));
        lines.Add(Pair("TOTAL SALES", Money(s.Total), width));
        lines.Add(Pair("Average sale", Money(s.AverageSale), width));

        Section(lines, "PAYMENTS", s.ByPaymentMethod, width);
        if (s.AccountCharges != 0 || s.AccountPaymentsByMethod.Count > 0)
        {
            lines.Add("");
            lines.Add("CUSTOMER ACCOUNTS");
            lines.Add(Pair("Charged to accounts", Money(s.AccountCharges), width));
            foreach (var p in s.AccountPaymentsByMethod)
                lines.Add(Pair($"Paid on account ({p.Name})", Money(p.Amount), width));
        }

        lines.Add("");
        lines.Add("CASH DRAWER");
        lines.Add(Pair("Cash sales", Money(s.CashSales), width));
        if (s.CashAccountPayments != 0) lines.Add(Pair("Cash paid on accounts", Money(s.CashAccountPayments), width));
        lines.Add(Pair("Expected cash", Money(s.ExpectedCash), width));
        lines.Add("(plus the opening float)");

        Section(lines, "CASHIERS", s.ByCashier, width);

        if (s.TopProducts.Count > 0)
        {
            lines.Add("");
            lines.Add("TOP ITEMS");
            foreach (var p in s.TopProducts)
                lines.Add(Pair($"{p.Quantity} x {p.Name}", Money(p.Revenue), width));
        }

        lines.Add(new string('=', width));
        lines.Add(Fit($"Printed {printedAt.ToString("g", CultureInfo.CurrentCulture)} by {printedBy}", width));
        return lines;
    }

    public static string Period(SalesSummary s)
    {
        var lastDay = s.To.AddTicks(-1);
        return SameDay(s)
            ? s.From.ToString("ddd d MMM yyyy", CultureInfo.CurrentCulture)
            : $"{s.From.ToString("d MMM yyyy", CultureInfo.CurrentCulture)} – {lastDay.ToString("d MMM yyyy", CultureInfo.CurrentCulture)}";
    }

    private static bool SameDay(SalesSummary s) => s.From.Date == s.To.AddTicks(-1).Date;

    private static void Section(List<string> lines, string title, IReadOnlyList<NamedTotal> rows, int width)
    {
        if (rows.Count == 0) return;
        lines.Add("");
        lines.Add(title);
        foreach (var r in rows)
            lines.Add(Pair($"{r.Name} ({r.Count})", Money(r.Amount), width));
    }

    private static string Money(decimal amount) => amount.ToString("C", CultureInfo.CurrentCulture);

    /// <summary>Left text and right-aligned value on one line; the left side is shortened if they don't fit.</summary>
    public static string Pair(string left, string right, int width = Width)
    {
        var room = width - right.Length - 1;
        if (room < 1) return Fit(right, width);
        if (left.Length > room) left = left[..Math.Max(0, room - 1)] + "…";
        return left.PadRight(width - right.Length) + right;
    }

    private static string Fit(string text, int width) => text.Length <= width ? text : text[..(width - 1)] + "…";

    private static IEnumerable<string> Center(string text, int width)
    {
        foreach (var chunk in Wrap(text, width))
            yield return new string(' ', Math.Max(0, (width - chunk.Length) / 2)) + chunk;
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        var line = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(word.Length > width ? word[..width] : word);
        }
        if (line.Length > 0) yield return line.ToString();
    }
}
