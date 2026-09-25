using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ShmeshPOS.Services;

namespace ShmeshPOS.ViewModels.Admin;

/// <summary>What the administration screen needs from each tab.</summary>
public interface IAdminSection
{
    Task InitializeAsync();
    Task<bool> ConfirmDiscardAsync();
    ICommand SaveCommand { get; }

    /// <summary>The open import overlay, if any.</summary>
    ImportViewModel? Import { get; }
}

/// <summary>A form for one record. Tracks unsaved changes so switching away can ask before discarding them.</summary>
public abstract partial class EditorViewModel : ObservableObject
{
    private bool _tracking;

    [ObservableProperty]
    private bool _isDirty;

    public abstract bool IsNew { get; }

    /// <summary>Call at the end of the derived constructor, after the fields are loaded.</summary>
    protected void StartTracking() => _tracking = true;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_tracking && e.PropertyName != nameof(IsDirty) && IsTracked(e.PropertyName))
            IsDirty = true;
    }

    /// <summary>Override to exclude display-only properties (e.g. loaded history) from unsaved-change tracking.</summary>
    protected virtual bool IsTracked(string? propertyName) => true;

    protected static decimal ParseDecimal(string text, string error)
    {
        var cleaned = text.Trim().Replace(CultureInfo.CurrentCulture.NumberFormat.CurrencySymbol, "").Replace("%", "");
        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.CurrentCulture, out var value)
            ? value
            : throw new PosException(error);
    }

    protected static int ParseInt(string text, string error) =>
        string.IsNullOrWhiteSpace(text) ? 0
        : int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) ? value
        : throw new PosException(error);
}

public partial class AdminRowViewModel<T>(T item) : ObservableObject
{
    public T Item { get; } = item;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// List-plus-form screen shared by products, categories and users: filter the list, pick a row to edit it,
/// or start a new record; save goes through the matching admin service, which does all validation.
/// </summary>
public abstract partial class AdminSectionViewModel<TItem, TEditor>(IDialogService dialogs, ILogger logger) : ObservableObject, IAdminSection
    where TItem : class
    where TEditor : EditorViewModel
{
    private IReadOnlyList<TItem> _all = [];
    private int? _selectedId;

    public abstract string Title { get; }
    public abstract string NewLabel { get; }

    [ObservableProperty]
    private IReadOnlyList<AdminRowViewModel<TItem>> _rows = [];

    [ObservableProperty]
    private string _filterText = "";

    [ObservableProperty]
    private bool _showDisabled = true;

    [ObservableProperty]
    private TEditor? _editor;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private ImportViewModel? _import;

    public int TotalCount => _all.Count;

    /// <summary>Whether this list can be filled from a spreadsheet.</summary>
    public virtual bool CanImport => false;

    /// <summary>Builds the import overlay for this list; only called when <see cref="CanImport"/> is true.</summary>
    protected virtual ImportViewModel? CreateImport() => null;

    [RelayCommand]
    private async Task OpenImportAsync()
    {
        if (IsBusy || Import is not null || !CanImport) return;
        if (!await ConfirmDiscardAsync()) return;

        var import = CreateImport();
        if (import is null) return;
        import.Imported += async (_, _) => await LoadAsync(_selectedId);
        import.Closed += (_, _) => Import = null;
        Import = import;
    }

    protected IDialogService Dialogs => dialogs;
    protected ILogger Logger => logger;

    Task IAdminSection.InitializeAsync() => LoadAsync();
    ICommand IAdminSection.SaveCommand => SaveCommand;

    /// <summary>Called after a record (or a blank new record) is opened in the editor.</summary>
    protected virtual void OnOpened(TEditor editor, TItem? item)
    {
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();
    partial void OnShowDisabledChanged(bool value) => ApplyFilter();

    protected abstract Task<IReadOnlyList<TItem>> FetchAsync();
    protected abstract int IdOf(TItem item);
    protected abstract string NameOf(TItem item);
    protected abstract bool IsActive(TItem item);
    protected abstract bool Matches(TItem item, string filter);
    protected abstract TEditor CreateEditor(TItem? item);

    /// <returns>The id of the saved record.</returns>
    protected abstract Task<int> SaveEditorAsync(TEditor editor);

    public async Task LoadAsync(int? selectId = null)
    {
        IsBusy = true;
        try
        {
            _all = await FetchAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load {Section}", Title);
            ErrorMessage = $"Unable to load {Title.ToLowerInvariant()}. Please try again.";
            return;
        }
        finally
        {
            IsBusy = false;
        }

        OnPropertyChanged(nameof(TotalCount));
        var item = selectId is { } id ? _all.FirstOrDefault(i => IdOf(i) == id) : null;
        Open(item ?? _all.FirstOrDefault());
    }

    private void ApplyFilter()
    {
        var filter = FilterText.Trim();
        Rows = _all
            .Where(i => (ShowDisabled || IsActive(i)) && (filter.Length == 0 || Matches(i, filter)))
            .Select(i => new AdminRowViewModel<TItem>(i) { IsSelected = IdOf(i) == _selectedId })
            .ToList();
    }

    private void Open(TItem? item)
    {
        _selectedId = item is null ? null : IdOf(item);
        ApplyFilter();
        Editor = CreateEditor(item);
        ErrorMessage = null;
        OnOpened(Editor, item);
    }

    public async Task<bool> ConfirmDiscardAsync() =>
        Editor is not { IsDirty: true } ||
        await dialogs.ConfirmAsync("Unsaved Changes", "Discard the changes you made?", "Discard", "Keep Editing");

    [RelayCommand]
    private async Task SelectAsync(AdminRowViewModel<TItem>? row)
    {
        if (row is null || IsBusy || IdOf(row.Item) == _selectedId) return;
        if (!await ConfirmDiscardAsync()) return;
        StatusMessage = null;
        Open(row.Item);
    }

    [RelayCommand]
    private async Task NewAsync()
    {
        if (IsBusy || !await ConfirmDiscardAsync()) return;
        StatusMessage = null;
        Open(null);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Editor is null || IsBusy) return;

        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = null;
        int savedId;
        try
        {
            savedId = await SaveEditorAsync(Editor);
        }
        catch (PosException ex)
        {
            ErrorMessage = ex.Message;
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Saving in {Section} failed", Title);
            ErrorMessage = "Unable to save. Please try again.";
            return;
        }
        finally
        {
            IsBusy = false;
        }

        await LoadAsync(savedId);
        StatusMessage = _all.FirstOrDefault(i => IdOf(i) == savedId) is { } saved ? $"Saved “{NameOf(saved)}”." : "Saved.";
    }

    [RelayCommand]
    private void Revert()
    {
        if (IsBusy) return;
        Open(_selectedId is { } id ? _all.FirstOrDefault(i => IdOf(i) == id) : null);
        StatusMessage = null;
    }
}
