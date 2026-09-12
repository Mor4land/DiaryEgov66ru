using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MirageDiary.Server.Services;

public class DatabaseBackupWorker : BackgroundService
{
    private readonly DatabaseBackupService _backupService;
    private readonly ILogger<DatabaseBackupWorker> _logger;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    public DatabaseBackupWorker(DatabaseBackupService backupService, ILogger<DatabaseBackupWorker> logger)
    {
        _backupService = backupService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[BackupWorker] 🛡️ Automated SQLite backup worker started (Interval: 6 hours).");

        // Take startup snapshot
        await _backupService.CreateBackupAsync("startup");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, stoppingToken);
                await _backupService.CreateBackupAsync("scheduled");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BackupWorker] ❌ Error in periodic backup loop.");
            }
        }
    }
}
