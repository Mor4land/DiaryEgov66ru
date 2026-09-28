using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MirageDiary.Core;
using MirageDiary.Core.Models;
using MirageDiary.Server.Data;
using MirageDiary.Server.Models;
using MirageDiary.Server.Security;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using MAX.Bot.Interfaces;
using MAX.Bot.Interfaces.Models;
using MAX.Bot.Interfaces.Models.Request;
using MAX.Bot.Interfaces.Models.Request.Message;
using MAX.Bot.Interfaces.Models.Request.Message.Attachment;
using MAX.Bot.Interfaces.Models.Request.Message.Attachment.Payloads;

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
    private DateTime _lastBellCheck = DateTime.MinValue;
    private DateTime _lastPeriodicCheck = DateTime.MinValue;

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
        _logger.LogInformation("[GradeWatcher] 🚀 Live Grade Watcher успешно запущен (интервалы: звонки 2м, день 10м).");
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
                bool isBellWindow = _bellSchedule.IsLessonEndingOrBreakWindow(localTime);

                bool shouldCheck = false;
                if (isBellWindow)
                {
                    // В окнах звонков/перемен проверяем каждые 2 минуты
                    if ((localTime - _lastBellCheck).TotalMinutes >= 2)
                    {
                        shouldCheck = true;
                        _lastBellCheck = localTime;
                        _lastPeriodicCheck = localTime;
                        if ((localTime - _lastWindowLogTime).TotalMinutes >= 5)
                        {
                            _logger.LogInformation($"[GradeWatcher] 🔔 Окно звонка активно ({localTime:HH:mm}). Запуск проверки оценок...");
                            _lastWindowLogTime = localTime;
                        }
                    }
                }
                else if (localTime.Hour >= 8 && localTime.Hour < 22)
                {
                    // В учебное и вечернее время проверки ДЗ учителями (08:00–22:00) проверяем каждые 10 минут
                    if ((localTime - _lastPeriodicCheck).TotalMinutes >= 10)
                    {
                        shouldCheck = true;
                        _lastPeriodicCheck = localTime;
                        _logger.LogInformation($"[GradeWatcher] ⏰ Периодическая проверка оценок ({localTime:HH:mm})...");
                    }
                }
                else
                {
                    // Ночные часы (22:00–08:00) — раз в 60 минут
                    if ((localTime - _lastPeriodicCheck).TotalMinutes >= 60)
                    {
                        shouldCheck = true;
                        _lastPeriodicCheck = localTime;
                    }
                }

                if (shouldCheck)
                {
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
        List<string> userIds;
        using (var initScope = _services.CreateScope())
        {
            var initDb = initScope.ServiceProvider.GetRequiredService<AppDbContext>();
            userIds = await initDb.Users
                .AsNoTracking()
                .Where(u => !string.IsNullOrEmpty(u.AccessToken) && u.IsGradeWatcherEnabled)
                .Select(u => u.Id)
                .ToListAsync(stoppingToken);
        }

        if (userIds.Count == 0) return;

        foreach (var userId in userIds)
        {
            if (stoppingToken.IsCancellationRequested) break;

            using var userScope = _services.CreateScope();
            var db = userScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tokenStore = userScope.ServiceProvider.GetRequiredService<ITokenStore>();
            var httpFactory = userScope.ServiceProvider.GetRequiredService<System.Net.Http.IHttpClientFactory>();
            var botClient = userScope.ServiceProvider.GetService<ITelegramBotClient>();
            var maxBotClient = userScope.ServiceProvider.GetService<IMaxBotClient>();

            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, stoppingToken);
            if (user == null || string.IsNullOrEmpty(user.AccessToken) || !user.IsGradeWatcherEnabled)
                continue;

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

                // Синхронизируем с кэшем БД (marks_db:{studentId}), чтобы при открытии бота у пользователя сразу были свежие оценки
                string studentId = !string.IsNullOrEmpty(tokenData.StudentId) ? tokenData.StudentId : user.Id;
                string dbCacheKey = $"marks_db:{studentId}";
                var existingDb = await db.Cache.FirstOrDefaultAsync(c => c.CacheKey == dbCacheKey, stoppingToken);

                List<GosuslugiMarkItem>? previousMarks = null;
                if (existingDb != null && !string.IsNullOrEmpty(existingDb.JsonData))
                {
                    try
                    {
                        previousMarks = JsonSerializer.Deserialize<List<GosuslugiMarkItem>>(existingDb.JsonData, DiaryClient.DatamartJsonOptions);
                    }
                    catch (Exception pex)
                    {
                        _logger.LogDebug($"[GradeWatcher] Ошибка десериализации предыдущих оценок: {pex.Message}");
                    }
                }

                string marksJson = JsonSerializer.Serialize(marks, DiaryClient.DatamartJsonOptions);
                if (existingDb != null)
                {
                    existingDb.JsonData = marksJson;
                    existingDb.UpdatedAt = DateTime.UtcNow;
                }
                else
                {
                    db.Cache.Add(new CachedData { CacheKey = dbCacheKey, JsonData = marksJson, UpdatedAt = DateTime.UtcNow });
                }

                var memCache = userScope.ServiceProvider.GetService<IMemoryCache>();
                if (memCache != null)
                {
                    for (int q = 0; q <= 4; q++)
                    {
                        memCache.Remove($"user_marks:{user.Id}:q{q}");
                        memCache.Remove($"user_marks_str:{user.Id}:q{q}");
                        memCache.Remove($"user_marks:{user.Id}_{studentId}:q{q}");
                        memCache.Remove($"user_marks_str:{user.Id}_{studentId}:q{q}");
                    }
                }

                string expectedHashPrefix = $"{studentId}:";
                string currentRawHash = ComputeMarksHash(marks);
                string currentHash = $"{studentId}:{currentRawHash}";

                // Если это первый запуск для пользователя или сменился активный ребёнок — сохраняем базовый хэш без спама
                if (string.IsNullOrEmpty(user.LastMarksHash) || !user.LastMarksHash.StartsWith(expectedHashPrefix) || previousMarks == null || previousMarks.Count == 0)
                {
                    user.LastMarksHash = currentHash;
                    await db.SaveChangesAsync(stoppingToken);
                    continue;
                }

                // Если хэш изменился — значит появились новые оценки или изменения!
                if (user.LastMarksHash != currentHash)
                {
                    _logger.LogInformation($"[GradeWatcher] 🔥 Обнаружено изменение оценок для пользователя {user.Id} (student={studentId})!");

                    // Сравниваем оценки с предыдущим срезом по ключу
                    var prevKeys = new HashSet<string>(previousMarks.Select(GetMarkKey));
                    var freshMarks = marks
                        .Where(m => !prevKeys.Contains(GetMarkKey(m)))
                        .OrderByDescending(m => m.Date)
                        .ToList();

                    // Если нет совершенно новых оценок, проверяем, не исправил ли учитель оценку по существующему ID
                    if (freshMarks.Count == 0)
                    {
                        var prevById = previousMarks
                            .Where(m => !string.IsNullOrEmpty(m.Id))
                            .GroupBy(m => m.Id!)
                            .ToDictionary(g => g.Key, g => g.First());

                        freshMarks = marks
                            .Where(m => !string.IsNullOrEmpty(m.Id) && prevById.TryGetValue(m.Id!, out var prev) && prev.Value != m.Value)
                            .OrderByDescending(m => m.Date)
                            .ToList();
                    }

                    // Если реальных новых или исправленных оценок нет (например, изменился только формат хэша при деплое),
                    // ложное уведомление НЕ отправляется, а хэш тихо актуализируется в БД.
                    if (freshMarks.Count > 0)
                    {
                        var marksToSend = freshMarks.Take(5).ToList();
                        var sb = new StringBuilder();
                        sb.AppendLine(marksToSend.Count == 1
                            ? "🔥 *Новая оценка в электронном журнале!*"
                            : "🔥 *Новые оценки в электронном журнале!*");

                        if (user.Role == "Parent" || !string.IsNullOrEmpty(user.SelectedStudentId))
                        {
                            if (memCache != null && memCache.TryGetValue($"user_children:{user.Id}", out List<GosuslugiStudentInfo>? kids) && kids != null)
                            {
                                var k = kids.FirstOrDefault(x => x.StudentId == studentId);
                                if (k != null)
                                {
                                    string kname = $"{k.StudentLastName} {k.StudentFirstName}".Trim();
                                    if (!string.IsNullOrEmpty(kname))
                                    {
                                        sb.AppendLine($"👶 *Ученик:* `{TelegramMarkdownEscaper.Escape(kname)}`");
                                    }
                                }
                            }
                        }
                        sb.AppendLine();

                        // Группируем свежие оценки по предметам для структурированного вывода и расчета динамики
                        var subjectGroups = marksToSend.GroupBy(m => !string.IsNullOrWhiteSpace(m.SubjectName) ? m.SubjectName.Trim() : "Урок");
                        foreach (var group in subjectGroups)
                        {
                            string subjectName = group.Key;
                            string safeSubject = TelegramMarkdownEscaper.Escape(subjectName);
                            sb.AppendLine($"📚 *{safeSubject}*");

                            foreach (var m in group)
                            {
                                string markDisplay = !string.IsNullOrEmpty(m.Value) ? m.Value : "—";
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
                                string dateStr = !string.IsNullOrEmpty(m.Date) ? $" • {TelegramMarkdownEscaper.Escape(m.Date)}" : "";

                                sb.AppendLine($"⭐️ Оценка: *{TelegramMarkdownEscaper.Escape(markDisplay)}*{weight}{reason}{dateStr}");
                            }

                            // Динамика среднего балла и прогноз четвертной оценки по данному предмету
                            var subjDynamics = GradeCalculator.ComputeSubjectDynamics(previousMarks, marks, subjectName);
                            string gpaLine = subjDynamics.FormatSubjectGpaLine();
                            if (!string.IsNullOrEmpty(gpaLine))
                                sb.AppendLine(gpaLine);

                            string forecastLine = subjDynamics.FormatForecastLine();
                            if (!string.IsNullOrEmpty(forecastLine))
                                sb.AppendLine(forecastLine);

                            sb.AppendLine();
                        }

                        if (freshMarks.Count > marksToSend.Count)
                        {
                            int remaining = freshMarks.Count - marksToSend.Count;
                            string markWord = remaining % 10 == 1 && remaining % 100 != 11 ? "оценка" :
                                              (remaining % 10 is >= 2 and <= 4 && (remaining % 100 < 10 || remaining % 100 >= 20) ? "оценки" : "оценок");
                            sb.AppendLine($"_...и ещё {remaining} {markWord}_");
                            sb.AppendLine();
                        }

                        // Динамика общего среднего балла по всем предметам
                        var overallDynamics = GradeCalculator.ComputeOverallDynamics(previousMarks, marks);
                        string overallLine = overallDynamics.FormatOverallGpaLine();
                        if (!string.IsNullOrEmpty(overallLine))
                        {
                            sb.AppendLine(overallLine);
                            sb.AppendLine();
                        }

                        sb.AppendLine("💡 _Подробная статистика и цели в меню ниже:_");

                        var tgMarkup = new InlineKeyboardMarkup(new[]
                        {
                            new[]
                            {
                                InlineKeyboardButton.WithCallbackData("📊 Все оценки", "cmd_marks"),
                                InlineKeyboardButton.WithCallbackData("🎯 Калькулятор целей", "cmd_calc")
                            },
                            new[]
                            {
                                InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "cmd_back_main")
                            }
                        });

                        var maxAttachments = new List<Attachment>
                        {
                            new InlineKeyboardAttachment
                            {
                                Payload = new InlineKeyboardPayload
                                {
                                    Buttons = new List<List<Button>>
                                    {
                                        new List<Button>
                                        {
                                            new CallbackButton { Text = "📊 Все оценки", Payload = "cmd_marks" },
                                            new CallbackButton { Text = "🎯 Калькулятор целей", Payload = "cmd_calc" }
                                        },
                                        new List<Button>
                                        {
                                            new CallbackButton { Text = "🏠 Главное меню", Payload = "cmd_back_main" }
                                        }
                                    }
                                }
                            }
                        };

                        // Проверка предпочтения канала доставки для связанных аккаунтов
                        bool shouldSendPush = true;
                        if (!string.IsNullOrEmpty(user.NotificationChannel))
                        {
                            bool isMax = user.Id.StartsWith("max_");
                            if (user.NotificationChannel.Equals("telegram", StringComparison.OrdinalIgnoreCase) && isMax)
                            {
                                shouldSendPush = false;
                                _logger.LogInformation($"[GradeWatcher] Пропуск отправки пуша в MAX для {user.Id}: в настройках выбран Telegram.");
                            }
                            else if (user.NotificationChannel.Equals("max", StringComparison.OrdinalIgnoreCase) && !isMax)
                            {
                                shouldSendPush = false;
                                _logger.LogInformation($"[GradeWatcher] Пропуск отправки пуша в TG для {user.Id}: в настройках выбран MAX.");
                            }
                        }

                        if (shouldSendPush)
                        {
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
                                        Format = MessageFormat.Markdown,
                                        Attachments = maxAttachments
                                    });
                                    _logger.LogInformation($"[GradeWatcher] ✅ Пуш об оценке успешно доставлен MAX пользователю {user.Id}.");
                                }
                            }
                            else if (botClient != null)
                            {
                                try
                                {
                                    await botClient.SendMessage(
                                        chatId: user.Id,
                                        text: sb.ToString(),
                                        parseMode: ParseMode.Markdown,
                                        replyMarkup: tgMarkup,
                                        cancellationToken: stoppingToken);
                                    _logger.LogInformation($"[GradeWatcher] ✅ Пуш об оценке успешно доставлен TG пользователю {user.Id}.");
                                }
                                catch (Exception mex)
                                {
                                    _logger.LogWarning($"[GradeWatcher] Ошибка отправки Markdown пуша для {user.Id}: {mex.Message}. Попытка резервной отправки без разметки...");
                                    string plain = sb.ToString().Replace("*", "").Replace("_", "").Replace("`", "");
                                    await botClient.SendMessage(
                                        chatId: user.Id,
                                        text: plain,
                                        replyMarkup: tgMarkup,
                                        cancellationToken: stoppingToken);
                                    _logger.LogInformation($"[GradeWatcher] ✅ Резервный пуш без разметки доставлен TG пользователю {user.Id}.");
                                }
                            }
                        }
                        catch (Exception tex)
                        {
                            _logger.LogWarning($"[GradeWatcher] Не удалось отправить пуш пользователю {user.Id}: {tex.Message}");
                        }
                        }
                    }
                    else
                    {
                        _logger.LogInformation($"[GradeWatcher] Хэш изменился для {user.Id}, но новых оценок в сравнении с кэшем нет. Ложное срабатывание предотвращено.");
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

    public static string ComputeMarksHash(IEnumerable<GosuslugiMarkItem> marks)
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

    public static string GetMarkKey(GosuslugiMarkItem m)
    {
        if (!string.IsNullOrEmpty(m.Id))
            return m.Id;
        return $"{m.SubjectName}_{m.Date}_{m.Value}_{m.Weight}_{m.WorkKind}";
    }
}
