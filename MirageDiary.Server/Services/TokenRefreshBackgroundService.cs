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
/// Background service that periodically refreshes tokens for all active users in the database
/// every 2 hours to prevent RefreshTokens from expiring.
/// </summary>
public class TokenRefreshBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TokenRefreshBackgroundService> _logger;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);

    public TokenRefreshBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<TokenRefreshBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[TokenRefreshWorker] Фоновый сервис авто-обновления токенов запущен (интервал: 15 минут).");
        bool maintenanceAnnounced = false;

        // Initial 10-second delay on application start
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using (var scope = _serviceProvider.CreateScope())
                {
                    var config = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
                    if (DiaryMaintenanceSettings.IsTokenRefreshDisabled(config))
                    {
                        if (!maintenanceAnnounced)
                        {
                            _logger.LogInformation("[TokenRefreshWorker] ⏸️ Обновление токенов приостановлено на время тех. работ дневника (до 15.09).");
                            maintenanceAnnounced = true;
                        }
                        await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                        continue;
                    }
                    maintenanceAnnounced = false;
                }

                await RefreshAllTokensAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TokenRefreshWorker] Ошибка выполнения цикла обновления токенов.");
            }

            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task RefreshAllTokensAsync(CancellationToken stoppingToken = default)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenStore = scope.ServiceProvider.GetRequiredService<ITokenStore>();
        var httpClientFactory = scope.ServiceProvider.GetRequiredService<System.Net.Http.IHttpClientFactory>();

        var users = await dbContext.Users
            .Where(u => !string.IsNullOrEmpty(u.AccessToken) || !string.IsNullOrEmpty(u.RefreshToken))
            .ToListAsync(stoppingToken);

        if (users.Count == 0)
        {
            _logger.LogInformation("[TokenRefreshWorker] Нет пользователей с сохранёнными токенами.");
            return;
        }

        _logger.LogInformation($"[TokenRefreshWorker] Запуск проверки и авто-продления для {users.Count} пользователей...");

        foreach (var user in users)
        {
            if (stoppingToken.IsCancellationRequested) break;

            try
            {
                var tokenData = await tokenStore.GetTokensAsync(user.Id);
                if (tokenData == null || string.IsNullOrWhiteSpace(tokenData.AccessToken))
                    continue;

                var expUtc = DiaryClient.GetTokenExpirationUtc(tokenData.AccessToken);
                bool isMobile = !string.IsNullOrEmpty(tokenData.DeviceId) && !string.IsNullOrEmpty(tokenData.Oid);
                bool attemptedPlaywright = false;

                // Proactive refresh if token expires within 60 minutes or if exp is null/already expired
                bool needsProactiveRefresh = expUtc == null || (expUtc.Value - DateTime.UtcNow) < TimeSpan.FromMinutes(60);

                if (needsProactiveRefresh)
                {
                    if (isMobile)
                    {
                        try
                        {
                            double remMin = expUtc != null ? (expUtc.Value - DateTime.UtcNow).TotalMinutes : -1;
                            _logger.LogInformation($"[TokenRefreshWorker] 📱 Проактивное продление мобильной сессии для {user.Id} (осталось {remMin:F1} мин)...");
                            var mobileClient = new MobileEsiaClient(httpClientFactory.CreateClient());
                            string entToken = MobileEsiaClient.BuildEntToken(tokenData.DeviceId);
                            var renewResult = await mobileClient.CreateSessionAsync(tokenData.DeviceId, tokenData.Oid, tokenData.AccessToken, entToken);
                            if (renewResult != null && !string.IsNullOrEmpty(renewResult.AccessToken))
                            {
                                tokenData.AccessToken = renewResult.AccessToken;
                                tokenData.SessionId = renewResult.SessionId;
                                await tokenStore.SaveTokensAsync(user.Id, tokenData);
                                var newExp = DiaryClient.GetTokenExpirationUtc(renewResult.AccessToken);
                                _logger.LogInformation($"[TokenRefreshWorker] 🚀 Мобильная сессия успешно продлена для {user.Id}! Новый срок: {newExp:yyyy-MM-dd HH:mm:ss} UTC");
                                continue;
                            }
                            else
                            {
                                _logger.LogWarning($"[TokenRefreshWorker] ⚠️ createSession вернул пустой результат для {user.Id}");
                            }
                        }
                        catch (Exception mex)
                        {
                            _logger.LogWarning($"[TokenRefreshWorker] ⚠️ Ошибка при вызове createSession для {user.Id}: {mex.Message}");
                        }
                    }
                    else
                    {
                        attemptedPlaywright = true;
                        _logger.LogInformation($"[TokenRefreshWorker] 🌐 Проактивное продление веб-сессии через Playwright для {user.Id}...");
                        var refreshedCookies = await PlaywrightSessionRefresher.TryRefreshCookiesAsync(tokenData.AccessToken);
                        if (!string.IsNullOrEmpty(refreshedCookies))
                        {
                            tokenData.AccessToken = refreshedCookies;
                            tokenData.RefreshToken = refreshedCookies;
                            await tokenStore.SaveTokensAsync(user.Id, tokenData);
                            _logger.LogInformation($"[TokenRefreshWorker] 🚀 Веб-сессия успешно продлена для {user.Id}!");
                            continue;
                        }
                    }
                }

                // If token is still healthy with plenty of time remaining, verify it
                var client = httpClientFactory.CreateClient();
                var diaryClient = new DiaryClient(client, tokenStore, user.Id);
                
                try
                {
                    await diaryClient.RefreshTokensAsync();
                    double remMin = expUtc != null ? (expUtc.Value - DateTime.UtcNow).TotalMinutes : 0;
                    _logger.LogInformation($"[TokenRefreshWorker] ✅ Токены активны для пользователя {user.Id} (осталось {remMin:F1} мин).");
                }
                catch (DiarySessionExpiredException)
                {
                    _logger.LogInformation($"[TokenRefreshWorker] 🔄 Сессия устарела для {user.Id}. Запуск экстренного авто-продления...");
                    bool renewed = false;
                    
                    if (isMobile)
                    {
                        try
                        {
                            var mobileClient = new MobileEsiaClient(httpClientFactory.CreateClient());
                            string entToken = MobileEsiaClient.BuildEntToken(tokenData.DeviceId);
                            var renewResult = await mobileClient.CreateSessionAsync(tokenData.DeviceId, tokenData.Oid, tokenData.AccessToken, entToken);
                            if (renewResult != null && !string.IsNullOrEmpty(renewResult.AccessToken))
                            {
                                tokenData.AccessToken = renewResult.AccessToken;
                                tokenData.SessionId = renewResult.SessionId;
                                await tokenStore.SaveTokensAsync(user.Id, tokenData);
                                _logger.LogInformation($"[TokenRefreshWorker] 🚀 Экстренно продлена мобильная сессия для {user.Id}!");
                                renewed = true;
                                continue;
                            }
                        }
                        catch (Exception mex)
                        {
                            _logger.LogWarning($"[TokenRefreshWorker] ⚠️ Экстренная ошибка createSession: {mex.Message}");
                        }
                    }

                    if (!renewed && !isMobile && !attemptedPlaywright)
                    {
                        var refreshedCookies = await PlaywrightSessionRefresher.TryRefreshCookiesAsync(tokenData.AccessToken);
                        if (!string.IsNullOrEmpty(refreshedCookies))
                        {
                            tokenData.AccessToken = refreshedCookies;
                            tokenData.RefreshToken = refreshedCookies;
                            await tokenStore.SaveTokensAsync(user.Id, tokenData);
                            _logger.LogInformation($"[TokenRefreshWorker] 🚀 Сессия успешно продлена через Playwright для {user.Id}!");
                            renewed = true;
                        }
                    }

                    if (!renewed)
                    {
                        _logger.LogWarning($"[TokenRefreshWorker] ⚠️ Не удалось продлить сессию для {user.Id} (сессия окончательно истекла). Сбрасываем недействительные токены...");
                        await tokenStore.RevokeTokensAsync(user.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[TokenRefreshWorker] ⚠️ Исключение при проверке токена для {user.Id}: {ex.Message}");
            }

            // Stagger requests smoothly (5 requests/sec max per thread) to prevent IP rate limits
            try
            {
                await Task.Delay(200, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
