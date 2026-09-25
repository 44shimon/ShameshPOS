using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShmeshPOS.Infrastructure;

namespace ShmeshPOS.ViewModels;

public interface IDialogService
{
    Task ShowMessageAsync(string title, string message, string okText = "OK");
    Task<bool> ConfirmAsync(string title, string message, string confirmText = "Yes", string cancelText = "No");
}

public partial class DialogViewModel(string title, string message, string confirmText, string? cancelText) : ObservableObject
{
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Title { get; } = title;
    public string Message { get; } = message;
    public string ConfirmText { get; } = confirmText;

    /// <summary>Null for a message box with a single button.</summary>
    public string? CancelText { get; } = cancelText;

    public Task<bool> Result => _result.Task;

    [RelayCommand]
    private void Confirm() => _result.TrySetResult(true);

    [RelayCommand]
    private void Cancel() => _result.TrySetResult(false);
}

/// <summary>
/// In-window modal dialogs (no separate Windows dialog boxes, which break the kiosk look).
/// One dialog is shown at a time; later requests wait their turn.
/// </summary>
public partial class DialogService : ObservableObject, IDialogService, IKeyboardHandler
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    [ObservableProperty]
    private DialogViewModel? _current;

    public Task ShowMessageAsync(string title, string message, string okText = "OK") =>
        ShowAsync(new DialogViewModel(title, message, okText, null));

    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "Yes", string cancelText = "No") =>
        ShowAsync(new DialogViewModel(title, message, confirmText, cancelText));

    private async Task<bool> ShowAsync(DialogViewModel dialog)
    {
        await _gate.WaitAsync();
        try
        {
            Current = dialog;
            return await dialog.Result;
        }
        finally
        {
            Current = null;
            _gate.Release();
        }
    }

    public bool HandleKey(Key key, bool textInputFocused)
    {
        if (Current is not { } dialog)
            return false;

        switch (key)
        {
            case Key.Enter:
                dialog.ConfirmCommand.Execute(null);
                break;
            case Key.Escape:
                dialog.CancelCommand.Execute(null);
                break;
        }
        // While a dialog is open, nothing underneath should react to the keyboard.
        return true;
    }
}
