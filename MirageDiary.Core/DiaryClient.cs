using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MirageDiary.Core.Models;

namespace MirageDiary.Core;

public class DiaryClient
{
    private readonly HttpClient _client;
    private readonly ITokenStore _tokenStore;
    private readonly string _userId;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Expiry, List<GosuslugiStudentInfo> Students)> _studentInfoCache = new();

    public static void InvalidateStudentCache(string userId)
    {
        _studentInfoCache.TryRemove(userId, out _);
    }

    private const string GosuslugiBaseUrl = "https://www.gosuslugi.ru/api/myschool/";

    public static readonly JsonSerializerOptions DatamartJsonOptions = CreateDatamartJsonOptions();

    private static JsonSerializerOptions CreateDatamartJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        options.Converters.Add(new FlexibleBoolConverter());
        options.Converters.Add(new FlexibleStringConverter());
        options.Converters.Add(new FlexibleIntConverter());
        return options;
    }

    public bool IsBackground { get; set; } = false;

    public DiaryClient(HttpClient client, ITokenStore tokenStore, string userId, bool isBackground = false)
    {
        _client = client;
        _tokenStore = tokenStore;
        _userId = userId;
        IsBackground = isBackground;
    }

    public static DateTime? GetTokenExpirationUtc(string rawToken)
    {
        if (string.IsNullOrEmpty(rawToken)) return null;

        string jwt = rawToken;
        if (rawToken.Contains("acc_t="))
        {
            var match = System.Text.RegularExpressions.Regex.Match(rawToken, @"acc_t=([^;]+)");
            if (match.Success)
            {
                jwt = match.Groups[1].Value.Trim();
            }
        }

        var parts = jwt.Split('.');
        if (parts.Length >= 2)
        {
            try
            {
                string payloadB64 = parts[1].Replace('-', '+').Replace('_', '/');
                payloadB64 += new string('=', (4 - payloadB64.Length % 4) % 4);
                var jsonBytes = Convert.FromBase64String(payloadB64);
                using var doc = System.Text.Json.JsonDocument.Parse(jsonBytes);
                if (doc.RootElement.TryGetProperty("exp", out var expProp))
                {
                    long expSeconds = expProp.GetInt64();
                    return DateTimeOffset.FromUnixTimeSeconds(expSeconds).UtcDateTime;
                }
            }
            catch { }
        }
        return null;
    }

    public async Task<bool> TryAutoRenewSessionAsync()
    {
        try
        {
            var tokenData = await _tokenStore.GetTokensAsync(_userId);
            if (tokenData == null) return false;

            // 1. Mobile Session Renewal (50ms via /createSession)
            if (!string.IsNullOrEmpty(tokenData.DeviceId) && !string.IsNullOrEmpty(tokenData.Oid) && !string.IsNullOrEmpty(tokenData.AccessToken))
            {
                Console.WriteLine($"[DiaryClient] 🔄 Бесшумное продление мобильной сессии для user={_userId}...");
                using var httpClient = new HttpClient();
                var mobileClient = new MobileEsiaClient(httpClient);
                string entToken = MobileEsiaClient.BuildEntToken(tokenData.DeviceId);
                var renewResult = await mobileClient.CreateSessionAsync(tokenData.DeviceId, tokenData.Oid, tokenData.AccessToken, entToken);
                if (renewResult != null && !string.IsNullOrEmpty(renewResult.AccessToken))
                {
                    tokenData.AccessToken = renewResult.AccessToken;
                    tokenData.SessionId = renewResult.SessionId;
                    await _tokenStore.SaveTokensAsync(_userId, tokenData);
                    Console.WriteLine($"[DiaryClient] 🚀 Мобильная сессия мгновенно продлена для user={_userId}!");
                    await EnsureAuthorizationAsync(forceReconfigure: true);
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DiaryClient] ⚠️ Ошибка при вызове TryAutoRenewSessionAsync: {ex.Message}");
        }

        return false;
    }

    private async Task EnsureAuthorizationAsync(bool forceReconfigure = false)
    {
        string defaultUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
        
        _client.DefaultRequestHeaders.Remove("User-Agent");
        _client.DefaultRequestHeaders.Add("User-Agent", defaultUa);

        if (!_client.DefaultRequestHeaders.Contains("Accept"))
        {
            _client.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
        }
        if (!_client.DefaultRequestHeaders.Contains("Accept-Language"))
        {
            _client.DefaultRequestHeaders.Add("Accept-Language", "ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7");
        }
        if (!_client.DefaultRequestHeaders.Contains("Referer"))
        {
            _client.DefaultRequestHeaders.Add("Referer", "https://www.gosuslugi.ru/school");
        }
        if (!_client.DefaultRequestHeaders.Contains("sec-ch-ua"))
        {
            _client.DefaultRequestHeaders.Add("sec-ch-ua", "\"Not_A Brand\";v=\"8\", \"Chromium\";v=\"120\", \"Google Chrome\";v=\"120\"");
        }
        if (!_client.DefaultRequestHeaders.Contains("sec-ch-ua-mobile"))
        {
            _client.DefaultRequestHeaders.Add("sec-ch-ua-mobile", "?0");
        }
        if (!_client.DefaultRequestHeaders.Contains("sec-ch-ua-platform"))
        {
            _client.DefaultRequestHeaders.Add("sec-ch-ua-platform", "\"Windows\"");
        }
        if (!_client.DefaultRequestHeaders.Contains("sec-fetch-dest"))
        {
            _client.DefaultRequestHeaders.Add("sec-fetch-dest", "empty");
        }
        if (!_client.DefaultRequestHeaders.Contains("sec-fetch-mode"))
        {
            _client.DefaultRequestHeaders.Add("sec-fetch-mode", "cors");
        }
        if (!_client.DefaultRequestHeaders.Contains("sec-fetch-site"))
        {
            _client.DefaultRequestHeaders.Add("sec-fetch-site", "same-origin");
        }

        var tokenData = await _tokenStore.GetTokensAsync(_userId);
        if (tokenData != null && !string.IsNullOrEmpty(tokenData.AccessToken))
        {
            // Check if token expires soon (within 20 minutes) and renew proactively
            if (!forceReconfigure && !string.IsNullOrEmpty(tokenData.DeviceId) && !string.IsNullOrEmpty(tokenData.Oid))
            {
                var expUtc = GetTokenExpirationUtc(tokenData.AccessToken);
                if (expUtc != null && (expUtc.Value - DateTime.UtcNow) < TimeSpan.FromMinutes(20))
                {
                    Console.WriteLine($"[DiaryClient] ⏳ Токен user={_userId} истекает через {(expUtc.Value - DateTime.UtcNow).TotalMinutes:F1} мин. Продлеваем досрочно...");
                    if (await TryAutoRenewSessionAsync())
                    {
                        return;
                    }
                }
            }

            string token = CleanToken(tokenData.AccessToken);
            _client.DefaultRequestHeaders.Remove("Cookie");
            _client.DefaultRequestHeaders.Remove("Authorization");

            // 1. Mobile App Authentication (Bearer + Mobile Cookies)
            if (!string.IsNullOrEmpty(tokenData.SessionId) && !string.IsNullOrEmpty(tokenData.Oid))
            {
                _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                _client.DefaultRequestHeaders.Add("Cookie", $"u={tokenData.Oid}; sp_session={tokenData.SessionId}; acc_t={token}");
                Console.WriteLine($"[DiaryClient] 📱 Мобильная авторизация user={_userId}: Bearer + Cookie (u={tokenData.Oid}, sp_session={tokenData.SessionId.Substring(0, Math.Min(8, tokenData.SessionId.Length))}...)");
            }
            // 2. Legacy / Web Cookies Authentication
            else if (token.Contains("acc_t=") || token.Contains("rt=") || token.Contains("=") || token.Contains(";"))
            {
                var cookieParts = token.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var part in cookieParts)
                {
                    int eqIdx = part.IndexOf('=');
                    if (eqIdx > 0)
                    {
                        string name = part.Substring(0, eqIdx).Trim();
                        string val = part.Substring(eqIdx + 1).Trim();
                        if (!string.IsNullOrEmpty(val))
                        {
                            dict[name] = val;
                        }
                    }
                }

                string deduplicatedCookies = string.Join("; ", dict.Select(kvp => $"{kvp.Key}={kvp.Value}"));
                if (!string.IsNullOrEmpty(deduplicatedCookies))
                {
                    _client.DefaultRequestHeaders.Add("Cookie", deduplicatedCookies);
                }

                bool hasAccT = dict.ContainsKey("acc_t") || dict.ContainsKey("acc_token");
                Console.WriteLine($"[DiaryClient] 🔑 Настроена куки-авторизация user={_userId}: {dict.Count} куки (acc_t={hasAccT})");
            }
            else
            {
                _client.DefaultRequestHeaders.Add("Cookie", $"acc_t={token}");
                Console.WriteLine($"[DiaryClient] 🔑 Настроена куки-авторизация user={_userId}: Cookie acc_t (длина: {token.Length})");
            }
        }
        else
        {
            Console.WriteLine($"[DiaryClient] ⚠️ У пользователя {_userId} отсутствует сохраненный токен доступа!");
        }
    }

    // =========================================================================
    // GOSUSLUGI MY SCHOOL API METHODS
    // =========================================================================

    public async Task<List<GosuslugiStudentInfo>> GetStudentInfoAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && _studentInfoCache.TryGetValue(_userId, out var cached) && cached.Expiry > DateTime.UtcNow)
        {
            return cached.Students;
        }

        await EnsureAuthorizationAsync();
        
        string[] urls = new[]
        {
            GosuslugiBaseUrl + "v2/auth/student?role=parent",
            GosuslugiBaseUrl + "v2/auth/student?role=student"
        };

        bool encountered403 = false;
        var tokenData = await _tokenStore.GetTokensAsync(_userId);
        string cookieHeader = tokenData?.AccessToken ?? "";

        foreach (var url in urls)
        {
            await AcquireGosuslugiRateLimitAsync(IsBackground);
            try
            {
                Console.WriteLine($"[DiaryClient] 🔍 Запрос профиля у Госуслуг: GET {url} (user={_userId})");
                var response = await _client.GetAsync(url);
                Console.WriteLine($"[DiaryClient] 📊 Ответ HttpClient {url}: Status={(int)response.StatusCode} ({response.StatusCode})");

                string json = "";

                if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                {
                    Console.WriteLine($"[DiaryClient] 🛡️ WAF 403 на HttpClient. Запускаем Playwright Chromium API Fetcher для {url}...");
                    var (pwStatus, pwText) = await PlaywrightHttpFetcher.FetchAsync(url, "GET", cookieHeader);
                    Console.WriteLine($"[DiaryClient] 📊 Ответ Playwright API Fetcher {url}: Status={pwStatus}");
                    
                    if (pwStatus == 200 && !string.IsNullOrEmpty(pwText))
                    {
                        json = pwText;
                    }
                    else
                    {
                        encountered403 = true;
                        continue;
                    }
                }
                else if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    Console.WriteLine($"[DiaryClient] ℹ️ {url} вернул 404 Not Found (профиль/дети не найдены для данной роли). Проверяем следующую роль...");
                    continue;
                }
                else
                {
                    CheckResponseStatus(response);
                    json = await response.Content.ReadAsStringAsync();
                }

                // Log payload size only - do not log student personal data (PII) to server logs
                Console.WriteLine($"[DiaryClient] 📥 Получен ответ от {url} ({json.Length} байт)");

                var list = JsonSerializer.Deserialize<List<GosuslugiStudentInfo>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (list != null && list.Count > 0)
                {
                    using (var doc = JsonDocument.Parse(json))
                    {
                        foreach (var elem in doc.RootElement.EnumerateArray())
                        {
                            var props = elem.EnumerateObject().Select(p => $"{p.Name}={(p.Name.Contains("name", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("birth", StringComparison.OrdinalIgnoreCase) ? "***" : p.Value.ToString())}");
                            Console.WriteLine($"[DiaryClient] 👤 Full profile for user={_userId}: {string.Join(", ", props)}");
                        }
                    }
                    bool isParentUrl = url.Contains("role=parent", StringComparison.OrdinalIgnoreCase);
                    foreach (var item in list)
                    {
                        Console.WriteLine($"[DiaryClient] 👤 Parsed profile item for user={_userId}: sid={item.StudentId}, mdmId={item.MdmId}, oid={item.StudentOid}, region={item.StudentRegion}, regActive={item.StudentRegionActive}, trusted={item.Trusted}, permMark={item.PermissionMark}");
                        if (isParentUrl || item.KidId != null)
                        {
                            item.IsParentChild = true;
                        }
                    }

                    _studentInfoCache[_userId] = (DateTime.UtcNow.AddMinutes(30), list);
                    Console.WriteLine($"[DiaryClient] ✅ Успешно получен профиль ({list.Count} чел., isParent={isParentUrl}) для user={_userId} с {url}");
                    return list;
                }
                else
                {
                    Console.WriteLine($"[DiaryClient] ℹ️ Ответ от {url} вернул пустой список пользователей.");
                }
            }
            catch (DiarySessionExpiredException ex) 
            { 
                InvalidateStudentCache(_userId);
                Console.WriteLine($"[DiaryClient] ❌ DiarySessionExpiredException на {url}: {ex.Message}");
                throw; 
            }
            catch (DiaryMaintenanceException ex) 
            { 
                Console.WriteLine($"[DiaryClient] 🚧 DiaryMaintenanceException на {url}: {ex.Message}");
                throw; 
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DiaryClient] ⚠️ Исключение при вызове {url}: {ex.Message}");
            }
            finally
            {
                ReleaseGosuslugiRateLimit(IsBackground);
            }
        }

        if (encountered403)
        {
            Console.WriteLine($"[DiaryClient] ⚠️ Все роли вернули 403/401 для user={_userId}. Пробуем авто-продление сессии...");
            if (await TryAutoRenewSessionAsync())
            {
                Console.WriteLine($"[DiaryClient] 🔄 Повторный запрос профиля после успешного продления сессии для user={_userId}...");
                foreach (var url in urls)
                {
                    await AcquireGosuslugiRateLimitAsync(IsBackground);
                    try
                    {
                        var response = await _client.GetAsync(url);
                        if (response.IsSuccessStatusCode)
                        {
                            string json = await response.Content.ReadAsStringAsync();
                            var list = JsonSerializer.Deserialize<List<GosuslugiStudentInfo>>(json, new JsonSerializerOptions
                            {
                                PropertyNameCaseInsensitive = true
                            });
                            if (list != null && list.Count > 0)
                            {
                                bool isParentUrl = url.Contains("role=parent", StringComparison.OrdinalIgnoreCase);
                                foreach (var item in list)
                                {
                                    if (isParentUrl || item.KidId != null)
                                    {
                                        item.IsParentChild = true;
                                    }
                                }
                                Console.WriteLine($"[DiaryClient] ✅ Успешно восстановлен профиль для user={_userId}!");
                                return list;
                            }
                        }
                    }
                    catch { }
                    finally
                    {
                        ReleaseGosuslugiRateLimit(IsBackground);
                    }
                }
            }

            Console.WriteLine($"[DiaryClient] ⛔ Все роли вернули 403/401. Сессия пользователя {_userId} недействительна!");
            throw new DiarySessionExpiredException("Доступ к дневнику ограничен (403 Forbidden). Убедитесь, что ваша учетная запись Госуслуг подтверждена и в сервисе «Моя Школа» (gosuslugi.ru/school) привязан дневник, либо выполните вход под аккаунтом родителя.");
        }

        return new List<GosuslugiStudentInfo>();
    }

    public async Task<List<DatamartResponseItem>> GetDatamartAsync(string studentId, string intervalId, List<string> objectTypes)
    {
        return await GetDatamartBatchAsync(studentId, new List<string> { intervalId }, objectTypes);
    }

    public async Task<List<DatamartResponseItem>> GetDatamartBatchAsync(string studentId, List<string> intervalIds, List<string> objectTypes)
    {
        if (string.IsNullOrWhiteSpace(studentId) || !Guid.TryParse(studentId, out var parsedGuid))
        {
            Console.WriteLine($"[DiaryClient] ⚠️ Datamart studentId '{studentId}' is not a valid UUID. Skipping Datamart request to prevent HTTP 400.");
            return new List<DatamartResponseItem>();
        }
        studentId = parsedGuid.ToString("D");

        await EnsureAuthorizationAsync();
        string url = GosuslugiBaseUrl + "v1/datamart";

        var tokenData = await _tokenStore.GetTokensAsync(_userId);
        string cookieHeader = tokenData?.AccessToken ?? "";

        var requestItems = new List<DatamartRequestItem>();
        foreach (var intervalId in intervalIds)
        {
            foreach (var objType in objectTypes)
            {
                requestItems.Add(new DatamartRequestItem
                {
                    StudentId = studentId,
                    ObjType = objType,
                    IntervalId = intervalId
                });
            }
        }

        string jsonBody = JsonSerializer.Serialize(requestItems);

        for (int retry = 0; retry < 4; retry++)
        {
            await AcquireGosuslugiRateLimitAsync(IsBackground);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
                };

                var response = await _client.SendAsync(req);
                string jsonResponse = "";

                if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                {
                    if (retry == 0 && await TryAutoRenewSessionAsync())
                    {
                        Console.WriteLine($"[DiaryClient] 🔄 Продлена сессия при запросе Datamart для user={_userId}. Повторяем запрос...");
                        continue;
                    }

                    Console.WriteLine($"[DiaryClient] 🛡️ Datamart WAF 403. Вызываем Playwright API Fetcher для POST datamart...");
                    var (pwStatus, pwText) = await PlaywrightHttpFetcher.FetchAsync(url, "POST", cookieHeader, jsonBody);
                    if (pwStatus == 200 && !string.IsNullOrEmpty(pwText))
                    {
                        jsonResponse = pwText;
                    }
                    else
                    {
                        string checkErr = pwText;
                        if (string.IsNullOrEmpty(checkErr))
                        {
                            try { checkErr = await response.Content.ReadAsStringAsync(); } catch { }
                        }
                        if (!string.IsNullOrEmpty(checkErr) && checkErr.Contains("согласие", StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine($"[DiaryClient] ⚠️ Datamart 403 (Отсутствует согласие для studentId {studentId}): {checkErr}");
                            return new List<DatamartResponseItem>();
                        }

                        CheckResponseStatus(response);
                    }
                }
                else
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        var errBody = await response.Content.ReadAsStringAsync();
                        Console.WriteLine($"[DiaryClient] ❌ Datamart HTTP {(int)response.StatusCode}: {errBody} | Request: {jsonBody}");
                        if (errBody.Contains("согласие", StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine($"[DiaryClient] ⚠️ Datamart (Отсутствует согласие для studentId {studentId}): {errBody}");
                            return new List<DatamartResponseItem>();
                        }
                        if (response.StatusCode == HttpStatusCode.BadRequest || response.StatusCode == HttpStatusCode.NotFound)
                        {
                            return new List<DatamartResponseItem>();
                        }
                    }
                    CheckResponseStatus(response);
                    jsonResponse = await response.Content.ReadAsStringAsync();
                }

                var parsed = JsonSerializer.Deserialize<List<DatamartResponseItem>>(jsonResponse, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? new List<DatamartResponseItem>();

                bool hasData = parsed.Any(p => p.Data != null && p.Data.Count > 0);
                bool isPending = response.StatusCode == HttpStatusCode.Accepted ||
                    jsonResponse.Trim() == "[]" ||
                    parsed.Any(p =>
                        string.Equals(p.DataStatus, "pending", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(p.DataStatus, "processing", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(p.DataStatus, "in_progress", StringComparison.OrdinalIgnoreCase));

                // If we have data or if the status is not pending (e.g. ready/snapshot or empty dataset), return immediately.
                if (hasData || !isPending)
                {
                    if (!hasData)
                    {
                        string details = string.Join(", ", parsed.Select(p => $"{p.IntervalId}:{p.DataStatus}:c={p.Data?.Count ?? 0}"));
                        Console.WriteLine($"[DiaryClient] ⚠️ Datamart hasData=false for user={_userId}, sid={studentId}, items={parsed.Count}. Details: {details}");
                    }
                    return parsed;
                }

                if (retry < 3)
                {
                    await Task.Delay(1500);
                }
                else
                {
                    return parsed;
                }
            }
            finally
            {
                ReleaseGosuslugiRateLimit(IsBackground);
            }
        }

        return new List<DatamartResponseItem>();
    }

    public async Task<(string StudentId, string MdmId)> ResolveStudentIdsAsync(string? targetStudentId = null)
    {
        var tokenData = await _tokenStore.GetTokensAsync(_userId);
        string studentId = targetStudentId ?? tokenData?.StudentId ?? "";
        string mdmId = "";

        try
        {
            var students = await GetStudentInfoAsync();
            if (students != null && students.Count > 0)
            {
                GosuslugiStudentInfo? student = null;
                if (!string.IsNullOrEmpty(targetStudentId))
                {
                    student = students.FirstOrDefault(s => s.StudentId == targetStudentId || s.MdmId == targetStudentId);
                }

                if (student == null && !string.IsNullOrEmpty(studentId))
                {
                    student = students.FirstOrDefault(s => s.StudentId == studentId || s.MdmId == studentId);
                    if (student != null && !student.PermissionMark)
                    {
                        var active = students.FirstOrDefault(s => s.PermissionMark);
                        if (active != null) student = active;
                    }
                }

                if (student == null)
                {
                    student = students.FirstOrDefault(s => s.PermissionMark) ?? students[0];
                }

                studentId = !string.IsNullOrEmpty(student.StudentId) ? student.StudentId : studentId;
                mdmId = student.MdmId ?? "";
                
                if (tokenData != null && string.IsNullOrEmpty(targetStudentId) && (tokenData.StudentId != studentId))
                {
                    tokenData.StudentId = studentId;
                    await _tokenStore.SaveTokensAsync(_userId, tokenData);
                }
            }
        }
        catch (DiarySessionExpiredException)
        {
            throw;
        }
        catch { }

        return (studentId, mdmId);
    }

    // =========================================================================
    // HOMEWORK, SCHEDULE, MARKS
    // =========================================================================

    public async Task<List<HomeworkItem>> GetHomeworkAsync(DateTime date)
    {
        return await GetHomeworkAsync(date.ToString("yyyy-MM-dd"));
    }

    public async Task<List<HomeworkItem>> GetHomeworkAsync(string date, string? targetStudentId = null)
    {
        await EnsureAuthorizationAsync();

        try
        {
            var (studentId, mdmId) = await ResolveStudentIdsAsync(targetStudentId);
            List<string> intervals = GetIntervalsForDate(date);

            List<string> idsToTry = new List<string>();
            if (!string.IsNullOrEmpty(studentId) && Guid.TryParse(studentId, out var sGuid)) idsToTry.Add(sGuid.ToString("D"));
            if (!string.IsNullOrEmpty(mdmId) && Guid.TryParse(mdmId, out var mGuid))
            {
                string mStr = mGuid.ToString("D");
                if (!idsToTry.Contains(mStr)) idsToTry.Add(mStr);
            }

            if (idsToTry.Count == 0)
            {
                return new List<HomeworkItem>();
            }

            var result = new List<HomeworkItem>();

            foreach (var sid in idsToTry)
            {
                var datamart = await GetDatamartBatchAsync(sid, intervals, new List<string> { "student_homeworks_materials" });
                foreach (var item in datamart)
                {
                    if (item.Data != null && item.ObjType == "student_homeworks_materials")
                    {
                        foreach (var elem in item.Data)
                        {
                            try
                            {
                                var hw = JsonSerializer.Deserialize<GosuslugiHomeworkItem>(elem.GetRawText(), DatamartJsonOptions);
                                if (hw != null)
                                {
                                    string title = !string.IsNullOrWhiteSpace(hw.SubjectName) ? hw.SubjectName :
                                                   !string.IsNullOrWhiteSpace(hw.Subject) ? hw.Subject : "Предмет";

                                    string desc = !string.IsNullOrWhiteSpace(hw.Description) ? hw.Description :
                                                  !string.IsNullOrWhiteSpace(hw.Task) ? hw.Task :
                                                  !string.IsNullOrWhiteSpace(hw.Homework) ? hw.Homework :
                                                  !string.IsNullOrWhiteSpace(hw.Text) ? hw.Text :
                                                  !string.IsNullOrWhiteSpace(hw.Content) ? hw.Content : "";

                                    string hwDateRaw = !string.IsNullOrWhiteSpace(hw.PlanReadyDate) ? hw.PlanReadyDate :
                                                       !string.IsNullOrWhiteSpace(hw.Date) ? hw.Date :
                                                       !string.IsNullOrWhiteSpace(hw.IssueDate) ? hw.IssueDate :
                                                       !string.IsNullOrWhiteSpace(hw.ExecutionDate) ? hw.ExecutionDate :
                                                       !string.IsNullOrWhiteSpace(hw.DateAssigned) ? hw.DateAssigned : "";

                                    bool isDateMatch = string.IsNullOrEmpty(hwDateRaw);
                                    if (!isDateMatch && DateTime.TryParse(date, out var targetDt))
                                    {
                                        if (DateTime.TryParse(hwDateRaw, out var hwDt))
                                        {
                                            isDateMatch = (hwDt.Date == targetDt.Date);
                                        }
                                        else
                                        {
                                            isDateMatch = hwDateRaw.StartsWith(date) || hwDateRaw.Contains(date);
                                        }
                                    }

                                    if (isDateMatch && !string.IsNullOrWhiteSpace(desc))
                                    {
                                        if (!result.Exists(r => r.Description == desc && r.LessonName == title))
                                        {
                                            result.Add(new HomeworkItem
                                            {
                                                Id = hw.Id,
                                                LessonName = title,
                                                Description = desc,
                                                Date = hwDateRaw
                                            });
                                        }
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }

                if (result.Count == 0)
                {
                    var customHw = await GetCustomServicesHomeworkAsync(sid, intervals);
                    foreach (var hw in customHw)
                    {
                        bool isDateMatch = string.IsNullOrEmpty(hw.Date);
                        if (!isDateMatch && DateTime.TryParse(date, out var targetDt))
                        {
                            if (DateTime.TryParse(hw.Date, out var hwDt))
                            {
                                isDateMatch = (hwDt.Date == targetDt.Date);
                            }
                            else
                            {
                                isDateMatch = hw.Date.StartsWith(date) || hw.Date.Contains(date);
                            }
                        }

                        if (isDateMatch && !string.IsNullOrWhiteSpace(hw.Description))
                        {
                            if (!result.Exists(r => r.Description == hw.Description && r.LessonName == hw.LessonName))
                            {
                                result.Add(hw);
                            }
                        }
                    }
                }

                if (result.Count > 0) return result;
            }
        }
        catch (DiarySessionExpiredException)
        {
            throw;
        }
        catch (DiaryMaintenanceException)
        {
            throw;
        }
        catch (Exception)
        {
        }

        return new List<HomeworkItem>();
    }

    public async Task<List<HomeworkItem>> GetCustomServicesHomeworkAsync(string studentId, List<string> intervals)
    {
        await EnsureAuthorizationAsync();
        var result = new List<HomeworkItem>();

        try
        {
            string queryIntervals = string.Join("&", intervals.Select(i => $"intervalId={i}"));
            string url = $"{GosuslugiBaseUrl}v2/customservices/{studentId}?object=homework&{queryIntervals}";

            var response = await _client.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("homeworks", out var hwArray) && hwArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var elem in hwArray.EnumerateArray())
                    {
                        try
                        {
                            var hw = JsonSerializer.Deserialize<GosuslugiHomeworkItem>(elem.GetRawText(), DatamartJsonOptions);
                            if (hw != null)
                            {
                                string title = !string.IsNullOrWhiteSpace(hw.SubjectName) ? hw.SubjectName :
                                               !string.IsNullOrWhiteSpace(hw.Subject) ? hw.Subject : "Предмет";

                                string desc = !string.IsNullOrWhiteSpace(hw.Description) ? hw.Description :
                                              !string.IsNullOrWhiteSpace(hw.Task) ? hw.Task :
                                              !string.IsNullOrWhiteSpace(hw.Homework) ? hw.Homework :
                                              !string.IsNullOrWhiteSpace(hw.Text) ? hw.Text :
                                              !string.IsNullOrWhiteSpace(hw.Content) ? hw.Content : "";

                                string hwDateRaw = !string.IsNullOrWhiteSpace(hw.Date) ? hw.Date :
                                                   !string.IsNullOrWhiteSpace(hw.ExecutionDate) ? hw.ExecutionDate :
                                                   !string.IsNullOrWhiteSpace(hw.DateAssigned) ? hw.DateAssigned : "";

                                if (!string.IsNullOrWhiteSpace(desc))
                                {
                                    result.Add(new HomeworkItem
                                    {
                                        Id = hw.Id,
                                        LessonName = title,
                                        Description = desc,
                                        Date = hwDateRaw
                                    });
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
        }
        catch { }

        return result;
    }

    public async Task<List<GosuslugiLessonItem>> GetScheduleAsync(string date, string? targetStudentId = null)
    {
        await EnsureAuthorizationAsync();

        try
        {
            var (studentId, mdmId) = await ResolveStudentIdsAsync(targetStudentId);
            var intervals = GetIntervalsForDate(date);

            List<string> idsToTry = new List<string>();
            if (!string.IsNullOrEmpty(studentId) && Guid.TryParse(studentId, out var sGuid)) idsToTry.Add(sGuid.ToString("D"));
            if (!string.IsNullOrEmpty(mdmId) && Guid.TryParse(mdmId, out var mGuid))
            {
                string mStr = mGuid.ToString("D");
                if (!idsToTry.Contains(mStr)) idsToTry.Add(mStr);
            }

            if (idsToTry.Count == 0)
            {
                return new List<GosuslugiLessonItem>();
            }

            var lessons = new List<GosuslugiLessonItem>();

            foreach (var sid in idsToTry)
            {
                var datamart = await GetDatamartBatchAsync(sid, intervals, new List<string> { "student_lessons" });
                Console.WriteLine($"[DiaryClient] GetScheduleAsync: targetDate={date}, sid={sid}, intervals=[{string.Join(',', intervals)}], datamart items={datamart.Count}");
                foreach (var item in datamart)
                {
                    if (item.ObjType == "student_lessons" && item.Data != null)
                    {
                        Console.WriteLine($"[DiaryClient] Datamart student_lessons count={item.Data.Count}");
                        foreach (var elem in item.Data)
                        {
                            try
                            {
                                var lesson = JsonSerializer.Deserialize<GosuslugiLessonItem>(elem.GetRawText(), DatamartJsonOptions);
                                if (lesson != null)
                                {
                                    string lDate = !string.IsNullOrEmpty(lesson.Date) ? lesson.Date : (!string.IsNullOrEmpty(lesson.StartDatetime) ? lesson.StartDatetime[..Math.Min(10, lesson.StartDatetime.Length)] : "");
                                    bool matchesDate = (!string.IsNullOrEmpty(lesson.Date) && lesson.Date.Equals(date, StringComparison.OrdinalIgnoreCase))
                                        || (!string.IsNullOrEmpty(lesson.StartDatetime) && lesson.StartDatetime.StartsWith(date, StringComparison.OrdinalIgnoreCase));

                                    if (matchesDate)
                                    {
                                        lessons.Add(lesson);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[DiaryClient] Deserializing lesson error: {ex.Message} | Raw: {elem.GetRawText()}");
                            }
                        }
                    }
                }
                if (lessons.Count > 0) break;
            }

            // Deduplicate by LessonId (or SubjectName + StartTime) and sort chronologically
            var distinctLessons = lessons
                .GroupBy(l => !string.IsNullOrEmpty(l.Id) ? l.Id : $"{l.SubjectName}_{l.StartTime}_{l.Theme}")
                .Select(g => g.First())
                .OrderBy(l => l.StartTime)
                .ThenBy(l => l.SubjectName)
                .ToList();

            return distinctLessons;
        }
        catch (DiarySessionExpiredException)
        {
            throw;
        }
        catch (DiaryMaintenanceException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DiaryClient] GetScheduleAsync Error: {ex.Message}");
            return new List<GosuslugiLessonItem>();
        }
    }

    public async Task<List<GosuslugiMarkItem>> GetMarksAsync(string? targetStudentId = null, int pastWeeks = 12, int futureWeeks = 5)
    {
        await EnsureAuthorizationAsync();

        try
        {
            var (studentId, mdmId) = await ResolveStudentIdsAsync(targetStudentId);

            // Query recent week intervals covering the academic quarter or requested window
            var intervals = GetRecentWeekIntervals(pastWeeks, futureWeeks);

            List<string> idsToTry = new List<string>();
            if (!string.IsNullOrEmpty(studentId) && Guid.TryParse(studentId, out var sGuid)) idsToTry.Add(sGuid.ToString("D"));
            if (!string.IsNullOrEmpty(mdmId) && Guid.TryParse(mdmId, out var mGuid))
            {
                string mStr = mGuid.ToString("D");
                if (!idsToTry.Contains(mStr)) idsToTry.Add(mStr);
            }

            Console.WriteLine($"[DiaryClient] 🔍 GetMarksAsync for user={_userId}: targetStudentId={targetStudentId}, resolved studentId={studentId}, mdmId={mdmId}, idsToTry=[{string.Join(',', idsToTry)}]");

            if (idsToTry.Count == 0)
            {
                return new List<GosuslugiMarkItem>();
            }

            var marks = new List<GosuslugiMarkItem>();

            foreach (var sid in idsToTry)
            {
                var datamart = await GetDatamartBatchAsync(sid, intervals, new List<string> { "student_marks" });
                string firstStatus = datamart.Count > 0 ? $"{datamart[0].DataStatus} (hash={datamart[0].Hash})" : "empty";
                Console.WriteLine($"[DiaryClient] Datamart response for user={_userId}, sid={sid}: datamart items={datamart.Count}, status0={firstStatus}, marks in data={datamart.Where(i => i.ObjType == "student_marks" && i.Data != null).Sum(i => i.Data!.Count)} | {string.Join(", ", datamart.Select(d => $"{d.IntervalId}:{d.DataStatus}:c={d.Data?.Count ?? 0}"))}");
                foreach (var item in datamart)
                {
                    if (item.ObjType == "student_marks" && item.Data != null)
                    {
                        foreach (var elem in item.Data)
                        {
                            try
                            {
                                var mark = JsonSerializer.Deserialize<GosuslugiMarkItem>(elem.GetRawText(), DatamartJsonOptions);
                                if (mark != null && (!string.IsNullOrEmpty(mark.Value) || !string.IsNullOrEmpty(mark.MarkValue1)))
                                {
                                    marks.Add(mark);
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[DiaryClient] Deserializing mark error: {ex.Message}");
                            }
                        }
                    }
                }
                if (marks.Count > 0) break;
            }

            // Deduplicate marks by Id (or SubjectName + Date + Value + Weight + WorkKind + LessonId)
            var distinctMarks = marks
                .GroupBy(m => !string.IsNullOrEmpty(m.Id) ? m.Id : $"{m.SubjectName}_{m.Date}_{m.Value}_{m.Weight}_{m.WorkKind}_{m.LessonId}")
                .Select(g => g.First())
                .OrderByDescending(m => m.Date)
                .ToList();

            Console.WriteLine($"[DiaryClient] 📊 Marks for user={_userId}: fetched {distinctMarks.Count} distinct marks ({marks.Count} raw) across {intervals.Count} intervals.");

            return distinctMarks;
        }
        catch (DiarySessionExpiredException)
        {
            throw;
        }
        catch (DiaryMaintenanceException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DiaryClient] GetMarksAsync Error: {ex.Message}");
            return new List<GosuslugiMarkItem>();
        }
    }

    // ── ANTI-DDOS / RATE LIMITING THROTTLE & CIRCUIT BREAKER ──
    private static readonly System.Threading.SemaphoreSlim _gosuslugiThrottle = new System.Threading.SemaphoreSlim(2, 2);
    private static readonly System.Threading.SemaphoreSlim _backgroundThrottle = new System.Threading.SemaphoreSlim(1, 1);
    private static DateTime _circuitBreakerUntil = DateTime.MinValue;
    private static readonly object _delayLock = new object();
    private static DateTime _lastRequestTime = DateTime.MinValue;

    public static async Task AcquireGosuslugiRateLimitAsync(bool isBackground = false)
    {
        if (DateTime.UtcNow < _circuitBreakerUntil)
        {
            int remainingSec = (int)(_circuitBreakerUntil - DateTime.UtcNow).TotalSeconds + 1;
            throw new DiaryMaintenanceException($"🛡️ Защита от бана Госуслуг: повторите запрос через {remainingSec} сек.");
        }

        if (isBackground)
        {
            await _backgroundThrottle.WaitAsync();
        }

        try
        {
            await _gosuslugiThrottle.WaitAsync();

            int delayMs = 0;
            lock (_delayLock)
            {
                var elapsed = (DateTime.UtcNow - _lastRequestTime).TotalMilliseconds;
                if (elapsed < 300)
                {
                    delayMs = 300 - (int)elapsed;
                }
                _lastRequestTime = DateTime.UtcNow.AddMilliseconds(delayMs);
            }

            if (delayMs > 0)
            {
                await Task.Delay(delayMs);
            }
        }
        catch
        {
            if (isBackground)
            {
                try { _backgroundThrottle.Release(); } catch { }
            }
            throw;
        }
    }

    public static void ReleaseGosuslugiRateLimit(bool isBackground = false)
    {
        try { _gosuslugiThrottle.Release(); } catch { }
        if (isBackground)
        {
            try { _backgroundThrottle.Release(); } catch { }
        }
    }

    private void CheckResponseStatus(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new DiarySessionExpiredException("Сессия авторизации Госуслуг истекла. Пожалуйста, откройте Mini App и войдите заново.");
        }
        if (response.StatusCode == (HttpStatusCode)429 || response.StatusCode == HttpStatusCode.ServiceUnavailable || response.StatusCode == HttpStatusCode.BadGateway)
        {
            _circuitBreakerUntil = DateTime.UtcNow.AddSeconds(15);
            throw new DiaryMaintenanceException($"Портал Госуслуг временно ограничил частоту запросов ({(int)response.StatusCode}). Включена защита от бана.");
        }
        response.EnsureSuccessStatusCode();
    }

    private async Task<string> GetJsonAsync(string fullUrl)
    {
        var response = await _client.GetAsync(fullUrl);
        CheckResponseStatus(response);
        return await response.Content.ReadAsStringAsync();
    }

    public async Task RefreshTokensAsync()
    {
        var tokenData = await _tokenStore.GetTokensAsync(_userId);
        if (tokenData == null || string.IsNullOrWhiteSpace(tokenData.AccessToken))
        {
            throw new DiarySessionExpiredException("Отсутствует токен или куки авторизации. Пожалуйста, откройте Mini App и войдите заново.");
        }

        var expUtc = GetTokenExpirationUtc(tokenData.AccessToken);
        if (expUtc != null && (expUtc.Value - DateTime.UtcNow) < TimeSpan.FromMinutes(60))
        {
            Console.WriteLine($"[DiaryClient] 🔄 Проактивное обновление сессии для user={_userId} в RefreshTokensAsync...");
            await TryAutoRenewSessionAsync();
        }

        await EnsureAuthorizationAsync();
        try
        {
            await GetStudentInfoAsync();
        }
        catch (DiaryMaintenanceException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new DiarySessionExpiredException($"Не удалось обновить сессию Госуслуг: {ex.Message}");
        }
    }

    public static List<string> GetRecentWeekIntervals(int count = 12, int futureWeeks = 5)
    {
        var list = new List<string>();
        var now = DateTime.UtcNow;

        // 1. Недели вперед (для оценок, выставленных заранее на следующий месяц / следующую четверть)
        for (int f = futureWeeks; f >= 1; f--)
        {
            var dt = now.AddDays(7 * f);
            int weekNum = System.Globalization.ISOWeek.GetWeekOfYear(dt);
            string interval = $"{weekNum:D2}{dt.Year}";
            if (!list.Contains(interval))
            {
                list.Add(interval);
            }
        }

        // 2. Текущая неделя и прошлые недели четверти
        for (int i = 0; i < count; i++)
        {
            var dt = now.AddDays(-7 * i);
            int weekNum = System.Globalization.ISOWeek.GetWeekOfYear(dt);
            string interval = $"{weekNum:D2}{dt.Year}";
            if (!list.Contains(interval))
            {
                list.Add(interval);
            }
        }
        return list;
    }

    public static List<string> GetIntervalsForDate(string dateStr)
    {
        var list = new List<string>();
        if (DateTime.TryParse(dateStr, out var parsedDate))
        {
            var datesToTry = new[] { parsedDate, parsedDate.AddDays(-7), parsedDate.AddDays(7) };
            foreach (var dt in datesToTry)
            {
                int weekNum = System.Globalization.ISOWeek.GetWeekOfYear(dt);
                int year = dt.Year;
                string v = $"{weekNum:D2}{year}";
                if (!list.Contains(v)) list.Add(v);
            }
        }
        else
        {
            int curWeek = System.Globalization.ISOWeek.GetWeekOfYear(DateTime.UtcNow);
            int curYear = DateTime.UtcNow.Year;
            list.Add($"{curWeek:D2}{curYear}");
        }
        return list;
    }

    public static string CalculateIntervalId(string dateStr)
    {
        if (DateTime.TryParse(dateStr, out var parsedDate))
        {
            int weekNum = System.Globalization.ISOWeek.GetWeekOfYear(parsedDate);
            return $"{weekNum:D2}{parsedDate.Year}";
        }
        int curWeek = System.Globalization.ISOWeek.GetWeekOfYear(DateTime.UtcNow);
        return $"{curWeek:D2}{DateTime.UtcNow.Year}";
    }

    private static string CleanToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return "";
        return token.Trim('"', ' ', '\r', '\n', '\t');
    }
}

public class DiaryMaintenanceException : Exception
{
    public DiaryMaintenanceException(string message) : base(message) { }
}

public class DiarySessionExpiredException : Exception
{
    public DiarySessionExpiredException(string message) : base(message) { }
}
