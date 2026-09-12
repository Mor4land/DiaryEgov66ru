using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MirageDiary.Core;
using MirageDiary.Server.Data;

namespace MirageDiary.Server.Services;

/// <summary>
/// Фоновый воркер, удаляющий неактивные аккаунты пользователей, не проявлявших активность более 30 дней.
/// </summary>
public class AccountCleanupWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<AccountCleanupWorker> _logger;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(12);

    public AccountCleanupWorker(IServiceProvider services, ILogger<AccountCleanupWorker> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[AccountCleanupWorker] Сервис авто-очистки неактивных аккаунтов (30 дней) запущен.");

        // Пауза 1 минута после старта
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupInactiveAccountsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AccountCleanupWorker] Ошибка при авто-очистке неактивных аккаунтов.");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task CleanupInactiveAccountsAsync(CancellationToken stoppingToken)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenStore = scope.ServiceProvider.GetRequiredService<ITokenStore>();
        var eventLog = scope.ServiceProvider.GetService<ServerEventLog>();

        var cutoffUtc = DateTime.UtcNow.AddDays(-30);

        var staleUsers = await db.Users
            .Where(u => u.Role != "Admin" && u.UpdatedAt != null && u.UpdatedAt < cutoffUtc)
            .ToListAsync(stoppingToken);

        if (staleUsers.Count == 0)
        {
            return;
        }

        _logger.LogInformation($"[AccountCleanupWorker] Найдено {staleUsers.Count} неактивных аккаунтов для удаления (>30 дней без активности)...");

        int deletedCount = 0;
        foreach (var user in staleUsers)
        {
            if (stoppingToken.IsCancellationRequested) break;

            try
            {
                await tokenStore.RevokeTokensAsync(user.Id);
                db.Users.Remove(user);
                deletedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"[AccountCleanupWorker] Ошибка удаления пользователя {user.Id}");
            }
        }

        await db.SaveChangesAsync(stoppingToken);
        _logger.LogInformation($"[AccountCleanupWorker] ✅ Успешно удалено {deletedCount} неактивных аккаунтов.");
        eventLog?.Info("База данных", $"Авто-очистка: удалено {deletedCount} неактивных аккаунтов (>30 дней)");
    }
}
