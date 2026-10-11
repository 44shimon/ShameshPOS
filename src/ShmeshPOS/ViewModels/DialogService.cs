using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShmeshPOS.Infrastructure;

namespace ShmeshPOS.ViewModels;

public interface IDialogService
{
    Task ShowMessageAsync(string title, string message, string okText = "OK");
    Task<bool> ConfirmAsync(string title, string message, string confirmText = "Yes", string cancelText = "No");

    /// <summary>
    /// A message with a main button and an optional side action (e.g. "Print Receipt").
    /// Enter and Esc both choose the main button. Returns true only when the side action was pressed.
    /// </summary>
    Task<bool> ShowMessageWithActionAsync(string title, string message, string okText, string actionText);

    /// <summary>Asks for a short piece of text. Returns null when cancelled (the text may be empty).</summary>
    Task<string?> PromptAsync(string title, string message, string confirmText, string cancelText = "Cancel", int maxLength = 60);
}

public partial class DialogViewModel(string title, string message, string confirmText, string? cancelText, bool escapeConfirms = false) : ObservableObject
{
    /// <summary>When true, Esc presses the main button instead of the secondary one.</summary>
    public bool EscapeConfirms { get; } = escapeConfirms;

    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Title { get; } = title;
    public string Message { get; } = message;
    public string ConfirmText { get; } = confirmText;

    /// <summary>Null for a message box with a single button.</summary>
    public string? CancelText { get; } = cancelText;

    /// <summary>Shows a text box (see <see cref="IDialogService.PromptAsync"/>).</summary>
    public bool HasInput { get; init; }
    public int InputMaxLength { get; init; } = 60;

    [ObservableProperty]
    private string _inputText = "";

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

    public async Task<bool> ShowMessageWithActionAsync(string title, string message, string okText, string actionText) =>
        !await ShowAsync(new DialogViewModel(title, message, okText, actionText, escapeConfirms: true));

    public async Task<string?> PromptAsync(string title, string message, string confirmText, string cancelText = "Cancel", int maxLength = 60)
    {
        var dialog = new DialogViewModel(title, message, confirmText, cancelText) { HasInput = true, InputMaxLength = maxLength };
        return await ShowAsync(dialog) ? dialog.InputText.Trim() : null;
    }

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
            case Key.Escape when dialog.EscapeConfirms:
                dialog.ConfirmCommand.Execute(null);
                break;
            case Key.Escape:
                dialog.CancelCommand.Execute(null);
                break;
        }
        // While a dialog is open, nothing underneath should react to the keyboard; typing goes to its text box.
        return key is Key.Enter or Key.Escape || !(dialog.HasInput && textInputFocused);
    }
}
