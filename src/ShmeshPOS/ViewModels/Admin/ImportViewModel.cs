using System.IO;
using System.Text;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ShmeshPOS.Infrastructure;
using ShmeshPOS.Services;
using ShmeshPOS.Services.Import;

namespace ShmeshPOS.ViewModels.Admin;

public enum ImportStage
{
    Intro,
    Preview,
    Done,
}

/// <summary>
/// Import overlay: explain the expected columns → pick a file → preview what each row will do → import.
/// Nothing is written until the administrator presses Import on the preview.
/// </summary>
public partial class ImportViewModel(
    string title,
    string columnsHelp,
    string templateFileName,
    Func<string> template,
    Func<ImportTable, Task<ImportPlan>> planner,
    IImportService importer,
    IFileDialogService files,
    ILogger logger) : ObservableObject, IKeyboardHandler
{
    public string Title { get; } = title;
    public string ColumnsHelp { get; } = columnsHelp;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIntro), nameof(IsPreview), nameof(IsDone))]
    private ImportStage _stage = ImportStage.Intro;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImportLabel), nameof(CanImport))]
    private ImportPlan? _plan;

    [ObservableProperty]
    private IReadOnlyList<ImportPlanRow> _visibleRows = [];

    [ObservableProperty]
    private bool _problemsOnly;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    private bool _isBusy;

    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private string? _resultText;

    public bool IsIntro => Stage == ImportStage.Intro;
    public bool IsPreview => Stage == ImportStage.Preview;
    public bool IsDone => Stage == ImportStage.Done;

    public bool CanImport => !IsBusy && Plan is { HasWork: true };

    public string ImportLabel => Plan is { } p && p.HasWork
        ? $"Import {p.CreateCount + p.UpdateCount} row{(p.CreateCount + p.UpdateCount == 1 ? "" : "s")}"
        : "Nothing to import";

    public string ColumnsFound => Plan is null ? "" : string.Join("   ", Plan.Columns);

    /// <summary>Raised after rows were written, so the list behind the overlay can reload.</summary>
    public event EventHandler? Imported;
    public event EventHandler? Closed;

    partial void OnProblemsOnlyChanged(bool value) => RefreshRows();

    private void RefreshRows() =>
        VisibleRows = Plan is null ? [] : ProblemsOnly ? Plan.Rows.Where(r => r.Action == ImportAction.Error).ToList() : Plan.Rows;

    [RelayCommand]
    private async Task ChooseFileAsync()
    {
        if (IsBusy) return;
        var path = files.PickImportFile(Title);
        if (path is null) return;

        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = $"Reading {Path.GetFileName(path)}…";
        try
        {
            var table = await Task.Run(() => ImportTable.Read(path));
            Plan = await planner(table);
            OnPropertyChanged(nameof(ColumnsFound));
            ProblemsOnly = false;
            RefreshRows();
            Stage = ImportStage.Preview;
            StatusMessage = null;
        }
        catch (PosException ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Reading import file {File} failed", path);
            ErrorMessage = "Unable to read that file. Save it as .xlsx or .csv and try again.";
            StatusMessage = null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void SaveTemplate()
    {
        var path = files.PickSaveFile("Save template", templateFileName, "CSV file (*.csv)|*.csv");
        if (path is null) return;
        try
        {
            // UTF-8 with BOM so Excel shows accented names correctly when it opens the file.
            File.WriteAllText(path, template(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            StatusMessage = $"Template saved to {path}. Fill it in (or copy your columns into it), save, then choose it here.";
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Saving import template failed");
            ErrorMessage = "Unable to save the template there. Try another folder.";
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (!CanImport || Plan is null) return;

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var result = await importer.ApplyAsync(Plan);
            var parts = new List<string>();
            if (result.Created > 0) parts.Add($"{result.Created} added");
            if (result.Updated > 0) parts.Add($"{result.Updated} updated");
            if (result.CategoriesCreated > 0) parts.Add($"{result.CategoriesCreated} categor{(result.CategoriesCreated == 1 ? "y" : "ies")} created");
            if (result.Skipped > 0) parts.Add($"{result.Skipped} row{(result.Skipped == 1 ? "" : "s")} with problems skipped");
            ResultText = string.Join(" · ", parts) + ".";
            Stage = ImportStage.Done;
            Imported?.Invoke(this, EventArgs.Empty);
        }
        catch (PosException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Import from {File} failed", Plan.FileName);
            ErrorMessage = "The import failed and nothing was saved. Please try again.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Close()
    {
        if (!IsBusy) Closed?.Invoke(this, EventArgs.Empty);
    }

    public bool HandleKey(Key key, bool textInputFocused)
    {
        if (key == Key.Escape) Close();
        // Modal: nothing behind the overlay reacts. Enter deliberately doesn't import.
        return true;
    }
}
