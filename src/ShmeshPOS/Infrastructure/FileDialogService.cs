using System.Windows;
using Microsoft.Win32;

namespace ShmeshPOS.Infrastructure;

/// <summary>Windows open/save dialogs, kept behind an interface so view models stay free of UI types.</summary>
public interface IFileDialogService
{
    /// <returns>The chosen path, or null if the user cancelled.</returns>
    string? PickImportFile(string title);

    /// <returns>The chosen path, or null if the user cancelled.</returns>
    string? PickSaveFile(string title, string suggestedName, string filter);
}

public class FileDialogService : IFileDialogService
{
    public string? PickImportFile(string title)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = "Spreadsheets (*.xlsx;*.xls;*.csv)|*.xlsx;*.xlsm;*.xls;*.csv;*.txt|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }

    public string? PickSaveFile(string title, string suggestedName, string filter)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            FileName = suggestedName,
            Filter = filter,
            OverwritePrompt = true,
        };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }
}
