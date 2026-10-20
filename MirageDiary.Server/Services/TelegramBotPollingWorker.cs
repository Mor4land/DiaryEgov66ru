using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;

namespace MirageDiary.Server.Services;

public class TelegramBotPollingWorker : BackgroundService
{
    private const string HealthKey = "telegram";
    private const string HealthName = "Telegram-бот";

    private readonly ITelegramBotClient _botClient;
    private readonly IServiceProvider _services;
    private readonly ILogger<TelegramBotPollingWorker> _logger;
    private readonly ServiceHealthService _health;
    private readonly ServerEventLog _eventLog;
    private readonly ServerMetricsService _metrics;

    public TelegramBotPollingWorker(
        ITelegramBotClient botClient,
        IServiceProvider services,
        ILogger<TelegramBotPollingWorker> logger,
        ServiceHealthService health,
        ServerEventLog eventLog,
        ServerMetricsService metrics)
    {
        _botClient = botClient;
        _services = services;
        _logger = logger;
        _health = health;
        _eventLog = eventLog;
        _metrics = metrics;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting Telegram Bot Long Polling...");
        if (_botClient.BotId == 123456789)
        {
            _logger.LogWarning("BotToken is not configured (dummy ID). Polling disabled for local dev.");
            _health.ReportNotConfigured(HealthKey, HealthName);
            return;
        }
        _health.Register(HealthKey, HealthName);

        try
        {
            // Delete webhook so Telegram forwards all updates via Long Polling
            await _botClient.DeleteWebhook(cancellationToken: stoppingToken);
            _logger.LogInformation("Webhook deleted. Listening via Long Polling.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete webhook before long polling.");
            _eventLog.Warning(HealthName, $"Не удалось снять webhook перед поллингом: {ex.Message}");
        }

        try
        {
            var commands = new[]
            {
                new BotCommand { Command = "start", Description = "🏠 Главное меню" },
                new BotCommand { Command = "schedule", Description = "📅 Расписание уроков" },
                new BotCommand { Command = "homework", Description = "📚 Домашнее задание" },
                new BotCommand { Command = "marks", Description = "📊 Оценки и средний балл" },
                new BotCommand { Command = "calc", Description = "🎯 Калькулятор оценок и целей" },
                new BotCommand { Command = "now", Description = "🔔 Сколько до звонка" },
                new BotCommand { Command = "alarm", Description = "🎒 Умный будильник и рюкзак" },
                new BotCommand { Command = "children", Description = "👨‍👧 Выбор ребенка" },
                new BotCommand { Command = "profile", Description = "👤 Данные профиля ученика" },
                new BotCommand { Command = "settings", Description = "⚙️ Настройки и напоминания" },
                new BotCommand { Command = "link", Description = "🔗 Связка с MAX мессенджером" },
                new BotCommand { Command = "logout", Description = "🔒 Выйти из аккаунта" },
                new BotCommand { Command = "help", Description = "ℹ️ Справка по всем командам" },
                new BotCommand { Command = "stop", Description = "🚪 Выход и удаление данных" }
            };

            await _botClient.SetMyCommands(commands, cancellationToken: stoppingToken);
            _logger.LogInformation("Telegram bot commands registered successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to register Telegram bot commands.");
            _eventLog.Warning(HealthName, $"Не удалось зарегистрировать команды бота: {ex.Message}");
        }

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = Array.Empty<Telegram.Bot.Types.Enums.UpdateType>()
        };

        bool announced = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Вход в цикл приёма — это и есть признак жизни long polling:
                // обновления приходят только когда пишут пользователи, тишина сбоем не является.
                _health.ReportRunning(HealthKey, HealthName);
                if (!announced)
                {
                    _eventLog.Success(HealthName, "Long polling запущен, слушаю обновления");
                    announced = true;
                }

                await _botClient.ReceiveAsync(
                    updateHandler: HandleUpdateAsync,
                    errorHandler: HandleErrorAsync,
                    receiverOptions: receiverOptions,
                    cancellationToken: stoppingToken
                );
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Telegram bot polling error. Retrying in 5 seconds...");
                _health.ReportFailure(HealthKey, HealthName, ex.Message);
                _eventLog.Error(HealthName, $"Поллинг прерван: {ex.Message}. Повтор через 5 с");
                announced = false;
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    private Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        _logger.LogInformation(">>> [Telegram] Received Update ID {UpdateId}, Type {Type}", update.Id, update.Type);
        _ = Task.Run(async () =>
        {
            try
            {
                _metrics.RecordTelegramMessage();
                using var scope = _services.CreateScope();
                var commandService = scope.ServiceProvider.GetRequiredService<BotCommandService>();
                await commandService.HandleUpdateAsync(update);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing Telegram update {UpdateId}", update.Id);
                _eventLog.Error(HealthName, $"Ошибка обработки обновления {update.Id}: {ex.Message}");
            }
        }, cancellationToken);

        return Task.CompletedTask;
    }

    private Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        _logger.LogError(exception, "Telegram API error during polling");
        // Ошибка отдельного запроса к API не означает, что цикл приёма умер.
        _health.ReportFailure(HealthKey, HealthName, exception.Message, fatal: false);
        _eventLog.Warning(HealthName, $"Ошибка Telegram API: {exception.Message}");
        return Task.CompletedTask;
    }
}
