using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MAX.Bot.Interfaces;
using MAX.Bot.Interfaces.Models;
using MAX.Bot.Interfaces.Models.Request;

namespace MirageDiary.Server.Services;

public class MaxBotWebhookService : BackgroundService
{
    private const string HealthKey = "max";
    private const string HealthName = "MAX-бот";

    private readonly IMaxBotClient _botClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MaxBotWebhookService> _logger;
    private readonly ServiceHealthService _health;
    private readonly ServerEventLog _eventLog;

    public MaxBotWebhookService(
        IMaxBotClient botClient,
        IConfiguration configuration,
        ILogger<MaxBotWebhookService> logger,
        ServiceHealthService health,
        ServerEventLog eventLog)
    {
        _botClient = botClient;
        _configuration = configuration;
        _logger = logger;
        _health = health;
        _eventLog = eventLog;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[MaxWebhook] Запуск службы управления Webhook для MAX-бота...");
        _health.Register(HealthKey, HealthName);

        string? webhookUrl = (_configuration["MaxBot:WebhookUrl"] ?? _configuration["MaxWebhookUrl"])?.Trim();
        if (string.IsNullOrEmpty(webhookUrl))
        {
            var baseMiniApp = _configuration["MiniAppUrl"]?.TrimEnd('/');
            if (!string.IsNullOrEmpty(baseMiniApp))
            {
                webhookUrl = $"{baseMiniApp}/api/MaxBot";
            }
            else
            {
                webhookUrl = "https://miragediary.space/api/MaxBot";
            }
        }

        string? secret = (_configuration["MaxBot:Secret"] ?? _configuration["MaxBotSecret"])?.Trim();

        var desiredTypes = new List<string>
        {
            UpdateTypes.MessageCreated,
            UpdateTypes.MessageCallback,
            UpdateTypes.BotStarted
        };

        bool announced = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Проверяем текущее состояние подписок на platform-api2.max.ru
                var subsResponse = await _botClient.GetSubscriptionsAsync(stoppingToken);
                var activeSubs = subsResponse?.Subscriptions ?? new List<Subscription>();

                var matchingSub = activeSubs.FirstOrDefault(s => string.Equals(s.Url, webhookUrl, StringComparison.OrdinalIgnoreCase));

                // Удаляем устаревшие / чужие подписки, если URL изменился
                foreach (var oldSub in activeSubs.Where(s => !string.Equals(s.Url, webhookUrl, StringComparison.OrdinalIgnoreCase)))
                {
                    if (!string.IsNullOrEmpty(oldSub.Url))
                    {
                        _logger.LogInformation($"[MaxWebhook] Удаление устаревшей подписки MAX: {oldSub.Url}");
                        try
                        {
                            await _botClient.UnsubscribeAsync(new DeleteSubscriptionRequest { Url = oldSub.Url }, stoppingToken);
                        }
                        catch (Exception uex)
                        {
                            _logger.LogWarning($"[MaxWebhook] Не удалось удалить подписку {oldSub.Url}: {uex.Message}");
                        }
                    }
                }

                if (matchingSub == null)
                {
                    _logger.LogInformation($"[MaxWebhook] Регистрирую Webhook в MAX: {webhookUrl}...");
                    var subReq = new SubscriptionRequest
                    {
                        Url = webhookUrl,
                        UpdateTypes = desiredTypes,
                        Secret = secret
                    };

                    var res = await _botClient.SubscribeAsync(subReq, stoppingToken);
                    if (res?.Success == true)
                    {
                        _logger.LogInformation($"[MaxWebhook] ✅ Webhook успешно зарегистрирован в MAX: {webhookUrl}");
                        _health.ReportRunning(HealthKey, HealthName);
                        _eventLog.Success(HealthName, $"Webhook активен: {webhookUrl}");
                        announced = true;
                    }
                    else
                    {
                        string errMsg = res?.Message ?? "Неизвестный ответ от API MAX";
                        _logger.LogWarning($"[MaxWebhook] Ошибка регистрации Webhook: {errMsg}");
                        _health.ReportFailure(HealthKey, HealthName, errMsg, fatal: false);
                    }
                }
                else
                {
                    if (!announced)
                    {
                        _logger.LogInformation($"[MaxWebhook] ✅ Подписка на Webhook уже активна на стороне MAX: {webhookUrl}");
                        _health.ReportRunning(HealthKey, HealthName);
                        _eventLog.Success(HealthName, $"Webhook активен: {webhookUrl}");
                        announced = true;
                    }
                    else
                    {
                        _health.ReportRunning(HealthKey, HealthName);
                    }
                }

                // Сторожевой интервал: проверка подписки каждые 15 минут
                await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[MaxWebhook] Ошибка при проверке/регистрации Webhook MAX. Повтор через 30 секунд...");
                _health.ReportFailure(HealthKey, HealthName, ex.Message, fatal: false);
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }

        _logger.LogInformation("[MaxWebhook] Служба управления Webhook остановлена.");
    }
}
