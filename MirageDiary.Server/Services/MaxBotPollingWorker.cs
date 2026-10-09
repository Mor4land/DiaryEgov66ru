using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MAX.Bot.Interfaces;
using MAX.Bot.Interfaces.Models;

namespace MirageDiary.Server.Services;

public class MaxBotPollingWorker : BackgroundService
{
    private const string HealthKey = "max";
    private const string HealthName = "MAX-бот";

    private readonly IMaxBotClient _botClient;
    private readonly IServiceProvider _services;
    private readonly ILogger<MaxBotPollingWorker> _logger;
    private readonly ServiceHealthService _health;
    private readonly ServerEventLog _eventLog;

    public MaxBotPollingWorker(
        IMaxBotClient botClient,
        IServiceProvider services,
        ILogger<MaxBotPollingWorker> logger,
        ServiceHealthService health,
        ServerEventLog eventLog)
    {
        _botClient = botClient;
        _services = services;
        _logger = logger;
        _health = health;
        _eventLog = eventLog;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting MAX Bot Long Polling worker...");
        _health.Register(HealthKey, HealthName);

        try
        {
            var subs = await _botClient.GetSubscriptionsAsync(stoppingToken);
            if (subs?.Subscriptions != null)
            {
                foreach (var s in subs.Subscriptions)
                {
                    if (!string.IsNullOrEmpty(s.Url))
                    {
                        _logger.LogInformation($"[MaxPolling] Удаление существующего Webhook перед поллингом: {s.Url}");
                        await _botClient.UnsubscribeAsync(new MAX.Bot.Interfaces.Models.Request.DeleteSubscriptionRequest { Url = s.Url }, stoppingToken);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"[MaxPolling] Предупреждение при очистке подписок Webhook: {ex.Message}");
        }

        bool announced = false;
        long? marker = null;

        var request = new MAX.Bot.Interfaces.Models.Request.GetUpdatesRequest
        {
            Limit = 100,
            Timeout = 30,
            Types = new List<string> { UpdateTypes.MessageCreated, UpdateTypes.MessageCallback, UpdateTypes.BotStarted }
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _health.ReportRunning(HealthKey, HealthName);
                if (!announced)
                {
                    _eventLog.Success(HealthName, "Long polling запущен, слушаю обновления");
                    announced = true;
                }

                request.Marker = marker;
                var response = await _botClient.GetUpdatesAsync(request, stoppingToken);

                if (response?.Updates != null && response.Updates.Count > 0)
                {
                    foreach (var update in response.Updates)
                    {
                        if (stoppingToken.IsCancellationRequested) break;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                using var scope = _services.CreateScope();
                                var maxCommandService = scope.ServiceProvider.GetRequiredService<MaxCommandService>();
                                await maxCommandService.HandleUpdateAsync(update);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error processing MAX update");
                                _eventLog.Error(HealthName, $"Ошибка обработки обновления: {ex.Message}");
                            }
                        }, stoppingToken);
                    }
                }

                if (response?.Marker != null)
                {
                    marker = response.Marker;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("MAX Bot polling error: {Message}. Retrying immediately...", ex.Message);
                _health.ReportFailure(HealthKey, HealthName, ex.Message, fatal: false);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }
}
