using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using MirageDiary.Core;
using MirageDiary.Core.Models;
using MirageDiary.Server.Data;
using MirageDiary.Server.Models;
using MirageDiary.Server.Security;

namespace MirageDiary.Server.Services;

public class DiaryInteractionService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IMemoryCache _memoryCache;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<string>> _inFlightRequests = new();

    public DiaryInteractionService(IServiceProvider serviceProvider, IMemoryCache memoryCache)
    {
        _serviceProvider = serviceProvider;
        _memoryCache = memoryCache;
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
        bool hasClassroom = !string.IsNullOrEmpty(orgName) && orgName != "Unknown" && !string.IsNullOrEmpty(className) && className != "Unknown";
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

        string effectiveStudentId = !string.IsNullOrEmpty(user?.SelectedStudentId) ? user.SelectedStudentId : (user?.StudentId ?? userId);
        string classCacheKey = hasClassroom ? p2pCache.GenerateKey(orgName, className, targetDateStr) : $"user_{userId}_{effectiveStudentId}_{targetDateStr}";
        string memCacheKey = hasClassroom ? $"class_hw:{classCacheKey}" : $"user_hw:{userId}_{effectiveStudentId}_{targetDateStr}";

        if (!forceRefresh && _memoryCache.TryGetValue(memCacheKey, out string? memCachedHw) && !string.IsNullOrEmpty(memCachedHw))
        {
            _serviceProvider.GetService<ServerMetricsService>()?.RecordCacheHit();
            return memCachedHw;
        }

        if (maintenanceMode.IsMaintenanceModeActive())
        {
            return await GetFromCacheFallback(p2pCache, classCacheKey, $"🔧 _Портал на техработах, загружено из кэша класса на {displayDate}:_\n\n");
        }

        // ── P2P CACHE FIRST (30 min TTL) ──
        if (hasClassroom)
        {
            var cachedJson = await p2pCache.GetCachedDataAsync(classCacheKey, TimeSpan.FromMinutes(30));
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
                        _memoryCache.Set(memCacheKey, response, new MemoryCacheEntryOptions
                        {
                            SlidingExpiration = TimeSpan.FromMinutes(10),
                            AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1),
                            Size = 1
                        });
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

        string flightKey = $"hw_{classCacheKey}";
        return await ExecuteSingleFlightAsync(flightKey, async ct =>
        {
            var httpClientFactory = scope.ServiceProvider.GetRequiredService<System.Net.Http.IHttpClientFactory>();
            var client = httpClientFactory.CreateClient("diary-proxy");
            var clientWithUser = new DiaryClient(client, tokenStore, userId);

            try
            {
                _serviceProvider.GetService<ServerMetricsService>()?.RecordExternalCall();
                var hw = await clientWithUser.GetHomeworkAsync(targetDateStr);
                ReportDiaryOk();
                // Save to P2P cache for other students in the class
                if (hw != null && hw.Count > 0 && hasClassroom)
                {
                    var jsonToCache = JsonSerializer.Serialize(hw);
                    await p2pCache.SaveCachedDataAsync(classCacheKey, jsonToCache);
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
                    _memoryCache.Set(memCacheKey, response, new MemoryCacheEntryOptions
                    {
                        SlidingExpiration = TimeSpan.FromMinutes(10),
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1),
                        Size = 1
                    });
                    return response;
                }

                if (specificDateRequested)
                {
                    string noHwSpecific = $"Домашнего задания на {displayDate} нет.";
                    _memoryCache.Set(memCacheKey, noHwSpecific, new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(5), Size = 1 });
                    return noHwSpecific;
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
                    _memoryCache.Set(memCacheKey, response, new MemoryCacheEntryOptions
                    {
                        SlidingExpiration = TimeSpan.FromMinutes(5),
                        Size = 1
                    });
                    return response;
                }

                string noHw = $"Домашнего задания на {displayDate} и {displayTomorrow} нет.";
                _memoryCache.Set(memCacheKey, noHw, new MemoryCacheEntryOptions
                {
                    SlidingExpiration = TimeSpan.FromMinutes(5),
                    Size = 1
                });
                return noHw;
            }
            catch (OperationCanceledException)
            {
                ReportDiaryFailure("таймаут 12 с");
                return await GetFromCacheFallback(p2pCache, classCacheKey, $"⏱️ Сервера Госуслуг не ответили за 12 сек (таймаут сети).\nВывожу последние сохраненные данные на {displayDate}:\n\n");
            }
            catch (TimeoutException)
            {
                ReportDiaryFailure("таймаут 12 с");
                return await GetFromCacheFallback(p2pCache, classCacheKey, $"⏱️ Сервера Госуслуг не ответили за 12 сек (таймаут сети).\nВывожу последние сохраненные данные на {displayDate}:\n\n");
            }
            catch (DiaryMaintenanceException)
            {
                ReportDiaryFailure("портал на техработах");
                maintenanceMode.ActivateMaintenanceMode(15);
                return await GetFromCacheFallback(p2pCache, classCacheKey, "⚠️ Обнаружены неполадки Дневника. Включен резервный режим на 15 мин.\nВывожу последние закэшированные данные:\n\n");
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
        using var scope = _serviceProvider.CreateScope();
        var tokenStore = scope.ServiceProvider.GetRequiredService<ITokenStore>();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var p2pCache = scope.ServiceProvider.GetRequiredService<SharedClassroomCacheService>();
        
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

        var user = await dbContext.Users.FindAsync(userId);
        string orgName = user?.OrgName ?? "";
        string className = user?.ClassName ?? "";
        string effectiveStudentId = !string.IsNullOrEmpty(user?.SelectedStudentId) ? user.SelectedStudentId : (user?.StudentId ?? userId);
        bool hasClassroom = !string.IsNullOrWhiteSpace(orgName) && orgName != "Unknown" && !string.IsNullOrWhiteSpace(className) && className != "Unknown";
        string classCacheKey = hasClassroom ? p2pCache.GenerateKey(orgName, className, targetDateStr) : $"user_{userId}_{effectiveStudentId}_{targetDateStr}";
        string memCacheKey = hasClassroom ? $"class_sched:{classCacheKey}" : $"user_sched:{userId}_{effectiveStudentId}_{targetDateStr}";

        // 1. In-memory check
        if (!forceRefresh && _memoryCache.TryGetValue(memCacheKey, out string? cachedSchedule) && !string.IsNullOrEmpty(cachedSchedule))
        {
            _serviceProvider.GetService<ServerMetricsService>()?.RecordCacheHit();
            return cachedSchedule;
        }

        // 2. Shared Classroom SQLite cache check (when available)
        if (!forceRefresh && hasClassroom)
        {
            var cachedJson = await p2pCache.GetCachedDataAsync(classCacheKey, TimeSpan.FromMinutes(45));
            if (!string.IsNullOrEmpty(cachedJson))
            {
                try
                {
                    var cachedLessons = JsonSerializer.Deserialize<List<GosuslugiLessonItem>>(cachedJson);
                    if (cachedLessons != null && cachedLessons.Count > 0)
                    {
                        _serviceProvider.GetService<ServerMetricsService>()?.RecordCacheHit();
                        string formatted = FormatSchedule(cachedLessons, displayDate);
                        _memoryCache.Set(memCacheKey, formatted, new MemoryCacheEntryOptions
                        {
                            SlidingExpiration = TimeSpan.FromMinutes(15),
                            AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(2),
                            Size = 1
                        });
                        return formatted;
                    }
                }
                catch { }
            }
        }

        string flightKey = $"sched_{classCacheKey}";
        return await ExecuteSingleFlightAsync(flightKey, async ct =>
        {
            var httpClientFactory = scope.ServiceProvider.GetRequiredService<System.Net.Http.IHttpClientFactory>();
            var client = httpClientFactory.CreateClient("diary-proxy");
            var clientWithUser = new DiaryClient(client, tokenStore, userId);

            try
            {
                _serviceProvider.GetService<ServerMetricsService>()?.RecordExternalCall();
                var lessons = await clientWithUser.GetScheduleAsync(targetDateStr);
                ReportDiaryOk();
                if (lessons != null && lessons.Count > 0)
                {
                    string res = FormatSchedule(lessons, displayDate);
                    _memoryCache.Set(memCacheKey, res, new MemoryCacheEntryOptions
                    {
                        SlidingExpiration = TimeSpan.FromMinutes(15),
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(2),
                        Size = 1
                    });
                    if (hasClassroom)
                    {
                        _ = p2pCache.SaveCachedDataAsync(classCacheKey, JsonSerializer.Serialize(lessons));
                    }
                    return res;
                }
                string noLessons = $"📅 Расписание уроков на {displayDate} отсутствует или не загружено.";
                _memoryCache.Set(memCacheKey, noLessons, new MemoryCacheEntryOptions
                {
                    SlidingExpiration = TimeSpan.FromMinutes(5),
                    Size = 1
                });
                return noLessons;
            }
            catch (OperationCanceledException)
            {
                ReportDiaryFailure("таймаут 12 с");
                if (hasClassroom)
                {
                    return await GetFromCacheFallback(p2pCache, classCacheKey, $"⏱️ Сервера Госуслуг не ответили за 12 сек (таймаут сети).\nВывожу последние сохраненные данные на {displayDate}:\n\n");
                }
                return $"⏱️ Сервера Госуслуг не ответили за 12 сек (таймаут сети). Попробуйте обновить чуть позже.";
            }
            catch (TimeoutException)
            {
                ReportDiaryFailure("таймаут 12 с");
                if (hasClassroom)
                {
                    return await GetFromCacheFallback(p2pCache, classCacheKey, $"⏱️ Сервера Госуслуг не ответили за 12 сек (таймаут сети).\nВывожу последние сохраненные данные на {displayDate}:\n\n");
                }
                return $"⏱️ Сервера Госуслуг не ответили за 12 сек (таймаут сети). Попробуйте обновить чуть позже.";
            }
            catch (DiaryMaintenanceException)
            {
                ReportDiaryFailure("портал на техработах");
                var maintenanceMode = scope.ServiceProvider.GetRequiredService<MaintenanceModeService>();
                maintenanceMode.ActivateMaintenanceMode(15);
                if (hasClassroom)
                {
                    return await GetFromCacheFallback(p2pCache, classCacheKey, "⚠️ Обнаружены неполадки Дневника. Включен резервный режим на 15 мин.\nВывожу последние закэшированные данные:\n\n");
                }
                return "⚠️ Дневник временно недоступен (техработы).";
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

    public static string FormatSchedule(List<GosuslugiLessonItem> lessons, string displayDate)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"📅 *Расписание на {displayDate}:*");
        sb.AppendLine();
        int num = 1;
        foreach (var l in lessons)
        {
            string rawSubject = !string.IsNullOrEmpty(l.SubjectName) ? l.SubjectName : (!string.IsNullOrEmpty(l.LessonName) ? l.LessonName : "Урок");
            string subject = TelegramMarkdownEscaper.Escape(rawSubject);

            string time = "";
            if (!string.IsNullOrEmpty(l.StartTime) && !string.IsNullOrEmpty(l.EndTime))
                time = $" `[{TelegramMarkdownEscaper.Escape(l.StartTime)}–{TelegramMarkdownEscaper.Escape(l.EndTime)}]`";
            else if (!string.IsNullOrEmpty(l.StartTime))
                time = $" `[{TelegramMarkdownEscaper.Escape(l.StartTime)}]`";

            string room = !string.IsNullOrEmpty(l.Room) ? $" • каб. {TelegramMarkdownEscaper.Escape(l.Room)}" : "";
            string distant = l.IsDistant ? " 🌐 Дистант" : "";
            string status = l.IsCancelled ? " ❌ *(Отменен)*" : (l.IsReplacement ? " 🔄 *(Замена)*" : "");

            sb.AppendLine($"{num}. *{subject}*{time}{room}{distant}{status}");

            if (!string.IsNullOrWhiteSpace(l.LessonType) && 
                !l.LessonType.Equals("Урок", StringComparison.OrdinalIgnoreCase) &&
                !l.LessonType.Equals("0", StringComparison.OrdinalIgnoreCase) &&
                !l.LessonType.Equals("Обычный урок", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine($"   🏷️ *Вид:* {TelegramMarkdownEscaper.Escape(l.LessonType.Trim())}");
            }

            if (!string.IsNullOrWhiteSpace(l.Theme))
            {
                sb.AppendLine($"   📖 *Тема:* _{TelegramMarkdownEscaper.Escape(l.Theme.Trim())}_");
            }

            if (!string.IsNullOrWhiteSpace(l.TeacherName))
            {
                sb.AppendLine($"   👤 {TelegramMarkdownEscaper.Escape(l.TeacherName.Trim())}");
            }

            num++;
        }
        return sb.ToString().TrimEnd();
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

    public static int GetCurrentQuarter(DateTime? date = null)
    {
        var dt = date ?? DateTime.UtcNow.AddHours(5);
        int m = dt.Month;
        if (m is 9 or 10) return 1;
        if (m is 11 or 12) return 2;
        if (m is 1 or 2 or 3) return 3;
        if (m is 4 or 5 or 6) return 4;
        return 1;
    }

    public static int GetMarkQuarter(string? dateStr)
    {
        if (string.IsNullOrWhiteSpace(dateStr)) return 0;
        string trimmed = dateStr.Trim();

        // 1. Unix timestamp (seconds or milliseconds)
        if (long.TryParse(trimmed, out long ts) && ts > 1_000_000_000L)
        {
            try
            {
                var dtTs = (ts > 10_000_000_000L)
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ts).DateTime
                    : DateTimeOffset.FromUnixTimeSeconds(ts).DateTime;
                int m = dtTs.Month;
                if (m is 9 or 10) return 1;
                if (m is 11 or 12) return 2;
                if (m is 1 or 2 or 3) return 3;
                if (m is 4 or 5 or 6) return 4;
            }
            catch { }
        }

        // 2. Direct string extraction (YYYY-MM-DD)
        if (trimmed.Length >= 10 && trimmed[4] == '-' && trimmed[7] == '-')
        {
            if (int.TryParse(trimmed.Substring(5, 2), out int month))
            {
                if (month is 9 or 10) return 1;
                if (month is 11 or 12) return 2;
                if (month is 1 or 2 or 3) return 3;
                if (month is 4 or 5 or 6) return 4;
            }
        }

        // 3. DateTimeOffset / DateTime parsing
        if (DateTimeOffset.TryParse(trimmed, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dto))
        {
            int m = dto.Month;
            if (m is 9 or 10) return 1;
            if (m is 11 or 12) return 2;
            if (m is 1 or 2 or 3) return 3;
            if (m is 4 or 5 or 6) return 4;
        }

        if (DateTime.TryParse(trimmed, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dt))
        {
            int m = dt.Month;
            if (m is 9 or 10) return 1;
            if (m is 11 or 12) return 2;
            if (m is 1 or 2 or 3) return 3;
            if (m is 4 or 5 or 6) return 4;
        }

        return 0;
    }

    public async Task<List<SubjectGradeSummary>> GetSubjectGradesSummaryAsync(string userId, int? quarter = null, bool forceRefresh = false)
    {
        int currentQ = GetCurrentQuarter();
        int targetQ = quarter ?? currentQ;

        if (IsDiaryMaintenanceActive())
        {
            return new List<SubjectGradeSummary>();
        }

        using var scope = _serviceProvider.CreateScope();
        var tokenStore = scope.ServiceProvider.GetRequiredService<ITokenStore>();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var user = await dbContext.Users.FindAsync(userId);
        string effectiveStudentId = !string.IsNullOrEmpty(user?.SelectedStudentId) ? user.SelectedStudentId : (user?.StudentId ?? userId);
        string memKey = $"user_marks:{userId}_{effectiveStudentId}:q{targetQ}";

        if (!forceRefresh && _memoryCache.TryGetValue(memKey, out List<SubjectGradeSummary>? cachedMarks) && cachedMarks != null)
        {
            _serviceProvider.GetService<ServerMetricsService>()?.RecordCacheHit();
            return cachedMarks;
        }

        var tokenData = await tokenStore.GetTokensAsync(userId);
        if (tokenData == null || string.IsNullOrEmpty(tokenData.AccessToken))
        {
            return new List<SubjectGradeSummary>();
        }

        string studentId = !string.IsNullOrEmpty(tokenData.StudentId) ? tokenData.StudentId : effectiveStudentId;
        string dbCacheKey = $"marks_db:{studentId}";

        List<GosuslugiMarkItem>? allMarks = null;

        // 1. Быстрая загрузка из постоянного кэша БД
        bool isDbCacheFresh = false;
        if (!forceRefresh)
        {
            var dbEntry = await dbContext.Cache.FirstOrDefaultAsync(c => c.CacheKey == dbCacheKey);
            if (dbEntry != null && !string.IsNullOrEmpty(dbEntry.JsonData))
            {
                try
                {
                    allMarks = JsonSerializer.Deserialize<List<GosuslugiMarkItem>>(dbEntry.JsonData, DiaryClient.DatamartJsonOptions);
                    // Считаем кэш свежим, если он обновлялся менее 5 минут назад
                    if (allMarks != null && allMarks.Count > 0 && (DateTime.UtcNow - dbEntry.UpdatedAt).TotalMinutes < 5)
                    {
                        isDbCacheFresh = true;
                    }
                }
                catch { }
            }
        }

        // 2. Если в БД нет данных, кэш старше 5 минут или запрошено принудительное обновление — идем в API Госуслуг
        if (!isDbCacheFresh || forceRefresh)
        {
            var httpClientFactory = scope.ServiceProvider.GetRequiredService<System.Net.Http.IHttpClientFactory>();
            var client = httpClientFactory.CreateClient("diary-proxy");
            var clientWithUser = new DiaryClient(client, tokenStore, userId);

            try
            {
                var apiMarks = await clientWithUser.GetMarksAsync();
                if (apiMarks != null)
                {
                    // Загружаем предыдущие оценки из БД для слияния (чтобы прошлые четверти не терялись при смещении 12-недельного окна)
                    var existingDb = await dbContext.Cache.FirstOrDefaultAsync(c => c.CacheKey == dbCacheKey);
                    var existingList = new List<GosuslugiMarkItem>();
                    if (existingDb != null && !string.IsNullOrEmpty(existingDb.JsonData))
                    {
                        try
                        {
                            existingList = JsonSerializer.Deserialize<List<GosuslugiMarkItem>>(existingDb.JsonData, DiaryClient.DatamartJsonOptions) ?? new();
                        }
                        catch { }
                    }

                    var mergedDict = new Dictionary<string, GosuslugiMarkItem>(StringComparer.OrdinalIgnoreCase);
                    foreach (var m in existingList)
                    {
                        string k = !string.IsNullOrEmpty(m.Id) ? m.Id : $"{m.SubjectName}_{m.Date}_{m.Value}_{m.WorkKind}";
                        mergedDict[k] = m;
                    }
                    foreach (var m in apiMarks)
                    {
                        string k = !string.IsNullOrEmpty(m.Id) ? m.Id : $"{m.SubjectName}_{m.Date}_{m.Value}_{m.WorkKind}";
                        mergedDict[k] = m;
                    }

                    allMarks = mergedDict.Values.OrderByDescending(m => m.Date).ToList();

                    string json = JsonSerializer.Serialize(allMarks, DiaryClient.DatamartJsonOptions);
                    if (existingDb != null)
                    {
                        existingDb.JsonData = json;
                        existingDb.UpdatedAt = DateTime.UtcNow;
                    }
                    else
                    {
                        dbContext.Cache.Add(new CachedData { CacheKey = dbCacheKey, JsonData = json, UpdatedAt = DateTime.UtcNow });
                    }
                    await dbContext.SaveChangesAsync();
                }
            }
            catch (DiarySessionExpiredException)
            {
                // Если есть сохраненные оценки в кэше БД — продолжаем работу на их основе, не выбрасывая ошибку
                if (allMarks == null || allMarks.Count == 0)
                {
                    throw;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DiaryInteractionService] ⚠️ Ошибка запроса оценок API: {ex.Message}");
            }
        }

        allMarks ??= new List<GosuslugiMarkItem>();

        // 3. Фильтрация по четвертям (если targetQ == 0, показываем за весь учебный год)
        var marksForQuarter = (targetQ > 0)
            ? allMarks.Where(m => GetMarkQuarter(m.Date) == targetQ).ToList()
            : allMarks;

        // Если в текущей четверти еще нет оценок, но в журнале есть оценки за другие периоды или без точной даты — используем allMarks
        if (marksForQuarter.Count == 0 && allMarks.Count > 0)
        {
            marksForQuarter = allMarks;
        }

        if (marksForQuarter.Count == 0)
        {
            var emptyList = new List<SubjectGradeSummary>();
            _memoryCache.Set(memKey, emptyList, new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromSeconds(10), AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(15), Size = 1 });
            return emptyList;
        }

        var grouped = marksForQuarter
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
                if (string.IsNullOrWhiteSpace(m.Value)) continue;

                var parts = m.Value.Split(new[] { '/', '\\', ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    if (int.TryParse(part.Trim(), out int gradeVal))
                    {
                        if (gradeVal is >= 1 and <= 5)
                        {
                            int w = (m.Weight.HasValue && m.Weight.Value > 0) ? m.Weight.Value : 1;
                            wSum += gradeVal * w;
                            totalW += w;
                        }
                        else if (gradeVal is >= 11 and <= 55)
                        {
                            // Decompose concatenated grades e.g. 54 -> 5 and 4
                            int d1 = gradeVal / 10;
                            int d2 = gradeVal % 10;
                            if (d1 is >= 1 and <= 5 && d2 is >= 1 and <= 5)
                            {
                                int w = (m.Weight.HasValue && m.Weight.Value > 0) ? m.Weight.Value : 1;
                                wSum += (d1 + d2) * w;
                                totalW += w * 2;
                            }
                        }
                    }
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

        _memoryCache.Set(memKey, result, new MemoryCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromMinutes(2),
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5),
            Size = 1
        });
        return result;
    }

    public async Task<List<SubjectGradeSummary>> GetSubjectGradesSummaryAsync(string userId, bool forceRefresh)
    {
        return await GetSubjectGradesSummaryAsync(userId, quarter: null, forceRefresh);
    }

    public async Task<string> GetMarksStringAsync(string userId, int? quarter = null, bool forceRefresh = false)
    {
        int currentQ = GetCurrentQuarter();
        int targetQ = quarter ?? currentQ;

        if (IsDiaryMaintenanceActive())
        {
            return "⚠️ *Электронный дневник временно недоступен*\n\nШлюз Госуслуг / ФГИС «Моя Школа» проводит регламентные работы. Новые данные загрузятся автоматически после завершения работ.";
        }

        string effectiveStudentId = userId;
        using (var scope = _serviceProvider.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await dbContext.Users.FindAsync(userId);
            effectiveStudentId = !string.IsNullOrEmpty(user?.SelectedStudentId) ? user.SelectedStudentId : (user?.StudentId ?? userId);
        }

        string marksStrKey = $"user_marks_str:{userId}_{effectiveStudentId}:q{targetQ}";
        try
        {
            if (!forceRefresh && _memoryCache.TryGetValue(marksStrKey, out string? cachedMarksStr) && !string.IsNullOrEmpty(cachedMarksStr))
            {
                _serviceProvider.GetService<ServerMetricsService>()?.RecordCacheHit();
                return cachedMarksStr;
            }

            var subjects = await GetSubjectGradesSummaryAsync(userId, targetQ, forceRefresh);
            string quarterTitle = (targetQ > 0) ? $"{targetQ}-я четверть" : "Все четверти";

            if (subjects != null && subjects.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"📊 *Ваши оценки и средний балл ({quarterTitle}):*");
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
                    var markStrings = s.Marks
                        .Where(m => !string.IsNullOrEmpty(m.Value))
                        .Select(m =>
                        {
                            int w = (m.Weight.HasValue && m.Weight.Value > 1) ? m.Weight.Value : 1;
                            string wSuffix = w > 1 ? $"×{w}" : "";
                            string val = TelegramMarkdownEscaper.Escape(m.Value);
                            return $"{val}{wSuffix}";
                        });

                    sb.AppendLine($"  └ _{string.Join("  ", markStrings)}_");
                    sb.AppendLine();

                    totalAllSum += s.WeightedSum;
                    totalAllWeight += s.TotalWeight;
                }

                if (totalAllWeight > 0)
                {
                    double overallGpa = Math.Round((double)totalAllSum / totalAllWeight, 2);
                    sb.AppendLine($"🏆 *Общий средний балл ({quarterTitle}):* `{overallGpa:F2}`");
                }

                string res = sb.ToString().TrimEnd();
                _memoryCache.Set(marksStrKey, res, new MemoryCacheEntryOptions
                {
                    SlidingExpiration = TimeSpan.FromMinutes(2),
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5),
                    Size = 1
                });
                return res;
            }

            string qText = (targetQ > 0) ? $"{targetQ}-ю четверть" : "выбранный период";
            return $"📊 Оценок в дневнике Госуслуг за {qText} пока нет.";
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

    public async Task<string> GetMarksStringAsync(string userId, bool forceRefresh)
    {
        return await GetMarksStringAsync(userId, quarter: null, forceRefresh);
    }

    public async Task<string> GetProfileStringAsync(string userId, bool forceRefresh = false)
    {
        string profileKey = $"user_profile:{userId}";
        if (!forceRefresh && _memoryCache.TryGetValue(profileKey, out string? cachedProfile) && !string.IsNullOrEmpty(cachedProfile))
        {
            _serviceProvider.GetService<ServerMetricsService>()?.RecordCacheHit();
            return cachedProfile;
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
        var client = httpClientFactory.CreateClient("diary-proxy");
        var clientWithUser = new DiaryClient(client, tokenStore, userId);

        // Auto-resolve school and class if not present in DB
        if (user != null && string.IsNullOrEmpty(user.ClassName))
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
                                        Console.WriteLine($"[DiaryInteractionService] 🔍 student_classes raw: {elem.GetRawText()}");
                                        var classInfo = JsonSerializer.Deserialize<GosuslugiClassInfo>(elem.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                                        if (classInfo != null && (!string.IsNullOrEmpty(classInfo.ShortName) || !string.IsNullOrEmpty(classInfo.ClassNum)))
                                        {
                                            string resolvedClass = !string.IsNullOrEmpty(classInfo.ShortName)
                                                ? classInfo.ShortName
                                                : $"{classInfo.ClassNum}{classInfo.ClassLetter}".Trim();
                                            user.ClassName = resolvedClass;

                                            string resolvedSchool = "";
                                            if (elem.TryGetProperty("school_name", out var sn) ||
                                                elem.TryGetProperty("schoolName", out sn) ||
                                                elem.TryGetProperty("org_name", out sn) ||
                                                elem.TryGetProperty("orgName", out sn) ||
                                                elem.TryGetProperty("organization", out sn) ||
                                                elem.TryGetProperty("organization_name", out sn))
                                            {
                                                resolvedSchool = sn.GetString()?.Trim() ?? "";
                                            }

                                            if (!string.IsNullOrEmpty(resolvedSchool) && !string.Equals(resolvedSchool, resolvedClass, StringComparison.OrdinalIgnoreCase))
                                            {
                                                user.OrgName = resolvedSchool;
                                            }

                                            await dbContext.SaveChangesAsync();
                                            break;
                                        }
                                    }
                                }
                                if (!string.IsNullOrEmpty(user.ClassName)) break;
                            }
                        }
                        if (!string.IsNullOrEmpty(user.ClassName)) break;
                    }
                    if (!string.IsNullOrEmpty(user.ClassName)) break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DiaryInteractionService] ❌ Ошибка auto-resolve student_classes: {ex.Message}");
            }
        }

        // Clean up legacy corrupted OrgName where school was set to the class name
        if (user != null && !string.IsNullOrEmpty(user.OrgName) && string.Equals(user.OrgName.Trim(), user.ClassName?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            user.OrgName = "";
            await dbContext.SaveChangesAsync();
        }

        string profileCard = "👤 *Профиль пользователя*\n\n";

        try
        {
            var students = await clientWithUser.GetStudentInfoAsync();
            if (students != null && students.Count > 0)
            {
                if (user?.Role == "Parent" || students.Count > 1 || !string.IsNullOrEmpty(user?.SelectedStudentId))
                {
                    var selectedChild = (!string.IsNullOrEmpty(user?.SelectedStudentId)
                        ? students.FirstOrDefault(c => c.StudentId == user.SelectedStudentId)
                        : null) ?? students[0];

                    string childName = $"{selectedChild.StudentLastName} {selectedChild.StudentFirstName}".Trim();
                    profileCard += "👨‍👧 *Родительский профиль*\n";
                    profileCard += $"👶 *Выбранный ребёнок*: `{TelegramMarkdownEscaper.Escape(childName)}`\n";
                    profileCard += $"🎓 *Статус ЕСИА*: {(selectedChild.Trusted ? "✅ Подтверждён" : "⚠️ Не подтверждён")}\n\n";
                }
                else
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
        }
        catch (Exception ex)
        {
            profileCard += $"⚠️ _Не удалось загрузить статус Госуслуг: {TelegramMarkdownEscaper.Escape(ex.Message)}_\n\n";
        }

        string displayOrg = user?.OrgName ?? "";
        if (string.Equals(displayOrg.Trim(), user?.ClassName?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            displayOrg = "";
        }

        string platformName = userId.StartsWith("max_") ? "MAX" : "Telegram";
        string roleDisplay = user?.Role == "Parent" ? "Родитель 👨‍👧" : (user?.Role ?? "Student");
        profileCard += "⚙️ *Данные в MirageDiary:*\n";
        profileCard += $"🎭 *Роль*: `{roleDisplay}`\n";
        if (!string.IsNullOrWhiteSpace(displayOrg))
        {
            profileCard += $"🏫 *Школа*: `{displayOrg}`\n";
        }
        profileCard += $"📚 *Класс*: `{(string.IsNullOrWhiteSpace(user?.ClassName) ? "Не указан" : user.ClassName)}`\n";
        profileCard += $"🔔 *Будильник*: {(user?.IsAlarmEnabled ?? true ? "🟢 Включен" : "🔴 Выключен")}\n" +
                       $"📱 *Платформа*: `{platformName}`\n";

        _memoryCache.Set(profileKey, profileCard, new MemoryCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromMinutes(5),
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30),
            Size = 1
        });
        return profileCard;
    }

    public async Task<List<GosuslugiStudentInfo>> GetChildrenListAsync(string userId, bool forceRefresh = false)
    {
        string childrenKey = $"user_children:{userId}";
        if (!forceRefresh && _memoryCache.TryGetValue(childrenKey, out List<GosuslugiStudentInfo>? cached) && cached != null)
        {
            _serviceProvider.GetService<ServerMetricsService>()?.RecordCacheHit();
            return cached;
        }

        using var scope = _serviceProvider.CreateScope();
        var tokenStore = scope.ServiceProvider.GetRequiredService<ITokenStore>();

        var httpClientFactory = scope.ServiceProvider.GetRequiredService<System.Net.Http.IHttpClientFactory>();
        var client = httpClientFactory.CreateClient("diary-proxy");
        var clientWithUser = new DiaryClient(client, tokenStore, userId);

        try
        {
            var res = await clientWithUser.GetStudentInfoAsync();
            if (res != null)
            {
                _memoryCache.Set(childrenKey, res, new MemoryCacheEntryOptions
                {
                    SlidingExpiration = TimeSpan.FromMinutes(5),
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30),
                    Size = 1
                });
            }
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
        user.StudentId = studentId;
        user.Role = "Parent";

        string childMetaKey = $"child_meta:{studentId}";
        bool hasMeta = false;

        if (_memoryCache.TryGetValue(childMetaKey, out (string ClassName, string OrgName) memMeta))
        {
            user.ClassName = memMeta.ClassName;
            user.OrgName = memMeta.OrgName;
            hasMeta = true;
        }
        else
        {
            var dbCached = await dbContext.Cache.FirstOrDefaultAsync(c => c.CacheKey == childMetaKey);
            if (dbCached != null && !string.IsNullOrEmpty(dbCached.JsonData))
            {
                try
                {
                    var meta = JsonSerializer.Deserialize<Dictionary<string, string>>(dbCached.JsonData);
                    if (meta != null)
                    {
                        user.ClassName = meta.GetValueOrDefault("className", "");
                        user.OrgName = meta.GetValueOrDefault("orgName", "");
                        _memoryCache.Set(childMetaKey, (user.ClassName, user.OrgName), TimeSpan.FromDays(30));
                        hasMeta = true;
                    }
                }
                catch { }
            }
        }

        if (!hasMeta)
        {
            user.OrgName = "";
            user.ClassName = "";
        }

        await dbContext.SaveChangesAsync();

        var tokenData = await tokenStore.GetTokensAsync(userId);
        if (tokenData != null)
        {
            tokenData.StudentId = studentId;
            await tokenStore.SaveTokensAsync(userId, tokenData);
        }

        // Only query Datamart over the internet if class info is not already known and cached
        if (!hasMeta)
        {
            var httpClientFactory = scope.ServiceProvider.GetRequiredService<System.Net.Http.IHttpClientFactory>();
            var client = httpClientFactory.CreateClient("diary-proxy");
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
                                        string resolvedClass = !string.IsNullOrEmpty(classInfo.ShortName)
                                            ? classInfo.ShortName
                                            : $"{classInfo.ClassNum}{classInfo.ClassLetter}".Trim();
                                        user.ClassName = resolvedClass;

                                        string resolvedSchool = "";
                                        if (elem.TryGetProperty("school_name", out var sn) ||
                                            elem.TryGetProperty("schoolName", out sn) ||
                                            elem.TryGetProperty("org_name", out sn) ||
                                            elem.TryGetProperty("orgName", out sn) ||
                                            elem.TryGetProperty("organization", out sn) ||
                                            elem.TryGetProperty("organization_name", out sn))
                                        {
                                            resolvedSchool = sn.GetString()?.Trim() ?? "";
                                        }

                                        if (!string.IsNullOrEmpty(resolvedSchool) && !string.Equals(resolvedSchool, resolvedClass, StringComparison.OrdinalIgnoreCase))
                                        {
                                            user.OrgName = resolvedSchool;
                                        }
                                        else if (string.Equals(user.OrgName, user.ClassName, StringComparison.OrdinalIgnoreCase))
                                        {
                                            user.OrgName = "";
                                        }

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

            // Cache the result (even if empty, so we don't spam Datamart on repeat clicks)
            var metaDict = new Dictionary<string, string>
            {
                ["className"] = user.ClassName ?? "",
                ["orgName"] = user.OrgName ?? ""
            };
            string metaJson = JsonSerializer.Serialize(metaDict);
            var existingCache = await dbContext.Cache.FirstOrDefaultAsync(c => c.CacheKey == childMetaKey);
            if (existingCache != null)
            {
                existingCache.JsonData = metaJson;
                existingCache.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                dbContext.Cache.Add(new CachedData { CacheKey = childMetaKey, JsonData = metaJson, UpdatedAt = DateTime.UtcNow });
            }
            await dbContext.SaveChangesAsync();
            _memoryCache.Set(childMetaKey, (user.ClassName ?? "", user.OrgName ?? ""), TimeSpan.FromDays(30));
        }

        // Invalidate cached views for this user
        _memoryCache.Remove($"user_profile:{userId}");
        _memoryCache.Remove($"user_marks:{userId}");
        _memoryCache.Remove($"user_children:{userId}");
        for (int q = 0; q <= 4; q++)
        {
            _memoryCache.Remove($"user_marks:{userId}:q{q}");
            _memoryCache.Remove($"user_marks_str:{userId}:q{q}");
            _memoryCache.Remove($"user_marks:{userId}_{studentId}:q{q}");
            _memoryCache.Remove($"user_marks_str:{userId}_{studentId}:q{q}");
        }

        try
        {
            var userSync = scope.ServiceProvider.GetService<UserSyncService>();
            if (userSync != null)
            {
                await userSync.SyncUserAcrossPlatformsAsync(user);
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
