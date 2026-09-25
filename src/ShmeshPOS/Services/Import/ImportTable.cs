using System.Globalization;
using System.IO;
using System.Text;
using ExcelDataReader;

namespace ShmeshPOS.Services.Import;

public sealed record ImportRow(int Line, IReadOnlyList<string?> Cells);

/// <summary>
/// The first worksheet of an Excel file (.xlsx/.xls) or a CSV/text file, as header names plus rows of text.
/// The header row is the first non-empty row; fully empty rows are skipped.
/// </summary>
public sealed class ImportTable
{
    public static readonly string[] SupportedExtensions = [".xlsx", ".xlsm", ".xls", ".csv", ".txt"];

    static ImportTable()
    {
        // Legacy .xls and some CSV files use Windows code pages that .NET doesn't load by default.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private ImportTable(string fileName, IReadOnlyList<string> headers, IReadOnlyList<ImportRow> rows)
    {
        FileName = fileName;
        Headers = headers;
        Rows = rows;
    }

    public string FileName { get; }
    public IReadOnlyList<string> Headers { get; }
    public IReadOnlyList<ImportRow> Rows { get; }

    public static ImportTable Read(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (!SupportedExtensions.Contains(extension))
            throw new PosException("Choose an Excel (.xlsx, .xls) or CSV file.");

        try
        {
            // ReadWrite sharing lets us read a file that is still open in Excel.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = extension is ".csv" or ".txt"
                ? ExcelReaderFactory.CreateCsvReader(stream, new ExcelReaderConfiguration
                {
                    FallbackEncoding = Encoding.UTF8,
                    AutodetectSeparators = [',', ';', '\t', '|'],
                })
                : ExcelReaderFactory.CreateReader(stream);
            return FromReader(Path.GetFileName(path), reader);
        }
        catch (PosException)
        {
            throw;
        }
        catch (IOException ex)
        {
            throw new PosException($"Couldn't open {Path.GetFileName(path)}. Check that the file exists and try again.", ex);
        }
        catch (Exception ex)
        {
            throw new PosException($"Couldn't read {Path.GetFileName(path)}. Save it as .xlsx or .csv and try again.", ex);
        }
    }

    /// <summary>Builds a table from in-memory rows; used by tests.</summary>
    public static ImportTable FromRows(string fileName, IEnumerable<IReadOnlyList<string?>> rows)
    {
        var list = rows.ToList();
        var headerIndex = list.FindIndex(r => r.Any(c => !string.IsNullOrWhiteSpace(c)));
        if (headerIndex < 0) throw new PosException($"{fileName} is empty.");
        var headers = list[headerIndex].Select(h => h?.Trim() ?? "").ToList();
        var data = list.Skip(headerIndex + 1)
            .Select((cells, i) => new ImportRow(headerIndex + i + 2, cells))
            .Where(r => r.Cells.Any(c => !string.IsNullOrWhiteSpace(c)))
            .ToList();
        return new ImportTable(fileName, headers, data);
    }

    private static ImportTable FromReader(string fileName, IExcelDataReader reader)
    {
        var rows = new List<IReadOnlyList<string?>>();
        while (reader.Read())
        {
            var cells = new string?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
                cells[i] = CellText(reader.GetValue(i));
            rows.Add(cells);
        }
        return FromRows(fileName, rows);
    }

    private static string? CellText(object? value) => value switch
    {
        null or DBNull => null,
        string s => s.Trim(),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()?.Trim(),
    };
}

/// <summary>Finds each field's column by its header, accepting the usual names people give them.</summary>
public sealed class ColumnMap
{
    // Words too short or too generic to match inside a longer heading ("no" is in "phone", "id" in "paid").
    private const int MinContainsLength = 4;
    private static readonly string[] IgnoredHeadingWords = ["cost", "wholesale", "supplier", "vendor"];

    private readonly Dictionary<string, int> _columns;
    private readonly IReadOnlyList<string> _headers;

    private ColumnMap(Dictionary<string, int> columns, IReadOnlyList<string> headers)
    {
        _columns = columns;
        _headers = headers;
    }

    /// <summary>
    /// Pass 1: headings that equal a synonym (earlier synonyms win, so "Name" beats "Description").
    /// Pass 2: for fields still missing, headings that contain a synonym ("Item Description" → Name),
    /// skipping headings already used and cost/supplier columns so "Cost Price" is never taken as the price.
    /// </summary>
    public static ColumnMap Build(IReadOnlyList<string> headers, IReadOnlyDictionary<string, string[]> synonyms)
    {
        var normalized = headers.Select(Normalize).ToList();
        var columns = new Dictionary<string, int>();

        foreach (var (field, names) in synonyms)
        {
            foreach (var name in names)
            {
                var index = normalized.IndexOf(Normalize(name));
                if (index >= 0 && !columns.ContainsValue(index))
                {
                    columns[field] = index;
                    break;
                }
            }
        }

        foreach (var (field, names) in synonyms)
        {
            if (columns.ContainsKey(field)) continue;
            foreach (var name in names.Select(Normalize).Where(n => n.Length >= MinContainsLength).OrderByDescending(n => n.Length))
            {
                var index = normalized.FindIndex(h =>
                    h.Contains(name) && !IgnoredHeadingWords.Any(h.Contains));
                if (index >= 0 && !columns.ContainsValue(index))
                {
                    columns[field] = index;
                    break;
                }
            }
        }

        return new ColumnMap(columns, headers);
    }

    public bool Has(string field) => _columns.ContainsKey(field);

    public IEnumerable<string> Fields => _columns.Keys;

    /// <summary>How each field was matched, e.g. Name ← “Item Description”, for the import preview.</summary>
    public IReadOnlyList<string> Describe() =>
        _columns.OrderBy(c => c.Value).Select(c => $"{Friendly(c.Key)} ← “{_headers[c.Value]}”").ToList();

    // "FirstName" → "First Name"
    private static string Friendly(string field) =>
        string.Concat(field.Select((ch, i) => i > 0 && char.IsUpper(ch) ? " " + ch : ch.ToString()));

    /// <summary>The file's non-empty headings, for "column not found" messages.</summary>
    public string HeadingList => string.Join(", ", _headers.Where(h => h.Length > 0).Select(h => $"“{h}”"));

    /// <summary>Trimmed cell text, or null when the column is missing or the cell is blank.</summary>
    public string? Get(ImportRow row, string field) =>
        _columns.TryGetValue(field, out var i) && i < row.Cells.Count && !string.IsNullOrWhiteSpace(row.Cells[i])
            ? row.Cells[i]!.Trim()
            : null;

    private static string Normalize(string header) =>
        new(header.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}

/// <summary>Lenient parsing of spreadsheet values ("$2.50", "8.875%", "Yes").</summary>
public static class ImportValue
{
    private static readonly char[] CurrencySymbols = ['$', '€', '£', '₪', '¥'];

    public static bool TryMoney(string text, out decimal value)
    {
        var s = text.Trim();
        var negative = s.StartsWith('(') && s.EndsWith(')');
        s = new string(s.Where(c => !CurrencySymbols.Contains(c) && c is not '(' and not ')' && !char.IsWhiteSpace(c)).ToArray());
        if (!TryNumber(s, out value)) return false;
        if (negative) value = -value;
        value = Math.Round(value, 2, MidpointRounding.AwayFromZero);
        return true;
    }

    /// <summary>
    /// "8.875%" and "8.875" both mean 8.875%; a bare value below 1 (e.g. 0.08875, how Excel stores a
    /// percentage-formatted cell) is taken as a fraction. Returns the rate as a fraction.
    /// </summary>
    public static bool TryTaxRate(string text, out decimal rate)
    {
        var s = text.Trim();
        var hasPercent = s.EndsWith('%');
        if (!TryNumber(s.TrimEnd('%').Trim(), out var number) || number < 0)
        {
            rate = 0;
            return false;
        }
        rate = hasPercent || number >= 1 ? number / 100m : number;
        rate = Math.Round(rate, 5, MidpointRounding.AwayFromZero);
        return rate < 1;
    }

    public static bool TryBool(string text, out bool value)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "yes" or "y" or "true" or "1" or "active" or "on" or "open" or "enabled":
                value = true;
                return true;
            case "no" or "n" or "false" or "0" or "inactive" or "off" or "closed" or "disabled":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }

    public static bool TryInt(string text, out int value)
    {
        value = 0;
        if (!TryNumber(text.Trim(), out var d) || d != Math.Floor(d) || d < int.MinValue || d > int.MaxValue)
            return false;
        value = (int)d;
        return true;
    }

    /// <summary>Text for identifier columns (codes, barcodes): Excel turns "101" into 101.0, which reads back as "101".</summary>
    public static string? Key(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static bool TryNumber(string s, out decimal value) =>
        decimal.TryParse(s, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out value)
        || decimal.TryParse(s, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.CurrentCulture, out value);
}
