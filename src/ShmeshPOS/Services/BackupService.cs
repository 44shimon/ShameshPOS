using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using ShmeshPOS.Data;
using ShmeshPOS.Security;

namespace ShmeshPOS.Services;

/// <summary>Database backup settings, bound from the "Backup" configuration section (per machine).</summary>
public class BackupOptions
{
    public const string SectionName = "Backup";

    /// <summary>Take an automatic backup once a day on this machine.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Where backups are written. Default: %ProgramData%\ShmeshPOS\backups.</summary>
    public string Folder { get; set; } = "";

    /// <summary>Backups older than this are deleted after a successful backup.</summary>
    public int KeepDays { get; set; } = 30;

    /// <summary>Full path to pg_dump.exe. Default: the newest one under C:\Program Files\PostgreSQL, or PATH.</summary>
    public string PgDumpPath { get; set; } = "";

    public string ResolvedFolder => string.IsNullOrWhiteSpace(Folder) ? Path.Combine(App.DataDirectory, "backups") : Folder;
}

public sealed record BackupInfo(string Path, DateTime Time, long SizeBytes);

public interface IBackupService
{
    /// <summary>Newest backup in the backup folder, if any.</summary>
    BackupInfo? LastBackup();

    string Folder { get; }

    /// <summary>Runs pg_dump now. Throws <see cref="PosException"/> with a readable reason on failure.</summary>
    Task<BackupInfo> BackupNowAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Full backups with PostgreSQL's own pg_dump (custom format, restorable with pg_restore). The password is
/// passed to pg_dump through its environment, never on the command line or in a file.
/// </summary>
public class BackupService(
    IConfiguration configuration,
    IOptions<BackupOptions> options,
    ISessionService session,
    TimeProvider clock,
    ILogger<BackupService> logger) : IBackupService
{
    public const string FilePrefix = "shmesh_pos_";
    private readonly SemaphoreSlim _running = new(1, 1);

    public string Folder => options.Value.ResolvedFolder;

    public BackupInfo? LastBackup()
    {
        if (!Directory.Exists(Folder)) return null;
        return new DirectoryInfo(Folder).GetFiles($"{FilePrefix}*.backup")
            .Where(f => f.Length > 0)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => new BackupInfo(f.FullName, f.LastWriteTime, f.Length))
            .FirstOrDefault();
    }

    /// <summary>Manual backups need the settings permission; the daily scheduler runs without a user.</summary>
    public Task<BackupInfo> BackupNowAsync(CancellationToken cancellationToken = default)
    {
        if (!session.HasPermission(Permission.ManageSettings))
            throw new PosException("You are not allowed to run backups.");
        return RunAsync("manual", cancellationToken);
    }

    internal async Task<BackupInfo> RunAsync(string reason, CancellationToken cancellationToken)
    {
        if (!await _running.WaitAsync(0, cancellationToken))
            throw new PosException("A backup is already running.");
        try
        {
            var pgDump = FindPgDump()
                ?? throw new PosException("pg_dump.exe was not found. Install the PostgreSQL command-line tools on this computer, or set Backup:PgDumpPath.");
            var connection = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString(DbConfigurator.ConnectionStringName));

            Directory.CreateDirectory(Folder);
            var stamp = clock.GetLocalNow().ToString("yyyyMMdd_HHmmss");
            var file = Path.Combine(Folder, $"{FilePrefix}{stamp}.backup");
            var partial = file + ".partial";

            var start = new ProcessStartInfo(pgDump)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (var arg in new[]
            {
                "--host", connection.Host ?? "localhost",
                "--port", connection.Port.ToString(),
                "--username", connection.Username ?? "",
                "--dbname", connection.Database ?? "",
                "--format", "custom",
                "--no-password",
                "--file", partial,
            })
            {
                start.ArgumentList.Add(arg);
            }
            if (!string.IsNullOrEmpty(connection.Password))
                start.Environment["PGPASSWORD"] = connection.Password;

            var watch = Stopwatch.StartNew();
            using var process = Process.Start(start) ?? throw new PosException("pg_dump could not be started.");
            // Drain both pipes so pg_dump can never block on a full output buffer.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(TimeSpan.FromMinutes(15));
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                    File.Delete(partial);
                    throw new PosException("The backup took too long and was stopped.");
                }
            }
            await stdoutTask;
            var stderr = (await stderrTask).Trim();

            if (process.ExitCode != 0 || !File.Exists(partial) || new FileInfo(partial).Length == 0)
            {
                if (File.Exists(partial)) File.Delete(partial);
                logger.LogError("Backup ({Reason}) failed with exit code {ExitCode}: {Error}", reason, process.ExitCode, stderr);
                throw new PosException("The backup failed. See the log for details.");
            }

            File.Move(partial, file);
            var info = new FileInfo(file);
            logger.LogInformation("Backup ({Reason}) written to {File}: {Size:N0} bytes in {Seconds:0.0}s",
                reason, file, info.Length, watch.Elapsed.TotalSeconds);
            DeleteOldBackups();
            return new BackupInfo(file, info.LastWriteTime, info.Length);
        }
        finally
        {
            _running.Release();
        }
    }

    private void DeleteOldBackups()
    {
        var keepDays = Math.Max(1, options.Value.KeepDays);
        var cutoff = clock.GetUtcNow().UtcDateTime.AddDays(-keepDays);
        foreach (var old in new DirectoryInfo(Folder).GetFiles($"{FilePrefix}*.backup").Where(f => f.LastWriteTimeUtc < cutoff))
        {
            try
            {
                old.Delete();
                logger.LogInformation("Deleted old backup {File}", old.Name);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Could not delete old backup {File}", old.Name);
            }
        }
    }

    /// <summary>
    /// Configured path, else the PostgreSQL bundled by the installer (database server computers), else the newest
    /// PostgreSQL install's pg_dump, else pg_dump on PATH.
    /// </summary>
    public string? FindPgDump()
    {
        if (!string.IsNullOrWhiteSpace(options.Value.PgDumpPath))
            return File.Exists(options.Value.PgDumpPath) ? options.Value.PgDumpPath : null;

        var bundled = Path.Combine(AppContext.BaseDirectory, "pgsql", "bin", "pg_dump.exe");
        if (File.Exists(bundled)) return bundled;

        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL");
        if (Directory.Exists(root))
        {
            var newest = Directory.GetDirectories(root)
                .Select(d => (Dir: d, Version: int.TryParse(Path.GetFileName(d).Split('.')[0], out var v) ? v : 0))
                .OrderByDescending(d => d.Version)
                .Select(d => Path.Combine(d.Dir, "bin", "pg_dump.exe"))
                .FirstOrDefault(File.Exists);
            if (newest is not null) return newest;
        }

        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, "pg_dump.exe"))
            .FirstOrDefault(File.Exists);
    }
}

/// <summary>Takes one automatic backup per day while the POS is running on this machine.</summary>
public class DailyBackupScheduler(BackupService backups, IOptions<BackupOptions> options, ILogger<DailyBackupScheduler> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("Automatic backups are turned off on this machine");
            return;
        }

        // Registers normally have no PostgreSQL tools; backups are made on the machine that does (usually the
        // server). Say so once instead of logging a failure every hour.
        if (backups.FindPgDump() is null)
        {
            logger.LogWarning("Automatic backups are off on this machine: pg_dump.exe was not found. Install the PostgreSQL " +
                              "command-line tools or set Backup:PgDumpPath to back up from here, or set Backup:Enabled to false");
            return;
        }

        // Let the register start up (and apply migrations) before the first check.
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            var last = backups.LastBackup();
            if (last is null || last.Time < DateTime.Now.AddHours(-24))
            {
                try
                {
                    await backups.RunAsync("daily", stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Logged; the next hourly check tries again.
                    logger.LogError(ex, "Automatic backup failed");
                }
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
