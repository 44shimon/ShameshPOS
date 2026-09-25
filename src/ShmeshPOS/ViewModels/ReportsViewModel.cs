using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShmeshPOS.Infrastructure;
using ShmeshPOS.Services;

namespace ShmeshPOS.ViewModels;

public partial class SaleRowViewModel(SaleListItem sale) : ObservableObject
{
    public SaleListItem Sale { get; } = sale;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// Reports (F3 → Reports): totals for a period (the end-of-day report when the period is one day),
/// and the list of sales with receipt preview and reprint.
/// </summary>
public partial class ReportsViewModel : ObservableObject, IKeyboardHandler
{
    private readonly IReportService _reports;
    private readonly IReceiptService _receipts;
    private readonly ISessionService _session;
    private readonly ILogger<ReportsViewModel> _logger;

    public ReportsViewModel(IReportService reports, IReceiptService receipts, ISessionService session,
        IOptions<PosOptions> options, ILogger<ReportsViewModel> logger)
    {
        _reports = reports;
        _receipts = receipts;
        _session = session;
        _logger = logger;
        StoreName = options.Value.StoreName;
        _fromDate = DateTime.Today;
        _toDate = DateTime.Today;
    }

    public string StoreName { get; }

    /// <summary>First day of the period (inclusive).</summary>
    [ObservableProperty] private DateTime _fromDate;

    /// <summary>Last day of the period (inclusive).</summary>
    [ObservableProperty] private DateTime _toDate;

    [ObservableProperty] private string _preset = "Today";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSummaryTab), nameof(IsSalesTab))]
    private string _tab = "Summary";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeriodText), nameof(ReportLines), nameof(HasSales))]
    private SalesSummary? _summary;

    [ObservableProperty] private IReadOnlyList<SaleRowViewModel> _sales = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedSale))]
    private SaleRowViewModel? _selectedSale;

    public bool HasSelectedSale => SelectedSale is not null;
    [ObservableProperty] private IReadOnlyList<string> _receiptLines = [];

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _statusMessage;

    public bool IsSummaryTab => Tab == "Summary";
    public bool IsSalesTab => Tab == "Sales";
    public bool HasSales => Summary is { SaleCount: > 0 };
    public string PeriodText => Summary is null ? "" : ReceiptFormatter.Period(Summary);

    /// <summary>The printable report, shown as a paper-style preview next to the figures.</summary>
    public IReadOnlyList<string> ReportLines => Summary is null ? [] :
        ReceiptFormatter.Summary(Summary, StoreName, _session.CurrentUser?.DisplayName ?? "", DateTime.Now);

    public event EventHandler? Closed;

    public Task InitializeAsync() => RefreshAsync();

    private bool _suppressDateRefresh;

    partial void OnFromDateChanged(DateTime value) => OnDatesEdited();
    partial void OnToDateChanged(DateTime value) => OnDatesEdited();

    private void OnDatesEdited()
    {
        if (_suppressDateRefresh) return;
        Preset = "Custom";
        _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task SetPresetAsync(string preset)
    {
        var today = DateTime.Today;
        var (from, to) = preset switch
        {
            "Yesterday" => (today.AddDays(-1), today.AddDays(-1)),
            "Week" => (today.AddDays(-(((int)today.DayOfWeek + 6) % 7)), today),   // Monday to today
            "Month" => (new DateTime(today.Year, today.Month, 1), today),
            _ => (today, today),
        };
        _suppressDateRefresh = true;
        FromDate = from;
        ToDate = to;
        _suppressDateRefresh = false;
        Preset = preset;
        await RefreshAsync();
    }

    [RelayCommand]
    private void ShowTab(string tab) => Tab = tab;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (ToDate.Date < FromDate.Date)
        {
            ErrorMessage = "The end date is before the start date.";
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            var from = FromDate.Date;
            var to = ToDate.Date.AddDays(1);
            Summary = await _reports.GetSummaryAsync(from, to);
            var list = await _reports.GetSalesAsync(from, to);
            Sales = list.Select(s => new SaleRowViewModel(s)).ToList();
            await SelectSaleAsync(Sales.FirstOrDefault());
        }
        catch (PosException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading reports failed");
            ErrorMessage = "Unable to load the report. Please try again.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SelectSaleAsync(SaleRowViewModel? row)
    {
        foreach (var r in Sales) r.IsSelected = r == row;
        SelectedSale = row;
        ReceiptLines = [];
        if (row is null) return;
        try
        {
            ReceiptLines = await _receipts.FormatSaleAsync(row.Sale.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading receipt {SaleId} failed", row.Sale.Id);
            ReceiptLines = ["Unable to load this receipt."];
        }
    }

    [RelayCommand]
    private async Task PrintReportAsync()
    {
        if (Summary is null || IsBusy) return;
        await RunPrintAsync(() => _receipts.PrintSummaryAsync(Summary), "Report sent to the printer.");
    }

    [RelayCommand]
    private async Task ReprintAsync()
    {
        if (SelectedSale is null || IsBusy) return;
        await RunPrintAsync(() => _receipts.PrintSaleAsync(SelectedSale.Sale.Id), $"Receipt {SelectedSale.Sale.ReceiptNumber} sent to the printer.");
    }

    private async Task RunPrintAsync(Func<Task> print, string done)
    {
        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            await print();
            StatusMessage = done;
        }
        catch (PosException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Printing failed");
            ErrorMessage = "Printing failed. Check the printer and try again.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Back() => Closed?.Invoke(this, EventArgs.Empty);

    public bool HandleKey(Key key, bool textInputFocused)
    {
        if (key == Key.Escape)
        {
            Back();
            return true;
        }
        return key is Key.F2 or Key.F3 or Key.F4 or Key.F8;
    }
}
