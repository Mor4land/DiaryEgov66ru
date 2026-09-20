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

        // Аккаунт считается действительно заброшенным ТОЛЬКО если:
        // 1. Не является администратором или завучем
        // 2. Не проявлял активности более 30 дней
        // 3. Нет сохраненных сессионных токенов (уже вышел или не авторизован)
        // 4. Нет активной платной или бессрочной подписки
        // 5. Не включены активные умные будильники / напоминания
        var staleUsers = await db.Users
            .Where(u => u.Role != "Admin" && u.Role != "HeadTeacher" &&
                        u.UpdatedAt != null && u.UpdatedAt < cutoffUtc &&
                        string.IsNullOrEmpty(u.AccessToken) && string.IsNullOrEmpty(u.RefreshToken) &&
                        !u.IsSubscribed &&
                        (u.SubscriptionExpiresAtUtc == null || u.SubscriptionExpiresAtUtc < DateTime.UtcNow) &&
                        (!u.IsAlarmEnabled || (!u.IsScheduleReminderEnabled && !u.IsHomeworkReminderEnabled && !u.IsEquipmentReminderEnabled)))
            .ToListAsync(stoppingToken);

        if (staleUsers.Count == 0)
        {
            return;
        }

        _logger.LogInformation($"[AccountCleanupWorker] Найдено {staleUsers.Count} заброшенных аккаунтов для удаления (>30 дней без токенов/будильников)...");

        int deletedCount = 0;
        foreach (var user in staleUsers)
        {
            if (stoppingToken.IsCancellationRequested) break;

            try
            {
                // Отвязываем парный аккаунт, чтобы не оставлять висячих ссылок в LinkedUserId
                if (!string.IsNullOrEmpty(user.LinkedUserId))
                {
                    var peer = await db.Users.FirstOrDefaultAsync(u => u.Id == user.LinkedUserId, stoppingToken);
                    if (peer != null && peer.LinkedUserId == user.Id)
                    {
                        peer.LinkedUserId = "";
                    }
                }

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
