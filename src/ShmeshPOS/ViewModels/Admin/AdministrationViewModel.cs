using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShmeshPOS.Infrastructure;
using ShmeshPOS.Security;
using ShmeshPOS.Services;

namespace ShmeshPOS.ViewModels.Admin;

public enum AdminSection
{
    Products,
    Categories,
    Customers,
    Users,
    Settings,
}

/// <summary>Back-office screen opened from F3 → Administration. Tabs are shown according to permissions.</summary>
public partial class AdministrationViewModel : ObservableObject, IKeyboardHandler
{
    private readonly IServiceProvider _services;
    private readonly ILogger<AdministrationViewModel> _logger;

    public AdministrationViewModel(IServiceProvider services, ISessionService session, ILogger<AdministrationViewModel> logger)
    {
        _services = services;
        _logger = logger;
        CanManageProducts = session.HasPermission(Permission.ManageProducts);
        CanManageCustomers = session.HasPermission(Permission.ManageCustomers);
        CanManageUsers = session.HasPermission(Permission.ManageUsers);
        CanManageSettings = session.HasPermission(Permission.ManageSettings);
        UserName = session.CurrentUser?.DisplayName ?? "";
    }

    public bool CanManageProducts { get; }
    public bool CanManageCustomers { get; }
    public bool CanManageUsers { get; }
    public bool CanManageSettings { get; }
    public string UserName { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProducts), nameof(IsCategories), nameof(IsCustomers), nameof(IsUsers), nameof(IsSettings))]
    private AdminSection? _selectedSection;

    /// <summary>The active tab's view model, rendered by its DataTemplate.</summary>
    [ObservableProperty]
    private IAdminSection? _currentSection;

    public bool IsProducts => SelectedSection == AdminSection.Products;
    public bool IsCategories => SelectedSection == AdminSection.Categories;
    public bool IsCustomers => SelectedSection == AdminSection.Customers;
    public bool IsUsers => SelectedSection == AdminSection.Users;
    public bool IsSettings => SelectedSection == AdminSection.Settings;

    /// <summary>Raised when the administrator returns to the register.</summary>
    public event EventHandler? Closed;

    public Task InitializeAsync()
    {
        var first = Enum.GetValues<AdminSection>().FirstOrDefault(Allowed, AdminSection.Products);
        return Allowed(first) ? ShowSectionAsync(first) : Task.CompletedTask;
    }

    [RelayCommand]
    private async Task ShowSectionAsync(AdminSection section)
    {
        if (section == SelectedSection || !Allowed(section)) return;
        if (CurrentSection is not null && !await CurrentSection.ConfirmDiscardAsync()) return;

        IAdminSection vm = section switch
        {
            AdminSection.Products => _services.GetRequiredService<ProductsAdminViewModel>(),
            AdminSection.Categories => _services.GetRequiredService<CategoriesAdminViewModel>(),
            AdminSection.Customers => _services.GetRequiredService<CustomersAdminViewModel>(),
            AdminSection.Users => _services.GetRequiredService<UsersAdminViewModel>(),
            _ => _services.GetRequiredService<SettingsAdminViewModel>(),
        };
        SelectedSection = section;
        CurrentSection = vm;
        _logger.LogDebug("Administration section {Section} opened", section);
        await vm.InitializeAsync();
    }

    [RelayCommand]
    private async Task BackAsync()
    {
        if (CurrentSection is not null && !await CurrentSection.ConfirmDiscardAsync()) return;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private bool Allowed(AdminSection section) => section switch
    {
        AdminSection.Products or AdminSection.Categories => CanManageProducts,
        AdminSection.Customers => CanManageCustomers,
        AdminSection.Users => CanManageUsers,
        _ => CanManageSettings,
    };

    public bool HandleKey(Key key, bool textInputFocused)
    {
        if (CurrentSection?.Import is { } import)
            return import.HandleKey(key, textInputFocused);
        if (key == Key.Escape)
        {
            BackCommand.Execute(null);
            return true;
        }
        if (key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            CurrentSection?.SaveCommand.Execute(null);
            return true;
        }
        // POS shortcuts do nothing here; everything else belongs to the form controls.
        return key is Key.F2 or Key.F3 or Key.F4 or Key.F8;
    }
}
