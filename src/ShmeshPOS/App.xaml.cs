using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using ShmeshPOS.Data;
using ShmeshPOS.Infrastructure;
using ShmeshPOS.Services;
using ShmeshPOS.Services.Import;
using ShmeshPOS.ViewModels;
using ShmeshPOS.ViewModels.Admin;
using ShmeshPOS.Views;

namespace ShmeshPOS;

public partial class App : Application, IApplicationControl
{
    /// <summary>Machine-wide folder for this register's settings (connection string) and logs.</summary>
    public static readonly string DataDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ShmeshPOS");

    private IHost? _host;
    private bool _showingCrashMessage;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        RegisterGlobalExceptionHandlers();

        try
        {
            _host = BuildHost(e.Args);
            await _host.StartAsync();

            var options = _host.Services.GetRequiredService<IOptions<PosOptions>>().Value;
            ApplyCulture(options.Culture);
            Log.Information("Shmesh POS {Version} starting on terminal {Terminal}",
                typeof(App).Assembly.GetName().Version, options.TerminalName);

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = mainWindow;
            mainWindow.Show();

            await _host.Services.GetRequiredService<MainWindowViewModel>().StartAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Shmesh POS failed to start");
            MessageBox.Show("Shmesh POS could not start.\n\nSee the log in " + Path.Combine(DataDirectory, "logs"),
                "Shmesh POS", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static IHost BuildHost(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        // Settings precedence (last wins): appsettings.json beside the exe → per-machine file in
        // %ProgramData%\ShmeshPOS → user secrets (Debug builds) → SHMESHPOS_* environment variables.
        builder.Configuration
            .AddJsonFile(Path.Combine(DataDirectory, "appsettings.json"), optional: true, reloadOnChange: false);
#if DEBUG
        builder.Configuration.AddUserSecrets<App>(optional: true);
#endif
        builder.Configuration.AddEnvironmentVariables("SHMESHPOS_");

        builder.Services.AddSerilog((_, logger) => logger
            .ReadFrom.Configuration(builder.Configuration)
            .Enrich.FromLogContext()
            .WriteTo.Debug()
            .WriteTo.File(
                Path.Combine(DataDirectory, "logs", "shmeshpos-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 60,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));

        builder.Services.Configure<PosOptions>(builder.Configuration.GetSection(PosOptions.SectionName));
        builder.Services.PostConfigure<PosOptions>(o =>
        {
            if (string.IsNullOrWhiteSpace(o.TerminalName)) o.TerminalName = Environment.MachineName;
        });
        builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(DatabaseOptions.SectionName));
        builder.Services.Configure<SeedOptions>(builder.Configuration.GetSection(SeedOptions.SectionName));
        builder.Services.Configure<BackupOptions>(builder.Configuration.GetSection(BackupOptions.SectionName));

        var connectionString = builder.Configuration.GetConnectionString(DbConfigurator.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException($"Connection string '{DbConfigurator.ConnectionStringName}' is not configured.");
        builder.Services.AddDbContextFactory<PosDbContext>(options => DbConfigurator.Configure(options, connectionString));

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IApplicationControl>(_ => (App)Current);

        // Services
        builder.Services.AddSingleton<IDatabaseInitializer, DatabaseInitializer>();
        builder.Services.AddSingleton<ISessionService, SessionService>();
        builder.Services.AddSingleton<IAuthenticationService, AuthenticationService>();
        builder.Services.AddSingleton<IProductService, ProductService>();
        builder.Services.AddSingleton<IPaymentService, PaymentService>();
        builder.Services.AddSingleton<IReceiptNumberGenerator, ReceiptNumberGenerator>();
        builder.Services.AddSingleton<ISaleService, SaleService>();
        builder.Services.AddSingleton<ICatalogAdminService, CatalogAdminService>();
        builder.Services.AddSingleton<IUserAdminService, UserAdminService>();
        builder.Services.AddSingleton<ISettingsService, SettingsService>();
        builder.Services.AddSingleton<ICustomerService, CustomerService>();
        builder.Services.AddSingleton<IImportService, ImportService>();
        builder.Services.AddSingleton<IReportService, ReportService>();
        builder.Services.AddSingleton<IReceiptPrinter, ReceiptPrinter>();
        builder.Services.AddSingleton<IReceiptService, ReceiptService>();
        builder.Services.AddSingleton<BackupService>();
        builder.Services.AddSingleton<IBackupService>(sp => sp.GetRequiredService<BackupService>());
        builder.Services.AddHostedService<DailyBackupScheduler>();
        builder.Services.AddSingleton<IFileDialogService, FileDialogService>();

        // UI
        builder.Services.AddSingleton<DialogService>();
        builder.Services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<DialogService>());
        builder.Services.AddSingleton<MainWindowViewModel>();
        builder.Services.AddTransient<LoginViewModel>();
        builder.Services.AddTransient<PosViewModel>();
        builder.Services.AddTransient<AdministrationViewModel>();
        builder.Services.AddTransient<ReportsViewModel>();
        builder.Services.AddTransient<ProductsAdminViewModel>();
        builder.Services.AddTransient<CategoriesAdminViewModel>();
        builder.Services.AddTransient<UsersAdminViewModel>();
        builder.Services.AddTransient<CustomersAdminViewModel>();
        builder.Services.AddTransient<SettingsAdminViewModel>();
        builder.Services.AddSingleton(sp => new MainWindow(
            sp.GetRequiredService<MainWindowViewModel>(),
            sp.GetRequiredService<IOptions<PosOptions>>().Value));

        return builder.Build();
    }

    private static void ApplyCulture(string cultureName)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        // WPF bindings format with the element's Language (en-US by default), not CurrentCulture.
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));
    }

    public void ExitApplication()
    {
        if (MainWindow is MainWindow window)
            window.AllowClose = true;
        Shutdown();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        Log.Information("Shmesh POS shutting down");
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(3));
            _host.Dispose();
        }
        await Log.CloseAndFlushAsync();
        base.OnExit(e);
    }

    // ---- Error handling: log everything, never crash, never show a stack trace to the cashier ----

    private void RegisterGlobalExceptionHandlers()
    {
        // Bootstrap logger until the host's Serilog configuration takes over.
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Debug()
            .WriteTo.File(Path.Combine(DataDirectory, "logs", "startup-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
            .CreateLogger();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception (terminating: {IsTerminating})", args.IsTerminating);
    }

    private async void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled UI exception");
        e.Handled = true;

        // Avoid a loop if showing the message itself fails.
        if (_showingCrashMessage || _host is null) return;
        _showingCrashMessage = true;
        try
        {
            var message = e.Exception is PosException pos ? pos.Message : "Something went wrong.\nPlease try again.";
            await _host.Services.GetRequiredService<IDialogService>().ShowMessageAsync("Error", message);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to display error message");
        }
        finally
        {
            _showingCrashMessage = false;
        }
    }
}
