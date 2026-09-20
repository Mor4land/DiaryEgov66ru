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
using MAX.Bot.Interfaces;
using MAX.Bot.Interfaces.Models;
using MAX.Bot.Interfaces.Models.Request;
using MAX.Bot.Interfaces.Models.Request.Message;

namespace MirageDiary.Server.Services;

public class SmartAlarmWorker : BackgroundService
{
    private readonly ILogger<SmartAlarmWorker> _logger;
    private readonly IServiceProvider _services;
    
    // Words to trigger equipment notifications
    private readonly string[] _triggerWords = {
        "циркуль", "линейка", "краски", "альбом", "форма", "транспортир",
        "контурные карты", "атлас", "гуашь", "кисти", "пластилин",
        "калькулятор", "чешки", "кроссовки", "лыжи", "сменка", "сменная обувь",
        "клей", "ножницы", "доклад", "реферат", "презентация"
    };

    // In-memory deduplication cache: protects against repeating alarms if SQLite is temporarily locked
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _sentAlarmsToday = new();

    public SmartAlarmWorker(ILogger<SmartAlarmWorker> logger, IServiceProvider services)
    {
        _logger = logger;
        _services = services;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SmartAlarmWorker is starting.");
        bool maintenanceAnnounced = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using (var scope = _services.CreateScope())
                {
                    var config = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
                    if (DiaryMaintenanceSettings.IsDiaryInMaintenance(config))
                    {
                        if (!maintenanceAnnounced)
                        {
                            _logger.LogInformation("[SmartAlarmWorker] ⏸️ Напоминания временно приостановлены в связи с техническими работами дневника (до 15.09).");
                            maintenanceAnnounced = true;
                        }
                        await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
                        continue;
                    }
                    maintenanceAnnounced = false;
                }

                await ProcessAlarmsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred executing SmartAlarmWorker.");
            }

            // Check every 30 seconds to catch exact minute
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private async Task ProcessAlarmsAsync(CancellationToken stoppingToken)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var diaryService = scope.ServiceProvider.GetRequiredService<DiaryInteractionService>();
        var tgBotClient = scope.ServiceProvider.GetService<ITelegramBotClient>();
        var maxBotClient = scope.ServiceProvider.GetService<IMaxBotClient>();

        // Query only users who actually have active tokens, master alarm enabled, and at least one notification enabled
        var students = await db.Users
            .Where(u => !string.IsNullOrEmpty(u.AccessToken) && u.IsAlarmEnabled &&
                       (u.IsScheduleReminderEnabled || u.IsHomeworkReminderEnabled || u.IsEquipmentReminderEnabled))
            .ToListAsync(stoppingToken);

        foreach (var student in students)
        {
            if (stoppingToken.IsCancellationRequested) break;

            var userNow = DateTime.UtcNow.AddHours(student.UtcOffsetHours > 0 ? student.UtcOffsetHours : 5);
            var userTimeOfDay = userNow.TimeOfDay;
            string todayStr = userNow.ToString("yyyy-MM-dd");
            bool dbChanged = false;

            // Helper to check if current user time falls within the activation window [TargetTime, TargetTime + 30 min]
            bool IsInTimeWindow(string reminderTimeStr)
            {
                if (TimeSpan.TryParse(reminderTimeStr, out var reminderTime))
                {
                    return userTimeOfDay >= reminderTime && userTimeOfDay < reminderTime.Add(TimeSpan.FromMinutes(30));
                }
                return false;
            }

            // Helper to check if reminder was already handled today (either in DB or in-memory)
            bool WasHandledToday(string type, string lastDateFromDb)
            {
                if (lastDateFromDb == todayStr) return true;
                return _sentAlarmsToday.TryGetValue($"{student.Id}_{type}", out var d) && d == todayStr;
            }

            void MarkHandledToday(string type)
            {
                _sentAlarmsToday[$"{student.Id}_{type}"] = todayStr;
            }

            // ─── 1. SCHEDULE REMINDER ──────────────────────────────────────────
            if (student.IsScheduleReminderEnabled &&
                !WasHandledToday("sched", student.LastScheduleReminderDate) &&
                IsInTimeWindow(student.ScheduleReminderTime))
            {
                try
                {
                    var schedText = await diaryService.GetScheduleStringAsync(student.Id, todayStr);
                    if (HasScheduleContent(schedText))
                    {
                        string msg = $"🔔 *Умный будильник: расписание уроков на сегодня!*\n\n{schedText}";
                        await SendMessageToUserAsync(student.Id, msg, tgBotClient, maxBotClient, stoppingToken);
                        student.LastScheduleReminderDate = todayStr;
                        MarkHandledToday("sched");
                        dbChanged = true;
                    }
                    else if (IsTerminalEmptySchedule(schedText))
                    {
                        // Уроков на сегодня нет (выходной/каникулы): фиксируем проверку, чтобы не долбить Госуслуги каждую минуту
                        student.LastScheduleReminderDate = todayStr;
                        MarkHandledToday("sched");
                        dbChanged = true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to send schedule reminder to {student.Id}");
                }
            }

            // ─── 2. HOMEWORK REMINDER ──────────────────────────────────────────
            if (student.IsHomeworkReminderEnabled &&
                !WasHandledToday("hw", student.LastHomeworkReminderDate) &&
                IsInTimeWindow(student.HomeworkReminderTime))
            {
                try
                {
                    var hwText = await diaryService.GetHomeworkStringAsync(student.Id, "завтра");
                    if (HasHomeworkContent(hwText))
                    {
                        string msg = $"📚 *Умный будильник: домашнее задание на завтра!*\n\n{hwText}";
                        await SendMessageToUserAsync(student.Id, msg, tgBotClient, maxBotClient, stoppingToken);
                        student.LastHomeworkReminderDate = todayStr;
                        MarkHandledToday("hw");
                        dbChanged = true;
                    }
                    else if (IsTerminalEmptyHomework(hwText))
                    {
                        // Домашнего задания на завтра нет: фиксируем проверку без отправки пустого уведомления
                        student.LastHomeworkReminderDate = todayStr;
                        MarkHandledToday("hw");
                        dbChanged = true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to send homework reminder to {student.Id}");
                }
            }

            // ─── 3. EQUIPMENT / BACKPACK REMINDER ──────────────────────────────
            if (student.IsEquipmentReminderEnabled &&
                !WasHandledToday("equip", student.LastEquipmentReminderDate) &&
                IsInTimeWindow(student.EquipmentReminderTime))
            {
                try
                {
                    var hwString = await diaryService.GetHomeworkStringAsync(student.Id, "завтра");
                    var schedString = await diaryService.GetScheduleStringAsync(student.Id, "завтра");
                    var combined = (hwString + " " + schedString).ToLowerInvariant();

                    var foundTriggers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var trigger in _triggerWords)
                    {
                        if (combined.Contains(trigger))
                        {
                            foundTriggers.Add(trigger);
                        }
                    }

                    // Smart subject checks
                    if (combined.Contains("физкультура") || combined.Contains("физра"))
                        foundTriggers.Add("спортивная форма и сменная обувь");
                    if (combined.Contains("изо") || combined.Contains("рисование"))
                        foundTriggers.Add("альбом, краски и кисти");
                    if (combined.Contains("геометрия"))
                        foundTriggers.Add("линейка, циркуль и карандаш");
                    if (combined.Contains("география"))
                        foundTriggers.Add("атлас и контурные карты");
                    if (combined.Contains("технология") || combined.Contains("труд"))
                        foundTriggers.Add("принадлежности для урока труда");

                    // Отправляем уведомление ТОЛЬКО если найдены специфические вещи на завтра
                    if (foundTriggers.Any())
                    {
                        string items = string.Join("\n• ", foundTriggers.Select(t => char.ToUpper(t[0]) + t[1..]));
                        string msg = $"🎒 *Умный будильник: собираем рюкзак на завтра!*\n\nНе забудьте взять с собой:\n• {items}\n\n✨ _Положите их в рюкзак прямо сейчас, чтобы не забыть утром!_";
                        await SendMessageToUserAsync(student.Id, msg, tgBotClient, maxBotClient, stoppingToken);
                    }

                    bool isNetworkError = combined.Contains("таймаут") || combined.Contains("ошибка при");
                    if (!isNetworkError)
                    {
                        student.LastEquipmentReminderDate = todayStr;
                        MarkHandledToday("equip");
                        dbChanged = true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to send equipment reminder to {student.Id}");
                }
            }

            if (dbChanged)
            {
                try
                {
                    await db.SaveChangesAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[SmartAlarmWorker] Ошибка сохранения дат напоминаний в SQLite (in-memory дедупликация защищает от повторной отправки).");
                }
            }
        }
    }

    private async Task SendMessageToUserAsync(string userId, string msg, ITelegramBotClient? tgBotClient, IMaxBotClient? maxBotClient, CancellationToken ct)
    {
        if (userId.StartsWith("max_"))
        {
            if (long.TryParse(userId.Replace("max_", ""), out long maxChatId) && maxBotClient != null)
            {
                await maxBotClient.SendMessageAsync(new SendMessageRequest
                {
                    UserId = maxChatId,
                    Text = msg,
                    Format = MessageFormat.Markdown
                });
            }
        }
        else if (long.TryParse(userId, out long chatId) && tgBotClient != null)
        {
            try
            {
                await tgBotClient.SendMessage(chatId, msg, parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown, cancellationToken: ct);
            }
            catch
            {
                await tgBotClient.SendMessage(chatId, msg, cancellationToken: ct);
            }
        }
    }

    private static bool HasScheduleContent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.Contains("отсутствует", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("не загружено", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("недоступен", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("не авторизованы", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("таймаут", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Ошибка", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("🔒"))
        {
            return false;
        }
        return text.Contains("1. *") || text.Contains("1.*");
    }

    private static bool IsTerminalEmptySchedule(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return text.Contains("отсутствует", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("не загружено", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasHomeworkContent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.Contains("нет.", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("отсутствует", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("недоступен", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("не авторизованы", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("таймаут", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Ошибка", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("🔒"))
        {
            return false;
        }
        return text.Contains("• *") || text.Contains("•*");
    }

    private static bool IsTerminalEmptyHomework(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return text.Contains("нет.", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("отсутствует", StringComparison.OrdinalIgnoreCase);
    }
}
