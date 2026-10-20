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
using Microsoft.Extensions.Configuration;
using MirageDiary.Server.Security;
using Telegram.Bot;
using Telegram.Bot.Types;
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

            // Если уведомления пользователя перенаправлены на другую платформу, пропускаем опрос для этого аккаунта
            if (!string.IsNullOrEmpty(user.NotificationChannel))
            {
                bool isMax = user.Id.StartsWith("max_");
                if (user.NotificationChannel.Equals("telegram", StringComparison.OrdinalIgnoreCase) && isMax)
                {
                    _logger.LogInformation($"[GradeWatcher] Пропуск фонового опроса для {user.Id}: уведомления направляются в Telegram.");
                    continue;
                }
                else if (user.NotificationChannel.Equals("max", StringComparison.OrdinalIgnoreCase) && !isMax)
                {
                    _logger.LogInformation($"[GradeWatcher] Пропуск фонового опроса для {user.Id}: уведомления направляются в MAX.");
                    continue;
                }
            }

            try
            {
                var tokenData = await tokenStore.GetTokensAsync(user.Id);
                if (tokenData == null || string.IsNullOrEmpty(tokenData.AccessToken))
                    continue;

                var client = httpFactory.CreateClient("diary-proxy");
                var diaryClient = new DiaryClient(client, tokenStore, user.Id, isBackground: true);

                string preferredStudentId = !string.IsNullOrEmpty(user.SelectedStudentId)
                    ? user.SelectedStudentId
                    : (!string.IsNullOrEmpty(tokenData.StudentId) ? tokenData.StudentId : user.StudentId);

                // Синхронизируем с кэшем БД:
                // 1) Личный срез пользователя (marks_db:{user.Id}_{studentId}) для расчета диффов без интерференции между родителем и учеником
                // 2) Общий срез студента (marks_db:{studentId}) для мгновенной загрузки дневника при открытии бота
                string studentId = !string.IsNullOrEmpty(preferredStudentId) ? preferredStudentId : user.Id;
                string userDbCacheKey = $"marks_db:{user.Id}_{studentId}";
                string sharedDbCacheKey = $"marks_db:{studentId}";

                var userDbEntry = await db.Cache.FirstOrDefaultAsync(c => c.CacheKey == userDbCacheKey, stoppingToken);
                var sharedDbEntry = await db.Cache.FirstOrDefaultAsync(c => c.CacheKey == sharedDbCacheKey, stoppingToken);

                List<GosuslugiMarkItem>? previousMarks = null;
                if (userDbEntry != null && !string.IsNullOrEmpty(userDbEntry.JsonData))
                {
                    try
                    {
                        previousMarks = JsonSerializer.Deserialize<List<GosuslugiMarkItem>>(userDbEntry.JsonData, DiaryClient.DatamartJsonOptions);
                    }
                    catch (Exception pex)
                    {
                        _logger.LogDebug($"[GradeWatcher] Ошибка десериализации предыдущих оценок пользователя {user.Id}: {pex.Message}");
                    }
                }
                // Если персонального кэша еще нет (первый запуск после обновления) — пробуем общий кэш
                if ((previousMarks == null || previousMarks.Count == 0) && sharedDbEntry != null && !string.IsNullOrEmpty(sharedDbEntry.JsonData))
                {
                    try
                    {
                        previousMarks = JsonSerializer.Deserialize<List<GosuslugiMarkItem>>(sharedDbEntry.JsonData, DiaryClient.DatamartJsonOptions);
                    }
                    catch { }
                }

                bool isInitialSync = previousMarks == null || previousMarks.Count == 0;

                // Запрашиваем полный срез четверти (12 недель назад + 2 вперед = 14 интервалов в 1 пакете Datamart),
                // чтобы исключить пропуски оценок при перезапусках, продлении сессий или выставлении оценок задним числом.
                var fetchedMarks = await diaryClient.GetMarksAsync(
                    preferredStudentId,
                    pastWeeks: 12,
                    futureWeeks: 2);

                if (isInitialSync && (fetchedMarks == null || fetchedMarks.Count == 0))
                {
                    _logger.LogDebug($"[GradeWatcher] ℹ️ Оценки для user={user.Id} не получены (0 шт.). Пропуск цикла.");
                    await Task.Delay(1500, stoppingToken);
                    continue;
                }

                // Бесшовно объединяем свежие оценки с сохраненным срезом четверти
                var marksMap = (previousMarks ?? new List<GosuslugiMarkItem>())
                    .GroupBy(GetMarkKey)
                    .ToDictionary(g => g.Key, g => g.First());

                if (fetchedMarks != null && fetchedMarks.Count > 0)
                {
                    foreach (var fresh in fetchedMarks)
                    {
                        marksMap[GetMarkKey(fresh)] = fresh;
                    }
                }
                var marks = marksMap.Values.OrderByDescending(m => m.Date).ToList();

                string marksJson = JsonSerializer.Serialize(marks, DiaryClient.DatamartJsonOptions);

                // Сохраняем/обновляем персональный срез пользователя
                if (userDbEntry != null)
                {
                    userDbEntry.JsonData = marksJson;
                    userDbEntry.UpdatedAt = DateTime.UtcNow;
                }
                else
                {
                    db.Cache.Add(new CachedData { CacheKey = userDbCacheKey, JsonData = marksJson, UpdatedAt = DateTime.UtcNow });
                }

                // Актуализируем общий срез студента: объединяем с существующим общим кэшем, чтобы не затереть срез другого родителя/ученика
                var existingShared = new List<GosuslugiMarkItem>();
                if (sharedDbEntry != null && !string.IsNullOrEmpty(sharedDbEntry.JsonData))
                {
                    try
                    {
                        existingShared = JsonSerializer.Deserialize<List<GosuslugiMarkItem>>(sharedDbEntry.JsonData, DiaryClient.DatamartJsonOptions) ?? new();
                    }
                    catch { }
                }

                var sharedMap = existingShared
                    .GroupBy(GetMarkKey)
                    .ToDictionary(g => g.Key, g => g.First());

                foreach (var m in marks)
                {
                    sharedMap[GetMarkKey(m)] = m;
                }

                var mergedShared = sharedMap.Values.OrderByDescending(m => m.Date).ToList();
                string sharedMarksJson = JsonSerializer.Serialize(mergedShared, DiaryClient.DatamartJsonOptions);

                if (sharedDbEntry != null)
                {
                    sharedDbEntry.JsonData = sharedMarksJson;
                    sharedDbEntry.UpdatedAt = DateTime.UtcNow;
                }
                else
                {
                    db.Cache.Add(new CachedData { CacheKey = sharedDbCacheKey, JsonData = sharedMarksJson, UpdatedAt = DateTime.UtcNow });
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

                    bool isCorrection = false;
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
                        if (freshMarks.Count > 0)
                        {
                            isCorrection = true;
                        }
                    }

                    // Фильтрация давности и защита от флаппинга серверов Госуслуг
                    var validFreshMarks = new List<GosuslugiMarkItem>();
                    var today = DateTime.UtcNow.Date;

                    foreach (var m in freshMarks)
                    {
                        bool hasDate = DiaryInteractionService.TryParseMarkDate(m.Date, out var markDate);
                        if (hasDate)
                        {
                            // 1. Для исправлений: не шлём пуши, если дата оценки старше 7 дней (фоновый синк старых оценок)
                            if (isCorrection && (today - markDate).TotalDays > 7)
                            {
                                _logger.LogInformation($"[GradeWatcher] ℹ️ Пуш подавлен: исправление старой оценки {m.SubjectName} '{m.Value}' за {m.Date} (старше 7 дней, id={m.Id}) для user={user.Id}.");
                                continue;
                            }

                            // 2. Для новых оценок: не шлём пуши, если дата урока старше 14 дней (исторический срез)
                            if (!isCorrection && (today - markDate).TotalDays > 14)
                            {
                                _logger.LogInformation($"[GradeWatcher] ℹ️ Пуш подавлен: новая оценка {m.SubjectName} '{m.Value}' за старую дату {m.Date} (старше 14 дней, id={m.Id}) для user={user.Id}.");
                                continue;
                            }
                        }

                        // 3. Защита от флаппинга серверов Госуслуг (когда WAF/ноды чередуют 3 и 5)
                        if (memCache != null && !string.IsNullOrEmpty(m.Id))
                        {
                            string notifValKey = $"last_notif_val:{studentId}_{m.Id}";
                            if (memCache.TryGetValue(notifValKey, out string? lastSentVal) && string.Equals(lastSentVal, m.Value, StringComparison.OrdinalIgnoreCase))
                            {
                                _logger.LogInformation($"[GradeWatcher] ℹ️ Пуш подавлен: оценка {m.SubjectName} '{m.Value}' (id={m.Id}) уже отправлялась недавно.");
                                continue;
                            }

                            string flapCountKey = $"notif_flap_cnt:{studentId}_{m.Id}";
                            int flapCount = memCache.Get<int>(flapCountKey);
                            if (flapCount >= 2)
                            {
                                _logger.LogWarning($"[GradeWatcher] ⚠️ Пуш заблокирован: зафиксирован флаппинг серверов Госуслуг для {m.SubjectName} id={m.Id} (более 2 изменений за сессию).");
                                continue;
                            }
                        }

                        validFreshMarks.Add(m);
                    }

                    // Если реальных новых или исправленных оценок нет (например, изменился только формат хэша при деплое или оценка старая),
                    // ложное уведомление НЕ отправляется, а хэш тихо актуализируется в БД.
                    if (validFreshMarks.Count > 0)
                    {
                        if (memCache != null)
                        {
                            foreach (var m in validFreshMarks)
                            {
                                if (!string.IsNullOrEmpty(m.Id))
                                {
                                    string notifValKey = $"last_notif_val:{studentId}_{m.Id}";
                                    memCache.Set(notifValKey, m.Value, TimeSpan.FromHours(24));

                                    string flapCountKey = $"notif_flap_cnt:{studentId}_{m.Id}";
                                    int flapCount = memCache.Get<int>(flapCountKey);
                                    memCache.Set(flapCountKey, flapCount + 1, TimeSpan.FromHours(6));
                                }
                            }
                        }

                        _logger.LogInformation($"[GradeWatcher] 🔔 Найдено свежих оценок: {validFreshMarks.Count} шт. для user={user.Id} (student={studentId}): " +
                            string.Join(", ", validFreshMarks.Select(m => $"{m.SubjectName} '{m.Value}' ({m.Date}, id={m.Id})")));

                        var marksToSend = validFreshMarks.Take(5).ToList();
                        var sb = new StringBuilder();
                        if (isCorrection)
                        {
                            sb.AppendLine(marksToSend.Count == 1
                                ? "✏️ *Исправление оценки в электронном журнале!*"
                                : "✏️ *Исправление оценок в электронном журнале!*");
                        }
                        else
                        {
                            sb.AppendLine(marksToSend.Count == 1
                                ? "🔥 *Новая оценка в электронном журнале!*"
                                : "🔥 *Новые оценки в электронном журнале!*");
                        }

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

                        int currentQuarter = DiaryInteractionService.GetCurrentQuarter();

                        // Группируем свежие оценки по предметам для структурированного вывода и расчета динамики
                        var subjectGroups = marksToSend.GroupBy(m => !string.IsNullOrWhiteSpace(m.SubjectName) ? m.SubjectName.Trim() : "Урок");
                        foreach (var group in subjectGroups)
                        {
                            string subjectName = group.Key;
                            string safeSubject = TelegramMarkdownEscaper.Escape(subjectName);
                            sb.AppendLine($"📚 *{safeSubject}*");

                            // Определяем целевую четверть для данного предмета (если оценка выставлена на ноябрь / следующую четверть)
                            int targetQuarter = group
                                .Select(x => DiaryInteractionService.GetMarkQuarter(x))
                                .FirstOrDefault(q => q > 0);
                            if (targetQuarter == 0) targetQuarter = currentQuarter;

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
                                int markQuarter = DiaryInteractionService.GetMarkQuarter(m);
                                string quarterSuffix = (markQuarter > 0 && markQuarter != currentQuarter) ? $" [{markQuarter}-я четверть]" : "";
                                string dateStr = !string.IsNullOrEmpty(m.Date) ? $" • {TelegramMarkdownEscaper.Escape(m.Date)}{TelegramMarkdownEscaper.Escape(quarterSuffix)}" : "";

                                sb.AppendLine($"⭐️ Оценка: *{TelegramMarkdownEscaper.Escape(markDisplay)}*{weight}{reason}{dateStr}");
                            }

                            // Динамика среднего балла и прогноз рассчитываются строго в рамках четверти полученных оценок
                            var prevQuarterMarks = previousMarks?.Where(x => DiaryInteractionService.GetMarkQuarter(x) == targetQuarter);
                            var currQuarterMarks = marks.Where(x => DiaryInteractionService.GetMarkQuarter(x) == targetQuarter);

                            var subjDynamics = GradeCalculator.ComputeSubjectDynamics(prevQuarterMarks, currQuarterMarks, subjectName);
                            string? qLabel = (targetQuarter != currentQuarter) ? $"{targetQuarter}-я четв." : null;

                            string gpaLine = subjDynamics.FormatSubjectGpaLine(qLabel);
                            if (!string.IsNullOrEmpty(gpaLine))
                                sb.AppendLine(gpaLine);

                            string forecastLine = subjDynamics.FormatForecastLine(qLabel);
                            if (!string.IsNullOrEmpty(forecastLine))
                                sb.AppendLine(forecastLine);

                            sb.AppendLine();
                        }

                        if (validFreshMarks.Count > marksToSend.Count)
                        {
                            int remaining = validFreshMarks.Count - marksToSend.Count;
                            string markWord = remaining % 10 == 1 && remaining % 100 != 11 ? "оценка" :
                                              (remaining % 10 is >= 2 and <= 4 && (remaining % 100 < 10 || remaining % 100 >= 20) ? "оценки" : "оценок");
                            sb.AppendLine($"_...и ещё {remaining} {markWord}_");
                            sb.AppendLine();
                        }

                        // Динамика общего среднего балла по текущей четверти
                        var prevOverallMarks = previousMarks?.Where(x => DiaryInteractionService.GetMarkQuarter(x) == currentQuarter);
                        var currOverallMarks = marks.Where(x => DiaryInteractionService.GetMarkQuarter(x) == currentQuarter);
                        var overallDynamics = GradeCalculator.ComputeOverallDynamics(prevOverallMarks, currOverallMarks);
                        string overallLine = overallDynamics.FormatOverallGpaLine();
                        if (!string.IsNullOrEmpty(overallLine))
                        {
                            sb.AppendLine(overallLine);
                            sb.AppendLine();
                        }

                        sb.AppendLine("💡 _Подробная статистика и цели в меню ниже:_");

                        string firstSubj = validFreshMarks.FirstOrDefault()?.SubjectName ?? "";
                        var config = userScope.ServiceProvider.GetService<IConfiguration>();
                        string baseMiniAppUrl = config?["MiniAppUrl"] ?? "https://miragediary.space";
                        string encodedSubj = !string.IsNullOrEmpty(firstSubj) ? $"&subject={Uri.EscapeDataString(firstSubj)}" : "";
                        string simWebAppUrl = $"{baseMiniAppUrl.TrimEnd('/')}/?mode=simulator&userId={user.Id}{encodedSubj}";

                        var tgMarkup = new InlineKeyboardMarkup(new[]
                        {
                            new[]
                            {
                                InlineKeyboardButton.WithCallbackData("📊 Все оценки", "cmd_marks"),
                                InlineKeyboardButton.WithWebApp("🔮 Симулятор оценок", new WebAppInfo { Url = simWebAppUrl })
                            },
                            new[]
                            {
                                InlineKeyboardButton.WithCallbackData("🎯 Калькулятор целей", "cmd_calc"),
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

                            if (!string.IsNullOrEmpty(user.LinkedUserId))
                            {
                                try
                                {
                                    if (user.LinkedUserId.StartsWith("max_") && maxBotClient != null)
                                    {
                                        if (long.TryParse(user.LinkedUserId.Replace("max_", ""), out long peerMaxId))
                                        {
                                            await maxBotClient.SendMessageAsync(new SendMessageRequest
                                            {
                                                UserId = peerMaxId,
                                                Text = sb.ToString(),
                                                Format = MessageFormat.Markdown,
                                                Attachments = maxAttachments
                                            });
                                            _logger.LogInformation($"[GradeWatcher] 🔀 Резервный пуш успешно доставлен в связанный MAX аккаунт {user.LinkedUserId}.");
                                        }
                                    }
                                    else if (!user.LinkedUserId.StartsWith("max_") && botClient != null)
                                    {
                                        await botClient.SendMessage(
                                            chatId: user.LinkedUserId,
                                            text: sb.ToString(),
                                            parseMode: ParseMode.Markdown,
                                            replyMarkup: tgMarkup,
                                            cancellationToken: stoppingToken);
                                        _logger.LogInformation($"[GradeWatcher] 🔀 Резервный пуш успешно доставлен в связанный TG аккаунт {user.LinkedUserId}.");
                                    }
                                }
                                catch (Exception peerEx)
                                {
                                    _logger.LogWarning($"[GradeWatcher] Резервная доставка в связанный аккаунт {user.LinkedUserId} также не удалась: {peerEx.Message}");
                                }
                            }
                        }
                        }
                    }
                    else
                    {
                        _logger.LogInformation($"[GradeWatcher] Хэш изменился для {user.Id}, но новых актуальных оценок нет (подавлены фильтром давности/флаппинга). Ложное срабатывание предотвращено.");
                    }

                    user.LastMarksHash = currentHash;
                    await db.SaveChangesAsync(stoppingToken);
                }
                else
                {
                    // Хэш не изменился — новых оценок нет, но сохраняем актуализированный UpdatedAt в кэше
                    await db.SaveChangesAsync(stoppingToken);
                }
            }
            catch (DiarySessionExpiredException)
            {
                // Сессия устарела, воркер рефреша подхватит её отдельно
            }
            catch (DiaryMaintenanceException mex)
            {
                _logger.LogWarning($"[GradeWatcher] 🛡️ Gosuslugi rate limit hit for {user.Id}: {mex.Message}. Pausing worker loop for 30s.");
                await Task.Delay(30000, stoppingToken);
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
            .ThenBy(m => m.Id)
            .ThenBy(m => m.LessonId)
            .ThenBy(m => m.Value)
            .ThenBy(m => m.WorkKind);

        var sb = new StringBuilder();
        foreach (var m in ordered)
        {
            sb.Append($"{m.Id}|{m.LessonId}|{m.SubjectName}|{m.Date}|{m.Value}|{m.Weight}|{m.WorkKind};");
        }

        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes);
    }

    public static string GetMarkKey(GosuslugiMarkItem m)
    {
        if (!string.IsNullOrEmpty(m.Id))
            return m.Id;
        string lessonSuffix = !string.IsNullOrEmpty(m.LessonId) ? $"_{m.LessonId}" : "";
        return $"{m.SubjectName}_{m.Date}_{m.Value}_{m.Weight}_{m.WorkKind}{lessonSuffix}";
    }
}
