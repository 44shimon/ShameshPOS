namespace ShmeshPOS.Infrastructure;

/// <summary>Terminal-level settings, bound from the "Pos" configuration section.</summary>
public class PosOptions
{
    public const string SectionName = "Pos";

    public string StoreName { get; set; } = "Shmesh POS";

    /// <summary>Identifies this register on each sale. Defaults to the Windows machine name.</summary>
    public string TerminalName { get; set; } = Environment.MachineName;

    /// <summary>
    /// Kiosk mode: borderless full screen with no title bar, and only an administrator can exit (F3 → Exit).
    /// Off by default: the POS opens as a normal maximized window with minimize and close buttons.
    /// </summary>
    public bool FullScreen { get; set; }

    /// <summary>Culture for currency and number formatting.</summary>
    public string Culture { get; set; } = "en-US";

    public int MaxFailedLoginAttempts { get; set; } = 5;
    public int LoginLockoutSeconds { get; set; } = 30;
}

/// <summary>Database start-up behaviour, bound from the "Database" configuration section.</summary>
public class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Apply pending EF Core migrations at start-up.</summary>
    public bool AutoMigrate { get; set; } = true;
}

/// <summary>First-run seed data, bound from the "Seed" configuration section.</summary>
public class SeedOptions
{
    public const string SectionName = "Seed";

    public string AdminUsername { get; set; } = "admin";
    public string AdminDisplayName { get; set; } = "Administrator";

    /// <summary>PIN for the initial administrator, used only when the users table is empty.</summary>
    public string AdminPin { get; set; } = "";

    /// <summary>Create sample canteen categories and products when the catalog is empty. Off by default so a real install starts clean.</summary>
    public bool SampleData { get; set; }
}
