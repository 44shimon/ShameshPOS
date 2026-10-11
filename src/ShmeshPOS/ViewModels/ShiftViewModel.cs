using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ShmeshPOS.Infrastructure;
using ShmeshPOS.Services;

namespace ShmeshPOS.ViewModels;

public enum ShiftStep
{
    /// <summary>Count the float into the drawer and open the shift.</summary>
    Open,

    /// <summary>Count the cash in the drawer (without seeing what is expected) and close the shift.</summary>
    Close,

    /// <summary>The closed shift's report: expected, counted, over or short.</summary>
    Report,
}

/// <summary>Shift overlay: opening float at the start, cash count and shift report at the end.</summary>
public partial class ShiftViewModel : ObservableObject, IKeyboardHandler
{
    private readonly IShiftService _shifts;
    private readonly IReceiptService _receipts;
    private readonly ILogger _logger;

    public ShiftViewModel(ShiftStep step, IShiftService shifts, IReceiptService receipts, ILogger logger, ShiftInfo? openShift = null,
        bool canSkip = true)
    {
        _shifts = shifts;
        _receipts = receipts;
        _logger = logger;
        _step = step;
        OpenShift = openShift;
        CanSkip = canSkip;
    }

    /// <summary>The shift being closed.</summary>
    public ShiftInfo? OpenShift { get; }

    /// <summary>Opening can be put off (e.g. an administrator only doing back-office work); the register then asks again before taking payment.</summary>
    public bool CanSkip { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpenStep), nameof(IsCloseStep), nameof(IsReportStep), nameof(Title), nameof(Prompt), nameof(ActionText))]
    private ShiftStep _step;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Amount), nameof(CanSubmit))]
    private string _amountText = "";

    [ObservableProperty] private string _note = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    private bool _isBusy;

    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private ShiftReport? _report;
    [ObservableProperty] private IReadOnlyList<string> _reportLines = [];

    public bool IsOpenStep => Step == ShiftStep.Open;
    public bool IsCloseStep => Step == ShiftStep.Close;
    public bool IsReportStep => Step == ShiftStep.Report;

    public string Title => Step switch
    {
        ShiftStep.Open => "OPEN SHIFT",
        ShiftStep.Close => "CLOSE SHIFT",
        _ => "SHIFT CLOSED",
    };

    public string Prompt => Step switch
    {
        ShiftStep.Open => "Count the cash you are putting in the drawer for change (the opening float), then press Open Shift.",
        ShiftStep.Close => "Count all the cash in the drawer, including the float, and enter the total. " +
                           "The register works out what should be there after you enter it.",
        _ => "",
    };

    public string ActionText => Step == ShiftStep.Open ? "OPEN SHIFT" : "CLOSE SHIFT";

    public decimal? Amount =>
        decimal.TryParse(AmountText, NumberStyles.Number, CultureInfo.CurrentCulture, out var value) && value >= 0
            ? value
            : null;

    public bool CanSubmit => !IsBusy && Amount is not null;

    /// <summary>A shift was opened.</summary>
    public event EventHandler<ShiftInfo>? Opened;

    /// <summary>The overlay is done (after opening, closing, or being put off).</summary>
    public event EventHandler? Closed;

    /// <summary>The shift was closed; raised when the report is dismissed.</summary>
    public event EventHandler<ShiftReport>? ShiftClosed;

    [RelayCommand]
    private void Keypad(string key)
    {
        if (IsReportStep || IsBusy) return;
        var separator = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
        AmountText = key switch
        {
            "BKSP" => AmountText.Length > 0 ? AmountText[..^1] : "",
            "CLR" => "",
            "." when AmountText.Contains(separator) => AmountText,
            "." => (AmountText.Length == 0 ? "0" : AmountText) + separator,
            _ when AmountText.Length >= 9 => AmountText,
            _ => AmountText + key,
        };
    }

    [RelayCommand]
    private async Task SubmitAsync()
    {
        if (!CanSubmit || Amount is not { } amount || IsReportStep) return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            if (IsOpenStep)
            {
                var shift = await _shifts.OpenAsync(amount);
                Opened?.Invoke(this, shift);
                Closed?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                Report = await _shifts.CloseAsync(amount, Note);
                ReportLines = _receipts.FormatShift(Report);
                Step = ShiftStep.Report;
            }
        }
        catch (PosException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Shift {Step} failed", Step);
            ErrorMessage = "Unable to save. Check the connection and try again.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task PrintAsync()
    {
        if (Report is null || IsBusy) return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await _receipts.PrintShiftAsync(Report);
            StatusMessage = "Shift report sent to the printer.";
        }
        catch (Exception ex) when (ex is PosException or InvalidOperationException)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Printing shift report {ShiftId} failed", Report.ShiftId);
            ErrorMessage = "Printing failed. Check the printer and try again.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsBusy) return;
        if (IsReportStep && Report is not null)
            ShiftClosed?.Invoke(this, Report);
        else if (IsOpenStep && !CanSkip)
            return;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    public bool HandleKey(Key key, bool textInputFocused)
    {
        switch (key)
        {
            case Key.Escape:
                Cancel();
                return true;
            case Key.Enter:
                if (IsReportStep) Cancel();
                else SubmitCommand.Execute(null);
                return true;
        }
        // Typing goes to the note box when it has focus; otherwise digits go to the amount.
        if (textInputFocused) return key is >= Key.F1 and <= Key.F24;
        if (KeyInput.ToDigit(key) is { } digit) Keypad(digit.ToString());
        else if (key == Key.Back) Keypad("BKSP");
        else if (key is Key.Decimal or Key.OemPeriod) Keypad(".");
        return true;
    }
}
