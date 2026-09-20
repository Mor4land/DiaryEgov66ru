using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MirageDiary.Server.Data;

namespace MirageDiary.Server.Services;

public class DatabaseBackupService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<DatabaseBackupService> _logger;
    private static readonly System.Threading.SemaphoreSlim _backupSemaphore = new(1, 1);

    public DatabaseBackupService(IServiceProvider services, ILogger<DatabaseBackupService> logger)
    {
        _services = services;
        _logger = logger;
    }

    /// <summary>
    /// Performs an atomic, non-blocking point-in-time snapshot of the active SQLite database
    /// using SQLite's native VACUUM INTO command, guaranteeing zero WAL corruption during live operations.
    /// </summary>
    public async Task<string?> CreateBackupAsync(string reason = "auto", System.Threading.CancellationToken ct = default)
    {
        if (!await _backupSemaphore.WaitAsync(TimeSpan.FromSeconds(30), ct))
        {
            _logger.LogWarning("[Backup] ⚠️ Another backup operation is already running. Skipping request ({Reason}).", reason);
            return null;
        }

        try
        {
            string baseDir = AppContext.BaseDirectory;
            string persistentDataDir = Path.Combine(baseDir, "data");
            string backupDir = Directory.Exists(persistentDataDir)
                ? Path.Combine(persistentDataDir, "backups")
                : Path.Combine(baseDir, "backups");
            Directory.CreateDirectory(backupDir);

            string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
            string backupFileName = $"miragediary_{timestamp}_{reason}.db";
            string backupPath = Path.Combine(backupDir, backupFileName);

            if (File.Exists(backupPath))
            {
                backupFileName = $"miragediary_{timestamp}_{Guid.NewGuid():N}_{reason}.db";
                backupPath = Path.Combine(backupDir, backupFileName);
            }

            using (var scope = _services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                
                // Native SQLite atomic snapshot
                string escapedPath = backupPath.Replace("'", "''");
#pragma warning disable EF1002
                await db.Database.ExecuteSqlRawAsync($"VACUUM INTO '{escapedPath}'", ct);
#pragma warning restore EF1002
            }

            var fileInfo = new FileInfo(backupPath);
            _logger.LogInformation("[Backup] ✅ Snapshot created: {Path} ({Size} KB) [Reason: {Reason}]",
                backupFileName, fileInfo.Length / 1024, reason);

            // Prune old backups to keep last 20 snapshots
            PruneOldBackups(backupDir, maxKeep: 20);

            return backupPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Backup] ❌ Failed to create database backup snapshot.");
            return null;
        }
        finally
        {
            _backupSemaphore.Release();
        }
    }

    private void PruneOldBackups(string backupDir, int maxKeep)
    {
        try
        {
            var files = new DirectoryInfo(backupDir)
                .GetFiles("miragediary_*.db")
                .OrderByDescending(f => f.CreationTimeUtc)
                .ToList();

            if (files.Count > maxKeep)
            {
                foreach (var oldFile in files.Skip(maxKeep))
                {
                    try
                    {
                        oldFile.Delete();
                        _logger.LogInformation("[Backup] 🗑️ Pruned old backup snapshot: {Name}", oldFile.Name);
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Backup] ⚠️ Error during old backup rotation.");
        }
    }
}
