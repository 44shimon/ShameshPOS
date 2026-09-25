using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShmeshPOS.Infrastructure;

namespace ShmeshPOS.ViewModels;

public partial class MenuItemViewModel(string shortcut, string label, Func<Task> action, bool isEnabled = true) : ObservableObject
{
    public string Shortcut { get; } = shortcut;
    public string Label { get; } = label;
    public bool IsEnabled { get; } = isEnabled;
    public Func<Task> Action { get; } = action;

    [ObservableProperty]
    private bool _isHighlighted;
}

/// <summary>
/// F3 menu overlay. Navigable with arrow keys + Enter, or the number shown beside each item.
/// Selection is tracked here rather than via WPF focus so keyboard behaviour is predictable.
/// </summary>
public partial class MenuViewModel : ObservableObject, IKeyboardHandler
{
    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private IReadOnlyList<MenuItemViewModel> _items = [];

    private int _highlightIndex;

    public void Open(IReadOnlyList<MenuItemViewModel> items)
    {
        Items = items;
        _highlightIndex = -1;
        Move(1);
        IsOpen = true;
    }

    [RelayCommand]
    public void Close() => IsOpen = false;

    [RelayCommand]
    private async Task ActivateAsync(MenuItemViewModel? item)
    {
        if (item is not { IsEnabled: true }) return;
        Close();
        await item.Action();
    }

    public bool HandleKey(Key key, bool textInputFocused)
    {
        switch (key)
        {
            case Key.Escape:
            case Key.F3:
                Close();
                return true;
            case Key.Up:
                Move(-1);
                return true;
            case Key.Down:
            case Key.Tab:
                Move(1);
                return true;
            case Key.Enter:
                if (_highlightIndex >= 0) ActivateCommand.Execute(Items[_highlightIndex]);
                return true;
        }

        if (KeyInput.ToDigit(key) is { } digit)
        {
            var item = Items.FirstOrDefault(i => i.Shortcut == digit.ToString());
            if (item is not null) ActivateCommand.Execute(item);
        }
        // The menu is modal: swallow everything else.
        return true;
    }

    private void Move(int direction)
    {
        if (Items.Count == 0 || !Items.Any(i => i.IsEnabled)) return;

        var index = _highlightIndex;
        do
        {
            index = (index + direction + Items.Count) % Items.Count;
        } while (!Items[index].IsEnabled);

        if (_highlightIndex >= 0) Items[_highlightIndex].IsHighlighted = false;
        _highlightIndex = index;
        Items[index].IsHighlighted = true;
    }
}
