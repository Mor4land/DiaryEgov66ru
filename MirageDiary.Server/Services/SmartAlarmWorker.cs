using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using MirageDiary.Server.Data;
using System.Collections.Generic;

namespace MirageDiary.Server.Services;

public class SmartAlarmWorker : BackgroundService
{
    private readonly ILogger<SmartAlarmWorker> _logger;
    private readonly IServiceProvider _services;
    
    // Words to trigger notifications
    private readonly string[] _triggerWords = { "циркуль", "линейка", "краски", "альбом", "форма", "транспортир", "контурные карты" };

    public SmartAlarmWorker(ILogger<SmartAlarmWorker> logger, IServiceProvider services)
    {
        _logger = logger;
        _services = services;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SmartAlarmWorker is starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessAlarmsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred executing SmartAlarmWorker.");
            }

            // Run once a day (or once an hour). For testing, let's set to 1 hour.
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }

    private async Task ProcessAlarmsAsync(CancellationToken stoppingToken)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var diaryService = scope.ServiceProvider.GetRequiredService<DiaryInteractionService>();
        var botClient = scope.ServiceProvider.GetRequiredService<ITelegramBotClient>();

        var students = await db.Users.Where(u => !string.IsNullOrEmpty(u.AccessToken)).ToListAsync(stoppingToken);

        foreach (var student in students)
        {
            if (stoppingToken.IsCancellationRequested) break;

            if (!student.IsAlarmEnabled) continue;

            try
            {
                // Note: GetHomeworkStringAsync gets today's/tomorrow's homework as a single string.
                // In a real scenario, we would parse JSON. We will just check the string.
                var hwString = await diaryService.GetHomeworkStringAsync(student.Id);

                var lowerHw = hwString.ToLowerInvariant();
                var foundTriggers = new List<string>();

                foreach (var trigger in _triggerWords)
                {
                    if (lowerHw.Contains(trigger))
                    {
                        foundTriggers.Add(trigger);
                    }
                }

                if (foundTriggers.Any())
                {
                    string items = string.Join(", ", foundTriggers);
                    string msg = $"🔔 **Напоминание (Умный будильник):**\n\nЗавтра вам понадобятся: **{items}**.\nНе забудьте положить их в рюкзак!";
                    
                    if (long.TryParse(student.Id, out long chatId))
                    {
                        await botClient.SendMessage(chatId, msg, parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown, cancellationToken: stoppingToken);
                        _logger.LogInformation($"Sent alarm to {chatId} for items: {items}");
                    }
                }
                
                // TODO: In the future, check for new grades here and notify!
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to process alarm for user {student.Id}");
            }
        }
    }
}
