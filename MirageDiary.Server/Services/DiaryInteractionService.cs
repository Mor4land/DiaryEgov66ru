using System;
using System.Text.Json;
using System.Threading.Tasks;
using MirageDiary.Core;
using MirageDiary.Server.Data;
using MirageDiary.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using MirageDiary.Server.Security;

namespace MirageDiary.Server.Services;

public class DiaryInteractionService
{
    private readonly IServiceProvider _serviceProvider;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime ExpireAt, List<SubjectGradeSummary> Data)> _marksCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime ExpireAt, string Data)> _scheduleCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime ExpireAt, string Data)> _homeworkCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime ExpireAt, string Data)> _profileCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime ExpireAt, List<GosuslugiStudentInfo> Data)> _childrenCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<string>> _inFlightRequests = new();

    public DiaryInteractionService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    private const string DiaryHealthKey = "diary";
    private const string DiaryHealthName = "API дневника";

    /// <summary>Отмечает успешный ответ регионального API — источник состояния для админ-консоли.</summary>
    private void ReportDiaryOk() =>
        _serviceProvider.GetService<ServiceHealthService>()?.ReportSuccess(DiaryHealthKey, DiaryHealthName);

    /// <summary>Отмечает неудачу обращения к региональному API с причиной, как она произошла.</summary>
    private void ReportDiaryFailure(string reason)
    {
        _serviceProvider.GetService<ServiceHealthService>()?.ReportFailure(DiaryHealthKey, DiaryHealthName, reason);
        _serviceProvider.GetService<ServerEventLog>()?.Warning(DiaryHealthName, $"Запрос не удался: {reason}");
    }

    private bool IsDiaryMaintenanceActive()
    {
        var config = _serviceProvider.GetService<Microsoft.Extensions.Configuration.IConfiguration>();
        return config != null && DiaryMaintenanceSettings.IsDiaryInMaintenance(config);
    }

    private static readonly TimeSpan FlightTimeout = TimeSpan.FromSeconds(12);

    /// <summary>
    /// SingleFlight pattern with strict timeout and zombie task mitigation:
    /// Deduplicates concurrent requests for the exact same resource.
    /// If Gosuslugi hangs or stalls in a half-open TCP state, the hard timeout (12s)
    /// unblocks all waiters, evicts the in-flight key, and triggers fallback without deadlock.
    /// </summary>
    private static async Task<string> ExecuteSingleFlightAsync(string key, Func<CancellationToken, Task<string>> fetcher)
    {
        while (true)
        {
            if (_inFlightRequests.TryGetValue(key, out var inFlightTask))
            {
                // Waiter: wait for leader with our own timeout to prevent hanging forever
                using var waiterCts = new CancellationTokenSource(FlightTimeout);
                try
                {
                    return await inFlightTask.WaitAsync(waiterCts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Leader stalled past timeout; force-evict zombie task and retry loop
                    _inFlightRequests.TryRemove(key, out _);
                    continue;
                }
            }

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_inFlightRequests.TryAdd(key, tcs.Task))
            {
                using var leaderCts = new CancellationTokenSource(FlightTimeout);
                try
                {
                    // Run fetcher with hard timeout cancellation token
                    var result = await fetcher(leaderCts.Token).WaitAsync(leaderCts.Token);
                    tcs.TrySetResult(result);
                    return result;
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                    throw;
                }
                finally
                {
                    // Guaranteed eviction from map: never leaves a zombie task in dictionary!
                    _inFlightRequests.TryRemove(key, out _);
                }
            }
        }
    }

    public async Task<string> GetHomeworkStringAsync(string userId, string? requestedDateStr = null, bool forceRefresh = false)
    {
        string memCacheKey = $"{userId}_{requestedDateStr ?? "today"}";
        if (!forceRefresh && _homeworkCache.TryGetValue(memCacheKey, out var cached) && cached.ExpireAt > DateTime.UtcNow)
        {
            _serviceProvider.GetService<ServerMetricsService>()?.RecordCacheHit();
            return cached.Data;
        }

        using var scope = _serviceProvider.CreateScope();
        var tokenStore = scope.ServiceProvider.GetRequiredService<ITokenStore>();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var p2pCache = scope.ServiceProvider.GetRequiredService<SharedClassroomCacheService>();
        var maintenanceMode = scope.ServiceProvider.GetRequiredService<MaintenanceModeService>();
        
        var tokenData = await tokenStore.GetTokensAsync(userId);
        if (tokenData == null || string.IsNullOrEmpty(tokenData.AccessToken))
        {
            return "Вы не авторизованы. Пожалуйста, откройте Mini App и войдите в систему Госуслуг.";
        }

        var user = await dbContext.Users.FindAsync(userId);
        string orgName = user?.OrgName ?? "Unknown";
        string className = user?.ClassName ?? "Unknown";
        var localTime = DateTime.UtcNow.AddHours(5);

        DateTime targetDate = localTime.Date;
        bool specificDateRequested = false;

        if (!string.IsNullOrWhiteSpace(requestedDateStr))
        {
            string cleanStr = requestedDateStr.Trim().ToLowerInvariant();
            if (cleanStr == "завтра" || cleanStr == "tomorrow")
            {
                targetDate = localTime.Date.AddDays(1);
                specificDateRequested = true;
            }
            else if (cleanStr == "вчера" || cleanStr == "yesterday")
            {
                targetDate = localTime.Date.AddDays(-1);
                specificDateRequested = true;
            }
            else if (cleanStr == "сегодня" || cleanStr == "today")
            {
                targetDate = localTime.Date;
                specificDateRequested = true;
            }
            else if (DateTime.TryParseExact(cleanStr, new[] { "yyyy-MM-dd", "dd.MM.yyyy", "dd.MM", "d.M.yyyy", "d.M" }, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsedDate))
            {
                if (cleanStr.Length <= 5)
                {
                    parsedDate = new DateTime(localTime.Year, parsedDate.Month, parsedDate.Day);
                }
                targetDate = parsedDate;
                specificDateRequested = true;
            }
        }

        string targetDateStr = targetDate.ToString("yyyy-MM-dd");
        string displayDate = targetDate.ToString("dd.MM.yyyy");

        string cacheKey = p2pCache.GenerateKey(orgName, className, targetDateStr);

        if (maintenanceMode.IsMaintenanceModeActive())
        {
            return await GetFromCacheFallback(p2pCache, cacheKey, $"🔧 _Портал на техработах, загружено из кэша класса на {displayDate}:_\n\n");
        }

        // ── P2P CACHE FIRST (30 min TTL) ──
        if (!string.IsNullOrEmpty(orgName) && orgName != "Unknown" && !string.IsNullOrEmpty(className) && className != "Unknown")
        {
            var cachedJson = await p2pCache.GetCachedDataAsync(cacheKey, TimeSpan.FromMinutes(30));
            if (!string.IsNullOrEmpty(cachedJson))
            {
                try
                {
                    var cachedHw = JsonSerializer.Deserialize<List<HomeworkItem>>(cachedJson);
                    if (cachedHw != null && cachedHw.Count > 0)
                    {
                        _serviceProvider.GetService<ServerMetricsService>()?.RecordCacheHit();
                        string response = $"📚 Домашнее задание на {displayDate} _(⚡ из кэша класса)_:\n\n";
                        foreach (var item in cachedHw)
                        {
                            string title = TelegramMarkdownEscaper.Escape(!string.IsNullOrWhiteSpace(item.LessonName) ? item.LessonName : "Предмет");
                            string desc = TelegramMarkdownEscaper.Escape(string.IsNullOrWhiteSpace(item.Description) ? "Нет описания" : item.Description);
                            response += $"• *{title}*: {desc}\n";
                        }
                        return response;
                    }
                }
                catch { }
            }
        }

        if (IsDiaryMaintenanceActive())
        {
            return "⚠️ *Электронный дневник временно недоступен*\n\nШлюз Госуслуг / ФГИС «Моя Школа» проводит регламентные работы. Новые данные загрузятся автоматически после завершения работ.";
        }

        string flightKey = $"hw_{cacheKey}";
        return await ExecuteSingleFlightAsync(flightKey, async ct =>
        {
            var httpClientFactory = scope.ServiceProvider.GetRequiredService<System.Net.Http.IHttpClientFactory>();
            var client = httpClientFactory.CreateClient();
            var clientWithUser = new DiaryClient(client, tokenStore, userId);

            try
            {
                _serviceProvider.GetService<ServerMetricsService>()?.RecordExternalCall();
                var hw = await clientWithUser.GetHomeworkAsync(targetDateStr);
                ReportDiaryOk();
                // Save to P2P cache for other students in the class
                if (hw != null && hw.Count > 0 && !string.IsNullOrEmpty(orgName) && orgName != "Unknown")
                {
                    var jsonToCache = JsonSerializer.Serialize(hw);
                    await p2pCache.SaveCachedDataAsync(cacheKey, jsonToCache);
                }

                if (hw != null && hw.Count > 0)
                {
                    string response = $"📚 Домашнее задание на {displayDate}:\n\n";
                    foreach (var item in hw)
                    {
                        string title = TelegramMarkdownEscaper.Escape(!string.IsNullOrWhiteSpace(item.LessonName) ? item.LessonName : "Предмет");
                        string desc = TelegramMarkdownEscaper.Escape(string.IsNullOrWhiteSpace(item.Description) ? "Нет описания" : item.Description);
                        response += $"• *{title}*: {desc}\n";
                    }
                    return response;
                }

                if (specificDateRequested)
                {
                    return $"Домашнего задания на {displayDate} нет.";
                }

                // Fallback to tomorrow if today's homework is empty and no specific date was asked
                var tomorrow = localTime.Date.AddDays(1);
                string targetDateTomorrow = tomorrow.ToString("yyyy-MM-dd");
                string displayTomorrow = tomorrow.ToString("dd.MM.yyyy");

                var hwTomorrow = await clientWithUser.GetHomeworkAsync(targetDateTomorrow);
                if (hwTomorrow != null && hwTomorrow.Count > 0)
                {
                    string response = $"Домашнего задания на сегодня ({displayDate}) нет.\n\n📚 Домашнее задание на {displayTomorrow}:\n\n";
                    foreach (var item in hwTomorrow)
                    {
                        string title = TelegramMarkdownEscaper.Escape(!string.IsNullOrWhiteSpace(item.LessonName) ? item.LessonName : "Предмет");
                        string desc = TelegramMarkdownEscaper.Escape(string.IsNullOrWhiteSpace(item.Description) ? "Нет описания" : item.Description);
                        response += $"• *{title}*: {desc}\n";
                    }
                    _homeworkCache[memCacheKey] = (DateTime.UtcNow.AddMinutes(3), response);
                    return response;
                }

                string noHw = $"Домашнего задания на {displayDate} и {displayTomorrow} нет.";
                _homeworkCache[memCacheKey] = (DateTime.UtcNow.AddMinutes(3), noHw);
                return noHw;
            }
            catch (OperationCanceledException)
            {
                ReportDiaryFailure("таймаут 12 с");
                return await GetFromCacheFallback(p2pCache, cacheKey, $"⏱️ Сервера Госуслуг не ответили за 12 сек (таймаут сети).\nВывожу последние сохраненные данные на {displayDate}:\n\n");
            }
            catch (TimeoutException)
            {
                ReportDiaryFailure("таймаут 12 с");
                return await GetFromCacheFallback(p2pCache, cacheKey, $"⏱️ Сервера Госуслуг не ответили за 12 сек (таймаут сети).\nВывожу последние сохраненные данные на {displayDate}:\n\n");
            }
            catch (DiaryMaintenanceException)
            {
                ReportDiaryFailure("портал на техработах");
                maintenanceMode.ActivateMaintenanceMode(15);
                return await GetFromCacheFallback(p2pCache, cacheKey, "⚠️ Обнаружены неполадки Дневника. Включен резервный режим на 15 мин.\nВывожу последние закэшированные данные:\n\n");
            }
            catch (DiarySessionExpiredException ex)
            {
                return $"🔒 {ex.Message}";
            }
            catch (Exception ex)
            {
                ReportDiaryFailure(ex.Message);
                return $"Ошибка при получении данных: {ex.Message}";
            }
        });
    }

    public async Task<string> GetScheduleStringAsync(string userId, string? requestedDateStr = null, bool forceRefresh = false)
    {
        string cacheKey = $"{userId}_{requestedDateStr ?? "today"}";
        if (!forceRefresh && _scheduleCache.TryGetValue(cacheKey, out var cached) && cached.ExpireAt > DateTime.UtcNow)
        {
            _serviceProvider.GetService<ServerMetricsService>()?.RecordCacheHit();
            return cached.Data;
        }

        using var scope = _serviceProvider.CreateScope();
        var tokenStore = scope.ServiceProvider.GetRequiredService<ITokenStore>();
        
        var tokenData = await tokenStore.GetTokensAsync(userId);
        if (tokenData == null || string.IsNullOrEmpty(tokenData.AccessToken))
        {
            return "Вы не авторизованы. Пожалуйста, откройте Mini App и войдите в систему Госуслуг.";
        }

        var localTime = DateTime.UtcNow.AddHours(5);
        DateTime targetDate = localTime.Date;

        if (!string.IsNullOrWhiteSpace(requestedDateStr))
        {
            string cleanStr = requestedDateStr.Trim().ToLowerInvariant();
            if (cleanStr == "завтра" || cleanStr == "tomorrow") targetDate = localTime.Date.AddDays(1);
            else if (cleanStr == "вчера" || cleanStr == "yesterday") targetDate = localTime.Date.AddDays(-1);
            else if (cleanStr == "сегодня" || cleanStr == "today") targetDate = localTime.Date;
            else if (DateTime.TryParseExact(cleanStr, new[] { "yyyy-MM-dd", "dd.MM.yyyy", "dd.MM", "d.M.yyyy", "d.M" }, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsedDate))
            {
                if (cleanStr.Length <= 5) parsedDate = new DateTime(localTime.Year, parsedDate.Month, parsedDate.Day);
                targetDate = parsedDate;
            }
        }

        string targetDateStr = targetDate.ToString("yyyy-MM-dd");
        string displayDate = targetDate.ToString("dd.MM.yyyy");
        if (IsDiaryMaintenanceActive())
        {
            return "⚠️ *Электронный дневник временно недоступен*\n\nШлюз Госуслуг / ФГИС «Моя Школа» проводит регламентные работы. Новые данные загрузятся автоматически после завершения работ.";
        }

        string flightKey = $"sched_{cacheKey}";
        return await ExecuteSingleFlightAsync(flightKey, async ct =>
        {
            var httpClientFactory = scope.ServiceProvider.GetRequiredService<System.Net.Http.IHttpClientFactory>();
            var client = httpClientFactory.CreateClient();
            var clientWithUser = new DiaryClient(client, tokenStore, userId);

            try
            {
                _serviceProvider.GetService<ServerMetricsService>()?.RecordExternalCall();
                var lessons = await clientWithUser.GetScheduleAsync(targetDateStr);
                ReportDiaryOk();
                if (lessons != null && lessons.Count > 0)
                {
                    string res = $"📅 *Расписание на {displayDate}:*\n\n";
                    int num = 1;
                    foreach (var l in lessons)
                    {
                        string rawSubject = !string.IsNullOrEmpty(l.SubjectName) ? l.SubjectName : l.LessonName;
                        string subject = TelegramMarkdownEscaper.Escape(rawSubject);
                        string time = !string.IsNullOrEmpty(l.StartTime) ? $" ({TelegramMarkdownEscaper.Escape(l.StartTime)}-{TelegramMarkdownEscaper.Escape(l.EndTime)})" : "";
                        string room = !string.IsNullOrEmpty(l.Room) ? $" (Каб. {TelegramMarkdownEscaper.Escape(l.Room)})" : "";
                        res += $"{num}. *{subject}*{time}{room}\n";
                        num++;
                    }
                    _scheduleCache[cacheKey] = (DateTime.UtcNow.AddMinutes(3), res);
                    return res;
                }
                string noLessons = $"📅 Расписание уроков на {displayDate} отсутствует или не загружено.";
                _scheduleCache[cacheKey] = (DateTime.UtcNow.AddMinutes(3), noLessons);
                return noLessons;
            }
            catch (OperationCanceledException)
            {
                ReportDiaryFailure("таймаут 12 с");
                return $"⏱️ Сервера Госуслуг не ответили за 12 сек (таймаут сети). Попробуйте обновить чуть позже.";
            }
            catch (TimeoutException)
            {
                ReportDiaryFailure("таймаут 12 с");
                return $"⏱️ Сервера Госуслуг не ответили за 12 сек (таймаут сети). Попробуйте обновить чуть позже.";
            }
            catch (DiarySessionExpiredException ex)
            {
                return $"🔒 {ex.Message}";
            }
            catch (Exception ex)
            {
                ReportDiaryFailure(ex.Message);
                return $"Ошибка при получении расписания: {ex.Message}";
            }
        });
    }

    public class SubjectGradeSummary
    {
        public string SubjectName { get; set; } = string.Empty;
        public List<GosuslugiMarkItem> Marks { get; set; } = new();
        public double AverageGpa { get; set; }
        public int WeightedSum { get; set; }
        public int TotalWeight { get; set; }
        public int FinalForecast { get; set; }
        public string StatusEmoji { get; set; } = "⚪";
    }

    public async Task<List<SubjectGradeSummary>> GetSubjectGradesSummaryAsync(string userId, bool forceRefresh = false)
    {
        if (!forceRefresh && _marksCache.TryGetValue(userId, out var cached) && cached.ExpireAt > DateTime.UtcNow)
        {
            return cached.Data;
        }

        if (IsDiaryMaintenanceActive())
        {
            return new List<SubjectGradeSummary>();
        }

        using var scope = _serviceProvider.CreateScope();
        var tokenStore = scope.ServiceProvider.GetRequiredService<ITokenStore>();
        
        var tokenData = await tokenStore.GetTokensAsync(userId);
        if (tokenData == null || string.IsNullOrEmpty(tokenData.AccessToken))
        {
            return new List<SubjectGradeSummary>();
        }

        var httpClientFactory = scope.ServiceProvider.GetRequiredService<System.Net.Http.IHttpClientFactory>();
        var client = httpClientFactory.CreateClient();
        var clientWithUser = new DiaryClient(client, tokenStore, userId);

        var marks = await clientWithUser.GetMarksAsync();
        if (marks == null || marks.Count == 0) return new List<SubjectGradeSummary>();

        var grouped = marks
            .Where(m => !string.IsNullOrWhiteSpace(m.SubjectName))
            .GroupBy(m => m.SubjectName.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key);

        var result = new List<SubjectGradeSummary>();

        foreach (var group in grouped)
        {
            var summary = new SubjectGradeSummary
            {
                SubjectName = group.Key,
                Marks = group.ToList()
            };

            int wSum = 0;
            int totalW = 0;

            foreach (var m in summary.Marks)
            {
                if (int.TryParse(m.Value, out int gradeVal) && gradeVal >= 1 && gradeVal <= 5)
                {
                    int w = (m.Weight.HasValue && m.Weight.Value > 0) ? m.Weight.Value : 1;
                    wSum += gradeVal * w;
                    totalW += w;
                }
            }

            summary.WeightedSum = wSum;
            summary.TotalWeight = totalW;

            if (totalW > 0)
            {
                summary.AverageGpa = Math.Round((double)wSum / totalW, 2);
                summary.FinalForecast = (int)Math.Round(summary.AverageGpa, MidpointRounding.AwayFromZero);

                if (summary.AverageGpa >= 4.5) summary.StatusEmoji = "🟢";
                else if (summary.AverageGpa >= 3.5) summary.StatusEmoji = "🟡";
                else summary.StatusEmoji = "🔴";
            }
            else
            {
                summary.AverageGpa = 0;
                summary.FinalForecast = 0;
                summary.StatusEmoji = "⚪";
            }

            result.Add(summary);
        }

        _marksCache[userId] = (DateTime.UtcNow.AddMinutes(3), result);
        return result;
    }

    public async Task<string> GetMarksStringAsync(string userId, bool forceRefresh = false)
    {
        try
        {
            if (!forceRefresh && _marksCache.TryGetValue(userId, out var memCached) && memCached.ExpireAt > DateTime.UtcNow && memCached.Data.Count > 0)
            {
                // Format from cache
            }
            else if (IsDiaryMaintenanceActive())
            {
                return "⚠️ *Электронный дневник временно недоступен*\n\nШлюз Госуслуг / ФГИС «Моя Школа» проводит регламентные работы. Новые данные загрузятся автоматически после завершения работ.";
            }

            var subjects = await GetSubjectGradesSummaryAsync(userId, forceRefresh);
            if (subjects != null && subjects.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("📊 *Ваши оценки и средний балл:*");
                sb.AppendLine();

                int totalAllWeight = 0;
                int totalAllSum = 0;

                foreach (var s in subjects)
                {
                    string gpaStr = s.TotalWeight > 0 ? $"{s.AverageGpa:F2}" : "—";
                    string forecastStr = s.FinalForecast > 0 ? $" (итог: *{s.FinalForecast}*)" : "";
                    
                    string safeSubject = TelegramMarkdownEscaper.Escape(s.SubjectName);
                    sb.AppendLine($"{s.StatusEmoji} *{safeSubject}* — `{gpaStr}`{forecastStr}");

                    // List marks
                    var markStrings = s.Marks.Select(m =>
                    {
                        int w = (m.Weight.HasValue && m.Weight.Value > 1) ? m.Weight.Value : 1;
                        string wSuffix = w > 1 ? $"×{w}" : "";
                        return $"{m.Value}{wSuffix}";
                    });

                    sb.AppendLine($"  └ _{string.Join("  ", markStrings)}_");
                    sb.AppendLine();

                    totalAllSum += s.WeightedSum;
                    totalAllWeight += s.TotalWeight;
                }

                if (totalAllWeight > 0)
                {
                    double overallGpa = Math.Round((double)totalAllSum / totalAllWeight, 2);
                    sb.AppendLine($"🏆 *Общий средний балл:* `{overallGpa:F2}`");
                }

                return sb.ToString().TrimEnd();
            }

            return "📊 Оценок в дневнике Госуслуг пока нет.";
        }
        catch (DiarySessionExpiredException ex)
        {
            return $"🔒 {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Ошибка при получении оценок: {ex.Message}";
        }
    }

    public async Task<string> GetProfileStringAsync(string userId, bool forceRefresh = false)
    {
        if (!forceRefresh && _profileCache.TryGetValue(userId, out var cached) && cached.ExpireAt > DateTime.UtcNow)
        {
            return cached.Data;
        }

        using var scope = _serviceProvider.CreateScope();
        var tokenStore = scope.ServiceProvider.GetRequiredService<ITokenStore>();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var tokenData = await tokenStore.GetTokensAsync(userId);
        if (tokenData == null || string.IsNullOrEmpty(tokenData.AccessToken))
        {
            return "❌ Вы не авторизованы в системе Госуслуг. Войдите через Mini App.";
        }

        var user = await dbContext.Users.FindAsync(userId);
        var httpClientFactory = scope.ServiceProvider.GetRequiredService<System.Net.Http.IHttpClientFactory>();
        var client = httpClientFactory.CreateClient();
        var clientWithUser = new DiaryClient(client, tokenStore, userId);

        // Auto-resolve school and class if not present in DB
        if (user != null && (string.IsNullOrEmpty(user.OrgName) || string.IsNullOrEmpty(user.ClassName)))
        {
            try
            {
                var (sid, mdmId) = await clientWithUser.ResolveStudentIdsAsync();
                List<string> idsToTry = new List<string>();
                if (!string.IsNullOrEmpty(sid)) idsToTry.Add(sid);
                if (!string.IsNullOrEmpty(mdmId) && !idsToTry.Contains(mdmId)) idsToTry.Add(mdmId);

                var years = new[] { DateTime.UtcNow.Year.ToString(), (DateTime.UtcNow.Year - 1).ToString() };
                foreach (var id in idsToTry)
                {
                    foreach (var year in years)
                    {
                        var datamart = await clientWithUser.GetDatamartAsync(id, year, new List<string> { "student_classes" });
                        if (datamart != null)
                        {
                            foreach (var item in datamart)
                            {
                                if (item.Data != null)
                                {
                                    foreach (var elem in item.Data)
                                    {
                                        var classInfo = JsonSerializer.Deserialize<GosuslugiClassInfo>(elem.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                                        if (classInfo != null && (!string.IsNullOrEmpty(classInfo.ShortName) || !string.IsNullOrEmpty(classInfo.ClassNum)))
                                        {
                                            user.ClassName = $"{classInfo.ClassNum}{classInfo.ClassLetter}".Trim();
                                            user.OrgName = classInfo.ShortName;
                                            await dbContext.SaveChangesAsync();
                                            break;
                                        }
                                    }
                                }
                                if (!string.IsNullOrEmpty(user.OrgName)) break;
                            }
                        }
                        if (!string.IsNullOrEmpty(user.OrgName)) break;
                    }
                    if (!string.IsNullOrEmpty(user.OrgName)) break;
                }
            }
            catch { }
        }

        string profileCard = "👤 *Профиль пользователя*\n\n";

        try
        {
            var students = await clientWithUser.GetStudentInfoAsync();
            if (students != null && students.Count > 0)
            {
                var s = students[0];
                string gender = s.StudentGender == "M" ? "👨 Мужской" : s.StudentGender == "F" ? "👩 Женский" : s.StudentGender;

                profileCard += $"🎓 *Статус аккаунта*: {(s.Trusted ? "✅ Подтверждён ЕСИА" : "⚠️ Не подтверждён")}\n";
                if (!string.IsNullOrEmpty(gender))
                {
                    profileCard += $"🚻 *Пол*: `{gender}`\n";
                }
                profileCard += "\n";
            }
        }
        catch (Exception ex)
        {
            profileCard += $"⚠️ _Не удалось загрузить статус Госуслуг: {TelegramMarkdownEscaper.Escape(ex.Message)}_\n\n";
        }

        string platformName = userId.StartsWith("max_") ? "MAX" : "Telegram";
        profileCard += "⚙️ *Данные в MirageDiary:*\n";
        profileCard += $"🎭 *Роль*: `{user?.Role ?? "Student"}`\n";
        profileCard += $"🏫 *Школа*: `{user?.OrgName ?? "Не указана"}`\n";
        profileCard += $"📚 *Класс*: `{user?.ClassName ?? "Не указан"}`\n";
        profileCard += $"🔔 *Будильник*: {(user?.IsAlarmEnabled ?? true ? "🟢 Включен" : "🔴 Выключен")}\n" +
                       $"📱 *Платформа*: `{platformName}`\n";

        _profileCache[userId] = (DateTime.UtcNow.AddMinutes(5), profileCard);
        return profileCard;
    }

    public async Task<List<GosuslugiStudentInfo>> GetChildrenListAsync(string userId, bool forceRefresh = false)
    {
        if (!forceRefresh && _childrenCache.TryGetValue(userId, out var cached) && cached.ExpireAt > DateTime.UtcNow)
        {
            return cached.Data;
        }

        using var scope = _serviceProvider.CreateScope();
        var tokenStore = scope.ServiceProvider.GetRequiredService<ITokenStore>();

        var httpClientFactory = scope.ServiceProvider.GetRequiredService<System.Net.Http.IHttpClientFactory>();
        var client = httpClientFactory.CreateClient();
        var clientWithUser = new DiaryClient(client, tokenStore, userId);

        try
        {
            var res = await clientWithUser.GetStudentInfoAsync();
            if (res != null) _childrenCache[userId] = (DateTime.UtcNow.AddMinutes(5), res);
            return res ?? new List<GosuslugiStudentInfo>();
        }
        catch
        {
            return new List<GosuslugiStudentInfo>();
        }
    }

    public async Task<bool> SelectChildAsync(string userId, string studentId)
    {
        using var scope = _serviceProvider.CreateScope();
        var tokenStore = scope.ServiceProvider.GetRequiredService<ITokenStore>();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var user = await dbContext.Users.FindAsync(userId);
        if (user == null) return false;

        user.SelectedStudentId = studentId;
        user.Role = "Parent";
        user.OrgName = "";
        user.ClassName = "";
        await dbContext.SaveChangesAsync();

        var httpClientFactory = scope.ServiceProvider.GetRequiredService<System.Net.Http.IHttpClientFactory>();
        var client = httpClientFactory.CreateClient();
        var clientWithUser = new DiaryClient(client, tokenStore, userId);

        try
        {
            var years = new[] { DateTime.UtcNow.Year.ToString(), (DateTime.UtcNow.Year - 1).ToString() };
            foreach (var year in years)
            {
                var datamart = await clientWithUser.GetDatamartAsync(studentId, year, new List<string> { "student_classes" });
                if (datamart != null)
                {
                    foreach (var item in datamart)
                    {
                        if (item.Data != null)
                        {
                            foreach (var elem in item.Data)
                            {
                                var classInfo = JsonSerializer.Deserialize<GosuslugiClassInfo>(elem.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                                if (classInfo != null && (!string.IsNullOrEmpty(classInfo.ShortName) || !string.IsNullOrEmpty(classInfo.ClassNum)))
                                {
                                    user.ClassName = $"{classInfo.ClassNum}{classInfo.ClassLetter}".Trim();
                                    user.OrgName = classInfo.ShortName;
                                    await dbContext.SaveChangesAsync();
                                    break;
                                }
                            }
                        }
                        if (!string.IsNullOrEmpty(user.OrgName)) break;
                    }
                }
                if (!string.IsNullOrEmpty(user.OrgName)) break;
            }
        }
        catch { }

        return true;
    }

    private async Task<string> GetFromCacheFallback(SharedClassroomCacheService cacheService, string cacheKey, string prefix)
    {
        var json = await cacheService.GetCachedDataAsync(cacheKey);
        if (string.IsNullOrEmpty(json))
        {
            return prefix + "К сожалению, в кэше сервера еще нет данных для вашего класса за этот день.";
        }

        try
        {
            var hw = JsonSerializer.Deserialize<System.Collections.Generic.List<HomeworkItem>>(json);
            if (hw == null || hw.Count == 0) return prefix + "Домашнего задания на сегодня нет (из кэша).";

            string response = prefix;
            foreach (var item in hw)
            {
                string rawTitle = !string.IsNullOrWhiteSpace(item.LessonName) ? item.LessonName : "Предмет";
                string safeTitle = TelegramMarkdownEscaper.Escape(rawTitle);
                string safeDesc = TelegramMarkdownEscaper.Escape(item.Description ?? string.Empty);
                response += $"• *{safeTitle}*: {safeDesc}\n";
            }
            return response;
        }
        catch
        {
            return prefix + "Ошибка чтения данных из кэша.";
        }
    }
}
