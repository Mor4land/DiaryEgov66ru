using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MirageDiary.Core;
using MirageDiary.Core.Models;
using MirageDiary.Server.Data;
using MirageDiary.Server.Security;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using MAX.Bot.Interfaces;
using MAX.Bot.Interfaces.Models;
using MAX.Bot.Interfaces.Models.Request;
using MAX.Bot.Interfaces.Models.Request.Message;

namespace MirageDiary.Server.Services;

/// <summary>
/// Фоновый мониторинг появления новых оценок по расписанию звонков (Smart Lesson-End Trigger).
/// Срабатывает только в конце уроков и на переменах МАОУ СОШ № 113.
/// </summary>
public class GradeWatcherWorker : BackgroundService
{
    private readonly ILogger<GradeWatcherWorker> _logger;
    private readonly IServiceProvider _services;
    private readonly SchoolBellScheduleService _bellSchedule;
    private DateTime _lastWindowLogTime = DateTime.MinValue;

    public GradeWatcherWorker(
        ILogger<GradeWatcherWorker> logger,
        IServiceProvider services,
        SchoolBellScheduleService bellSchedule)
    {
        _logger = logger;
        _services = services;
        _bellSchedule = bellSchedule;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[GradeWatcher] 🚀 Live Grade Watcher по звонкам успешно запущен.");
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
                            _logger.LogInformation("[GradeWatcher] ⏸️ Мониторинг оценок приостановлен на время тех. работ дневника (до 15.09).");
                            maintenanceAnnounced = true;
                        }
                        await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
                        continue;
                    }
                    maintenanceAnnounced = false;
                }

                var localTime = DateTime.UtcNow.AddHours(5); // Екатеринбург UTC+5

                if (_bellSchedule.IsLessonEndingOrBreakWindow(localTime))
                {
                    if ((localTime - _lastWindowLogTime).TotalMinutes >= 5)
                    {
                        _logger.LogInformation($"[GradeWatcher] 🔔 Окно звонка активно ({localTime:HH:mm}). Запуск проверки оценок...");
                        _lastWindowLogTime = localTime;
                    }

                    await CheckMarksForActiveUsersAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[GradeWatcher] Ошибка в цикле мониторинга оценок.");
            }

            // Проверяем каждую минуту
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
        }
    }

    private async Task CheckMarksForActiveUsersAsync(CancellationToken stoppingToken)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenStore = scope.ServiceProvider.GetRequiredService<ITokenStore>();
        var httpFactory = scope.ServiceProvider.GetRequiredService<System.Net.Http.IHttpClientFactory>();
        var botClient = scope.ServiceProvider.GetService<ITelegramBotClient>();
        var maxBotClient = scope.ServiceProvider.GetService<IMaxBotClient>();

        // Выбираем пользователей с активным токеном
        var users = await db.Users
            .Where(u => !string.IsNullOrEmpty(u.AccessToken))
            .ToListAsync(stoppingToken);

        if (users.Count == 0) return;

        foreach (var user in users)
        {
            if (stoppingToken.IsCancellationRequested) break;

            try
            {
                var tokenData = await tokenStore.GetTokensAsync(user.Id);
                if (tokenData == null || string.IsNullOrEmpty(tokenData.AccessToken))
                    continue;

                var client = httpFactory.CreateClient("diary-proxy");
                var diaryClient = new DiaryClient(client, tokenStore, user.Id);

                // Получаем актуальные оценки за текущую четверть
                var marks = await diaryClient.GetMarksAsync();
                if (marks == null || marks.Count == 0)
                    continue;

                string currentHash = ComputeMarksHash(marks);

                // Если это первый запуск для пользователя — сохраняем базовый хэш
                if (string.IsNullOrEmpty(user.LastMarksHash))
                {
                    user.LastMarksHash = currentHash;
                    await db.SaveChangesAsync(stoppingToken);
                    continue;
                }

                // Если хэш изменился — значит появились новые оценки или изменения!
                if (user.LastMarksHash != currentHash)
                {
                    _logger.LogInformation($"[GradeWatcher] 🔥 Обнаружено изменение оценок для пользователя {user.Id}!");

                    // Ищем свежие оценки (выставленные за сегодня или последние добавленные)
                    var freshMarks = marks
                        .OrderByDescending(m => m.Date)
                        .Take(3)
                        .ToList();

                    var sb = new StringBuilder();
                    sb.AppendLine("🔥 *Новая оценка в электронном журнале!*");
                    sb.AppendLine();

                    foreach (var m in freshMarks)
                    {
                        string markDisplay = !string.IsNullOrEmpty(m.Value) ? m.Value : "—";
                        string lessonName = !string.IsNullOrEmpty(m.SubjectName) ? TelegramMarkdownEscaper.Escape(m.SubjectName) : "Урок";
                        
                        string workKindStr = !string.IsNullOrEmpty(m.WorkKind) ? TelegramMarkdownEscaper.Escape(m.WorkKind) : "";
                        string commentStr = !string.IsNullOrEmpty(m.Comment) ? TelegramMarkdownEscaper.Escape(m.Comment) : "";
                        string reason = "";
                        if (!string.IsNullOrEmpty(workKindStr) && !string.IsNullOrEmpty(commentStr))
                            reason = $" ({workKindStr}, {commentStr})";
                        else if (!string.IsNullOrEmpty(workKindStr))
                            reason = $" ({workKindStr})";
                        else if (!string.IsNullOrEmpty(commentStr))
                            reason = $" ({commentStr})";

                        string weight = (m.Weight.HasValue && m.Weight.Value > 1) ? $" [вес: {m.Weight.Value}]" : "";

                        sb.AppendLine($"📚 *{lessonName}*");
                        sb.AppendLine($"⭐️ Оценка: *{TelegramMarkdownEscaper.Escape(markDisplay)}*{reason}{weight}");
                        if (!string.IsNullOrEmpty(m.Date))
                            sb.AppendLine($"📅 Дата: {TelegramMarkdownEscaper.Escape(m.Date)}");
                        sb.AppendLine();
                    }

                    sb.AppendLine("💡 _Проверь подробности и средний балл командой /marks_");

                    try
                    {
                        if (user.Id.StartsWith("max_"))
                        {
                            if (long.TryParse(user.Id.Replace("max_", ""), out long maxUserId) && maxBotClient != null)
                            {
                                await maxBotClient.SendMessageAsync(new SendMessageRequest
                                {
                                    UserId = maxUserId,
                                    Text = sb.ToString(),
                                    Format = MessageFormat.Markdown
                                });
                                _logger.LogInformation($"[GradeWatcher] ✅ Пуш об оценке успешно доставлен MAX пользователю {user.Id}.");
                            }
                        }
                        else if (botClient != null)
                        {
                            await botClient.SendMessage(
                                chatId: user.Id,
                                text: sb.ToString(),
                                parseMode: ParseMode.Markdown,
                                cancellationToken: stoppingToken);
                            _logger.LogInformation($"[GradeWatcher] ✅ Пуш об оценке успешно доставлен TG пользователю {user.Id}.");
                        }
                    }
                    catch (Exception tex)
                    {
                        _logger.LogWarning($"[GradeWatcher] Не удалось отправить пуш пользователю {user.Id}: {tex.Message}");
                    }

                    user.LastMarksHash = currentHash;
                    await db.SaveChangesAsync(stoppingToken);
                }
            }
            catch (DiarySessionExpiredException)
            {
                // Сессия устарела, воркер рефреша подхватит её отдельно
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"[GradeWatcher] Ошибка проверки оценок для {user.Id}: {ex.Message}");
            }

            // Плавная пауза между пользователями для защиты от WAF
            await Task.Delay(2500, stoppingToken);
        }
    }

    private static string ComputeMarksHash(IEnumerable<GosuslugiMarkItem> marks)
    {
        var ordered = marks
            .OrderBy(m => m.SubjectName)
            .ThenBy(m => m.Date)
            .ThenBy(m => m.Value)
            .ThenBy(m => m.WorkKind);

        var sb = new StringBuilder();
        foreach (var m in ordered)
        {
            sb.Append($"{m.SubjectName}|{m.Date}|{m.Value}|{m.Weight}|{m.WorkKind};");
        }

        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes);
    }
}
