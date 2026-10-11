using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ShmeshPOS.Infrastructure;
using ShmeshPOS.Services;

namespace ShmeshPOS.ViewModels;

public partial class HeldSaleRowViewModel(HeldSaleSummary sale) : ObservableObject
{
    public HeldSaleSummary Sale { get; } = sale;
    public string Title => Sale.Label ?? Sale.CustomerName ?? $"Held at {Sale.Time:h:mm tt}";

    [ObservableProperty]
    private bool _isHighlighted;
}

/// <summary>F3 → Retrieve Sale: held sales from every register; pick one to continue it here.</summary>
public partial class HeldSalesViewModel(IHeldSaleService held, IDialogService dialogs, ILogger logger) : ObservableObject, IKeyboardHandler
{
    private int _highlight = -1;

    [ObservableProperty] private IReadOnlyList<HeldSaleRowViewModel> _rows = [];
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isBusy;

    public event EventHandler<HeldSaleContent>? Retrieved;
    public event EventHandler? Closed;

    public async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            Rows = (await held.ListAsync()).Select(s => new HeldSaleRowViewModel(s)).ToList();
            Message = Rows.Count == 0 ? "No sales are on hold." : null;
            _highlight = -1;
            Move(1);
        }
        catch (PosException ex)
        {
            Message = ex.Message;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Loading held sales failed");
            Message = "Unable to load held sales. Please try again.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RetrieveAsync(HeldSaleRowViewModel? row)
    {
        if (row is null || IsBusy) return;
        IsBusy = true;
        try
        {
            Retrieved?.Invoke(this, await held.RetrieveAsync(row.Sale.Id));
        }
        catch (PosException ex)
        {
            IsBusy = false;
            await dialogs.ShowMessageAsync("Retrieve Sale", ex.Message);
            await InitializeAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Retrieving held sale {HeldSaleId} failed", row.Sale.Id);
            Message = "Unable to retrieve that sale. Please try again.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(HeldSaleRowViewModel? row)
    {
        if (row is null || IsBusy) return;
        if (!await dialogs.ConfirmAsync("Delete Held Sale", $"Delete “{row.Title}” ({row.Sale.Total:C})? The items will not be sold.", "Delete", "Keep"))
            return;
        try
        {
            await held.DeleteAsync(row.Sale.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Deleting held sale {HeldSaleId} failed", row.Sale.Id);
        }
        await InitializeAsync();
    }

    [RelayCommand]
    private void Close()
    {
        if (!IsBusy) Closed?.Invoke(this, EventArgs.Empty);
    }

    private void Move(int direction)
    {
        if (Rows.Count == 0) return;
        if (_highlight >= 0 && _highlight < Rows.Count) Rows[_highlight].IsHighlighted = false;
        _highlight = Math.Clamp(_highlight + direction, 0, Rows.Count - 1);
        Rows[_highlight].IsHighlighted = true;
    }

    private HeldSaleRowViewModel? Highlighted => _highlight >= 0 && _highlight < Rows.Count ? Rows[_highlight] : null;

    public bool HandleKey(Key key, bool textInputFocused)
    {
        switch (key)
        {
            case Key.Escape: Close(); break;
            case Key.Up: Move(-1); break;
            case Key.Down: Move(1); break;
            case Key.Enter: RetrieveCommand.Execute(Highlighted); break;
            case Key.Delete: DeleteCommand.Execute(Highlighted); break;
        }
        return true;
    }
}
