using Microsoft.AspNetCore.Mvc;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using MirageDiary.Core;
using MirageDiary.Core.Models;
using MirageDiary.Server.Services;
using MirageDiary.Server.Data;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using MAX.Bot.Interfaces;
using MAX.Bot.Interfaces.Models;
using MAX.Bot.Interfaces.Models.Request;
using MAX.Bot.Interfaces.Models.Request.Message;

namespace MirageDiary.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MiniAppController : ControllerBase
{
    private readonly ITokenStore _tokenStore;
    private readonly AppDbContext _context;
    private readonly IHttpClientFactory _httpFactory;
    private readonly BookmarkletAuthService _bookmarkletAuth;
    private readonly PlaywrightAuthManager _playwrightAuth;
    private readonly UserSyncService _userSyncService;

    public MiniAppController(
        ITokenStore tokenStore,
        AppDbContext context,
        IHttpClientFactory httpFactory,
        BookmarkletAuthService bookmarkletAuth,
        PlaywrightAuthManager playwrightAuth,
        UserSyncService userSyncService)
    {
        _tokenStore      = tokenStore;
        _context         = context;
        _httpFactory     = httpFactory;
        _bookmarkletAuth = bookmarkletAuth;
        _playwrightAuth  = playwrightAuth;
        _userSyncService = userSyncService;
    }

    /// <summary>
    /// Called by Mini App when user pastes their access token.
    /// We fetch student info from the diary API server-side so the user
    /// only needs to provide a single token.
    /// </summary>
    [HttpPost("login-with-token")]
    public async Task<IActionResult> LoginWithToken([FromBody] LoginWithTokenRequest request)
    {
        if (string.IsNullOrEmpty(request.TelegramUserId) || string.IsNullOrEmpty(request.AccessToken))
            return BadRequest(new { error = "TelegramUserId and AccessToken are required." });

        string studentId = "", className = "", orgName = "", refreshToken = request.RefreshToken ?? "";

        string rawToken = request.AccessToken;
        string oid = "";
        string deviceId = "";
        string sessionId = "";

        if (rawToken.Contains("u="))
        {
            var m = System.Text.RegularExpressions.Regex.Match(rawToken, @"\bu=([^;]+)");
            if (m.Success) oid = m.Groups[1].Value.Trim();
        }

        if (!string.IsNullOrEmpty(oid))
        {
            deviceId = BitConverter.ToString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(request.TelegramUserId))).Replace("-", "").Substring(0, 16).ToLower();
            string accT = rawToken;
            var accMatch = System.Text.RegularExpressions.Regex.Match(rawToken, @"\bacc_t=([^;]+)");
            if (accMatch.Success) accT = accMatch.Groups[1].Value.Trim();

            try
            {
                var mobileClient = new MobileEsiaClient(_httpFactory.CreateClient());
                string entToken = MobileEsiaClient.BuildEntToken(deviceId);
                var renewResult = await mobileClient.CreateSessionAsync(deviceId, oid, accT, entToken);
                if (renewResult != null && !string.IsNullOrEmpty(renewResult.AccessToken))
                {
                    rawToken = renewResult.AccessToken;
                    sessionId = renewResult.SessionId;
                    Console.WriteLine($"[MiniApp] 🚀 Пользователь {request.TelegramUserId} успешно повышен до мобильной сессии!");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MiniApp] ℹ️ Повышение до мобильной сессии пропущено: {ex.Message}");
            }
        }

        await _tokenStore.SaveTokensAsync(request.TelegramUserId, new TokenData
        {
            AccessToken = rawToken,
            RefreshToken = refreshToken,
            Oid = oid,
            DeviceId = deviceId,
            SessionId = sessionId
        });

        try
        {
            var diaryClient = new DiaryClient(_httpFactory.CreateClient(), _tokenStore, request.TelegramUserId);
            var res = await ResolveGosuslugiStudentDetailsAsync(diaryClient);
            studentId = res.StudentId;
            className = res.ClassName;
            orgName   = res.OrgName;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MiniApp] Ошибка получения данных Госуслуг: {ex.Message}");
        }

        var tokenData = new TokenData
        {
            AccessToken  = rawToken,
            RefreshToken = refreshToken,
            StudentId    = studentId,
            Oid          = oid,
            DeviceId     = deviceId,
            SessionId    = sessionId
        };

        await _tokenStore.SaveTokensAsync(request.TelegramUserId, tokenData);

        var user = await _context.Users.FindAsync(request.TelegramUserId);
        if (user != null)
        {
            user.ClassName = className;
            user.OrgName   = orgName;
            user.StudentId = studentId;
            user.Oid       = oid;
            user.DeviceId  = deviceId;
            user.SessionId = sessionId;
            user.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            // Mirror across all linked platforms (e.g. MAX bot)
            await _userSyncService.SyncUserAcrossPlatformsAsync(user);
        }

        return Ok(new
        {
            success   = true,
            studentId,
            className,
            orgName
        });
    }

    /// <summary>
    /// Called by Mini App when it already has all token fields (e.g. from postMessage).
    /// </summary>
    [HttpPost("sync-tokens")]
    public async Task<IActionResult> SyncTokens([FromBody] SyncTokensRequest request)
    {
        if (string.IsNullOrEmpty(request.TelegramUserId) || string.IsNullOrEmpty(request.AccessToken))
            return BadRequest("Missing required fields.");

        var tokenData = new TokenData
        {
            AccessToken  = request.AccessToken,
            RefreshToken = request.RefreshToken,
            StudentId    = request.StudentId
        };

        await _tokenStore.SaveTokensAsync(request.TelegramUserId, tokenData);

        var user = await _context.Users.FindAsync(request.TelegramUserId);
        if (user != null)
        {
            user.ClassName = request.ClassName;
            user.OrgName   = request.OrgName;
            await _context.SaveChangesAsync();
        }

        return Ok(new { success = true });
    }

    // =========================================================================
    // BOOKMARKLET AUTH FLOW
    // =========================================================================

    /// <summary>
    /// Step 1: Mini App calls this to get a short-lived code tied to the Telegram user.
    /// Returns the code + a ready-to-use bookmarklet JavaScript URL.
    /// </summary>
    [HttpPost("create-code")]
    public IActionResult CreateCode([FromBody] CreateCodeRequest request)
    {
        if (string.IsNullOrEmpty(request.TelegramUserId))
            return BadRequest(new { error = "TelegramUserId required." });

        var code = _bookmarkletAuth.CreateCode(request.TelegramUserId);
        var serverUrl = $"{Request.Scheme}://{Request.Host}";

        // Minimal bookmarklet JS — must be compact to fit a browser bookmark
        var bookmarkletJs = $"javascript:(function(){{" +
            $"var t=localStorage.getItem('Auth__token')," +
            $"r=localStorage.getItem('Auth__refresh')||'';" +
            $"if(!t){{alert('\u0421\u043d\u0430\u0447\u0430\u043b\u0430 \u0432\u043e\u0439\u0434\u0438\u0442\u0435 \u0432 \u0434\u043d\u0435\u0432\u043d\u0438\u043a!');return;}}" +
            $"fetch('{serverUrl}/api/miniapp/bookmarklet',{{" +
            $"method:'POST'," +
            $"headers:{{'Content-Type':'application/json'}}," +
            $"body:JSON.stringify({{code:'{code}',token:t,refresh:r}})" +
            $"}})" +
            $".then(function(res){{return res.json();}})" +
            $".then(function(d){{" +
            $"document.title='\u2705 \u0413\u043e\u0442\u043e\u0432\u043e!';" +
            $"alert('\u2705 \u0422\u043e\u043a\u0435\u043d \u043e\u0442\u043f\u0440\u0430\u0432\u043b\u0435\u043d! \u0412\u0435\u0440\u043d\u0438\u0442\u0435\u0441\u044c \u0432 Telegram.');" +
            $"}})" +
            $".catch(function(){{alert('\u041e\u0448\u0438\u0431\u043a\u0430. \u041f\u043e\u043f\u0440\u043e\u0431\u0443\u0439\u0442\u0435 \u0435\u0449\u0451.');}})" +
            $"}})();";

        return Ok(new { code, bookmarkletJs, expiresInMinutes = 10 });
    }

    /// <summary>
    /// Step 2: Called by the bookmarklet running in the diary browser.
    /// Receives tokens, looks up TelegramId by code, saves everything.
    /// </summary>
    [HttpPost("bookmarklet")]
    public async Task<IActionResult> Bookmarklet([FromBody] BookmarkletRequest request)
    {
        var telegramId = _bookmarkletAuth.ClaimCode(request.Code);
        if (telegramId == null)
            return BadRequest(new { error = "Invalid or expired code." });

        // Fetch student data server-side
        string studentId = "", className = "", orgName = "";
        try
        {
            var client = _httpFactory.CreateClient();
            if (request.Token.StartsWith("acc_t=") || request.Token.Contains(";"))
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", request.Token);
            }
            else
            {
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", request.Token);
            }

            var resp = await client.GetAsync("https://www.gosuslugi.ru/api/myschool/v2/auth/student");
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                {
                    var s = doc.RootElement[0];
                    studentId = s.TryGetProperty("student_id", out var id) ? id.GetString() ?? "" : "";
                    className = s.TryGetProperty("class_name", out var cn) ? cn.GetString() ?? "" : "";
                    orgName   = s.TryGetProperty("org_name",   out var org) ? org.GetString() ?? "" : "";
                }
                else if (doc.RootElement.TryGetProperty("students", out var students) &&
                    students.GetArrayLength() > 0)
                {
                    var s = students[0];
                    studentId = s.TryGetProperty("student_id", out var sid) ? sid.GetString() ?? "" : (s.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "");
                    className = s.TryGetProperty("className", out var cn)  ? cn.GetString()  ?? "" : "";
                    orgName   = s.TryGetProperty("orgName",   out var org) ? org.GetString() ?? "" : "";
                }
            }
        }
        catch { /* proceed with empty student info */ }

        var tokenData = new TokenData
        {
            AccessToken  = request.Token,
            RefreshToken = request.Refresh,
            StudentId    = studentId
        };
        await _tokenStore.SaveTokensAsync(telegramId, tokenData);

        var user = await _context.Users.FindAsync(telegramId);
        if (user != null)
        {
            user.ClassName = className;
            user.OrgName   = orgName;
            await _context.SaveChangesAsync();
        }

        return Ok(new { success = true, studentId, className, orgName });
    }

    /// <summary>
    /// Step 3: Mini App polls this every 2 seconds to detect completion.
    /// Returns { done: true } once the bookmarklet has posted tokens.
    /// </summary>
    [HttpGet("poll-auth/{code}")]
    public IActionResult PollAuth(string code)
    {
        bool done = _bookmarkletAuth.IsCompleted(code);
        return Ok(new { done });
    }

    // =========================================================================
    // HEADLESS BROWSER (PLAYWRIGHT) AUTH FLOW
    // =========================================================================

    [HttpGet("auth/check/{telegramUserId}")]
    public async Task<IActionResult> CheckAuth(string telegramUserId)
    {
        var tokens = await _tokenStore.GetTokensAsync(telegramUserId);
        if (tokens == null || string.IsNullOrEmpty(tokens.AccessToken))
        {
            return Ok(new Models.CheckAuthResponse { IsAuthorized = false });
        }

        // Если токен Госуслуг истёк, сначала пробуем бесшумно обновить через createSession
        if (IsJwtExpired(tokens.AccessToken))
        {
            if (!string.IsNullOrEmpty(tokens.DeviceId) && !string.IsNullOrEmpty(tokens.Oid))
            {
                try
                {
                    var mobileClient = new MobileEsiaClient(_httpFactory.CreateClient());
                    string entToken = MobileEsiaClient.BuildEntToken(tokens.DeviceId);
                    var renewResult = await mobileClient.CreateSessionAsync(tokens.DeviceId, tokens.Oid, tokens.AccessToken, entToken);
                    if (renewResult != null && !string.IsNullOrEmpty(renewResult.AccessToken))
                    {
                        tokens.AccessToken = renewResult.AccessToken;
                        tokens.SessionId = renewResult.SessionId;
                        await _tokenStore.SaveTokensAsync(telegramUserId, tokens);
                        Console.WriteLine($"[MiniApp] 🚀 Сессия {telegramUserId} мгновенно восстановлена при вызове CheckAuth!");
                        return Ok(new Models.CheckAuthResponse { IsAuthorized = true });
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MiniApp] ⚠️ Ошибка авто-восстановления в CheckAuth: {ex.Message}");
                }
            }

            return Ok(new Models.CheckAuthResponse { IsAuthorized = false });
        }

        try
        {
            var diaryClient = new DiaryClient(_httpFactory.CreateClient("diary-proxy"), _tokenStore, telegramUserId);
            var students = await diaryClient.GetStudentInfoAsync();
            if (students != null && students.Count > 0)
            {
                return Ok(new Models.CheckAuthResponse { IsAuthorized = true });
            }
        }
        catch { }

        return Ok(new Models.CheckAuthResponse { IsAuthorized = true });
    }

    private static bool IsJwtExpired(string rawToken)
    {
        if (string.IsNullOrEmpty(rawToken)) return true;

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
                    var expDate = DateTimeOffset.FromUnixTimeSeconds(expSeconds).UtcDateTime;
                    if (DateTime.UtcNow >= expDate)
                    {
                        return true;
                    }
                }
            }
            catch { }
        }

        return false;
    }

    [HttpPost("auth/start")]
    public async Task<IActionResult> StartAuth([FromBody] StartAuthRequest request)
    {
        if (string.IsNullOrEmpty(request.TelegramUserId) || string.IsNullOrEmpty(request.Login) || string.IsNullOrEmpty(request.Password))
        {
            Console.WriteLine($"[MiniApp 400 Bad Request] StartAuth missing fields. TelegramUserId: '{request.TelegramUserId}', Login: '{request.Login}'");
            return BadRequest(new { error = "TelegramUserId, Login, and Password are required." });
        }
        var session = await _playwrightAuth.StartAuthAsync(request.TelegramUserId, request.Login, request.Password);
        return Ok(new Models.StartAuthResponse { SessionId = session.SessionId, Status = session.Status.ToString() });
    }

    [HttpGet("auth/status/{sessionId}")]
    public async Task<IActionResult> GetAuthStatus(
        string sessionId,
        [FromServices] Telegram.Bot.ITelegramBotClient botClient,
        [FromServices] IMaxBotClient? maxBotClient = null)
    {
        try
        {
            var session = _playwrightAuth.GetSession(sessionId);
            if (session == null) return NotFound(new { error = "Session not found or expired" });

            if (session.Status == AuthStatus.Success && !string.IsNullOrEmpty(session.AccessToken))
            {
                // Prevent multiple triggers
                if (session.ErrorMessage == "Processed")
                    return Ok(new Models.AuthStatusResponse { Status = "Success" });
                
                Console.WriteLine($"[MiniApp] Статус Success! Токен длина: {session.AccessToken.Length}, Refresh: {session.RefreshToken?.Length ?? 0}");
                session.ErrorMessage = "Processed"; // simple flag

                // Save tokens first so DiaryClient can use them
                await _tokenStore.SaveTokensAsync(session.TelegramUserId, new TokenData
                {
                    AccessToken = session.AccessToken,
                    RefreshToken = session.RefreshToken ?? "",
                    Oid = session.Oid,
                    SessionId = session.MobileSessionId,
                    DeviceId = session.DeviceId
                });

                // Try to resolve studentId, className, orgName via Gosuslugi API
                string studentId = "", className = "", orgName = "";
                try
                {
                    var diaryClient = new DiaryClient(_httpFactory.CreateClient("diary-proxy"), _tokenStore, session.TelegramUserId);
                    var res = await ResolveGosuslugiStudentDetailsAsync(diaryClient);
                    studentId = res.StudentId;
                    className = res.ClassName;
                    orgName   = res.OrgName;
                }
                catch (Exception ex) { Console.WriteLine($"[MiniApp] Ошибка получения профиля: {ex.Message}"); }

                Console.WriteLine($"[MiniApp] Сохраняем токен для userId={session.TelegramUserId}, studentId={studentId}");
                await _tokenStore.SaveTokensAsync(session.TelegramUserId, new TokenData
                {
                    AccessToken = session.AccessToken,
                    RefreshToken = session.RefreshToken ?? "",
                    StudentId = studentId,
                    Oid = session.Oid,
                    SessionId = session.MobileSessionId,
                    DeviceId = session.DeviceId
                });
                Console.WriteLine($"[MiniApp] Токен сохранён!");

                var user = await _context.Users.FindAsync(session.TelegramUserId);
                if (user != null)
                {
                    user.ClassName = className;
                    user.OrgName = orgName;
                    user.StudentId = studentId;
                    user.Oid = session.Oid;
                    user.DeviceId = session.DeviceId;
                    user.SessionId = session.MobileSessionId;
                    user.AccessToken = session.AccessToken;
                    user.RefreshToken = session.RefreshToken ?? "";
                    user.UpdatedAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                    Console.WriteLine($"[MiniApp] Профиль пользователя обновлён!");

                    // Automatically mirror tokens, metadata and settings across linked platforms (e.g. MAX <-> Telegram)
                    await _userSyncService.SyncUserAcrossPlatformsAsync(user);
                }

                // Send a bot notification message (Telegram or MAX)
                try
                {
                    string successMessage = "✅ *Авторизация успешно завершена!*\n\nТеперь бот может получать ваше расписание и оценки.";
                    if (session.TelegramUserId.StartsWith("max_"))
                    {
                        if (long.TryParse(session.TelegramUserId.Replace("max_", ""), out long maxChatId) && maxBotClient != null)
                        {
                            await maxBotClient.SendMessageAsync(new SendMessageRequest
                            {
                                UserId = maxChatId,
                                Text = successMessage,
                                Format = MessageFormat.Markdown
                            });
                            Console.WriteLine($"[MiniApp] MAX Bot сообщение отправлено!");
                        }
                    }
                    else
                    {
                        await botClient.SendMessage(
                            chatId: session.TelegramUserId,
                            text: successMessage,
                            parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown);
                        Console.WriteLine($"[MiniApp] Telegram сообщение отправлено!");
                    }
                }
                catch (Exception ex) { Console.WriteLine($"[MiniApp] Ошибка отправки бота сообщения: {ex.Message}"); }

                Console.WriteLine($"[MiniApp] Отправляем Success ответ клиенту.");
                return Ok(new Models.AuthStatusResponse
                {
                    Status = "Success",
                    StudentId = studentId,
                    ClassName = className,
                    OrgName = orgName
                });
            }
            
            return Ok(new Models.AuthStatusResponse
            {
                Status = session.Status.ToString(),
                CaptchaImage = session.CaptchaImageBase64,
                ErrorMessage = session.ErrorMessage
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MiniApp] ОШИБКА в GetAuthStatus: {ex}");
            return StatusCode(500, new Models.AuthStatusResponse { Status = "Failed", ErrorMessage = "Внутренняя ошибка сервера: " + ex.Message });
        }
    }

    [HttpPost("auth/submit-input")]
    public IActionResult SubmitInput([FromBody] SubmitInputRequest request)
    {
        if (string.IsNullOrEmpty(request.SessionId) || string.IsNullOrEmpty(request.Input))
        {
            Console.WriteLine($"[MiniApp 400 Bad Request] SubmitInput missing required fields.");
            return BadRequest(new { error = "SessionId and Input are required." });
        }
        var success = _playwrightAuth.SubmitUserInput(request.SessionId, request.Input);
        if (!success)
        {
            Console.WriteLine($"[MiniApp 400 Bad Request] SubmitInput failed for SessionId: {request.SessionId}");
            return BadRequest(new { error = "Failed to submit input. Session may be expired or not waiting for input." });
        }
        return Ok(new Models.SimpleSuccessResponse { Success = true });
    }

    private static async Task<(string StudentId, string ClassName, string OrgName)> ResolveGosuslugiStudentDetailsAsync(DiaryClient diaryClient)
    {
        string studentId = "", className = "", orgName = "";
        try
        {
            var studentInfos = await diaryClient.GetStudentInfoAsync();
            if (studentInfos != null && studentInfos.Count > 0)
            {
                var student = studentInfos[0];
                studentId = !string.IsNullOrEmpty(student.StudentId) ? student.StudentId : (student.MdmId ?? "");

                List<string> idsToTry = new List<string>();
                if (!string.IsNullOrEmpty(student.StudentId)) idsToTry.Add(student.StudentId);
                if (!string.IsNullOrEmpty(student.MdmId) && !idsToTry.Contains(student.MdmId)) idsToTry.Add(student.MdmId);

                List<string> yearsToTry = new List<string> { DateTime.UtcNow.Year.ToString(), (DateTime.UtcNow.Year - 1).ToString() };

                foreach (var sid in idsToTry)
                {
                    foreach (var year in yearsToTry)
                    {
                        try
                        {
                            var datamart = await diaryClient.GetDatamartAsync(sid, year, new List<string> { "student_classes" });
                            if (datamart != null && datamart.Count > 0)
                            {
                                foreach (var dmItem in datamart)
                                {
                                    if (dmItem.Data != null && dmItem.Data.Count > 0)
                                    {
                                        foreach (var elem in dmItem.Data)
                                        {
                                            var classInfo = JsonSerializer.Deserialize<GosuslugiClassInfo>(elem.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                                            if (classInfo != null && (!string.IsNullOrEmpty(classInfo.ShortName) || !string.IsNullOrEmpty(classInfo.ClassNum)))
                                            {
                                                className = $"{classInfo.ClassNum} {classInfo.ClassLetter}".Trim();
                                                orgName = classInfo.ShortName;
                                                break;
                                            }
                                        }
                                    }
                                    if (!string.IsNullOrEmpty(orgName)) break;
                                }
                            }
                        }
                        catch { }
                        if (!string.IsNullOrEmpty(orgName)) break;
                    }
                    if (!string.IsNullOrEmpty(orgName)) break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MiniApp] Ошибка получения профиля: {ex.Message}");
        }

        return (studentId, className, orgName);
    }
}

public class StartAuthRequest
{
    public string TelegramUserId { get; set; } = "";
    public string Login { get; set; } = "";
    public string Password { get; set; } = "";
}

public class SubmitInputRequest
{
    public string SessionId { get; set; } = "";
    public string Input { get; set; } = "";
}

public class CreateCodeRequest
{
    public string TelegramUserId { get; set; } = "";
}

public class LoginWithTokenRequest
{
    public string TelegramUserId { get; set; } = "";
    public string AccessToken    { get; set; } = "";
    public string RefreshToken   { get; set; } = "";
}

public class SyncTokensRequest
{
    public string TelegramUserId { get; set; } = "";
    public string AccessToken    { get; set; } = "";
    public string RefreshToken   { get; set; } = "";
    public string StudentId      { get; set; } = "";
    public string ClassName      { get; set; } = "";
    public string OrgName        { get; set; } = "";
}

public class BookmarkletRequest
{
    public string Code    { get; set; } = "";
    public string Token   { get; set; } = "";
    public string Refresh { get; set; } = "";
}
