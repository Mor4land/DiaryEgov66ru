using Microsoft.AspNetCore.Mvc;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using MirageDiary.Core;
using MirageDiary.Server.Services;
using MirageDiary.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace MirageDiary.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MiniAppController : ControllerBase
{
    private readonly ITokenStore _tokenStore;
    private readonly AppDbContext _context;
    private readonly IHttpClientFactory _httpFactory;
    private readonly BookmarkletAuthService _bookmarkletAuth;

    public MiniAppController(
        ITokenStore tokenStore,
        AppDbContext context,
        IHttpClientFactory httpFactory,
        BookmarkletAuthService bookmarkletAuth)
    {
        _tokenStore      = tokenStore;
        _context         = context;
        _httpFactory     = httpFactory;
        _bookmarkletAuth = bookmarkletAuth;
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

        // ── Fetch student data from diary API ─────────────────────────────────
        string studentId = "", className = "", orgName = "", refreshToken = request.RefreshToken ?? "";

        try
        {
            var client = _httpFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", request.AccessToken);

            var resp = await client.GetAsync("https://dnevnik.egov66.ru/api/students");
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("students", out var students) &&
                    students.GetArrayLength() > 0)
                {
                    var s = students[0];
                    studentId = s.TryGetProperty("id",        out var id)  ? id.GetString()  ?? "" : "";
                    className = s.TryGetProperty("className", out var cn)  ? cn.GetString()  ?? "" : "";
                    orgName   = s.TryGetProperty("orgName",   out var org) ? org.GetString() ?? "" : "";
                }
            }
        }
        catch
        {
            // If we can't reach the diary API, proceed with what we have
        }

        // ── Save to DB ────────────────────────────────────────────────────────
        var tokenData = new TokenData
        {
            AccessToken  = request.AccessToken,
            RefreshToken = refreshToken,
            StudentId    = studentId
        };

        await _tokenStore.SaveTokensAsync(request.TelegramUserId, tokenData);

        var user = await _context.Users.FindAsync(request.TelegramUserId);
        if (user != null)
        {
            user.ClassName = className;
            user.OrgName   = orgName;
            await _context.SaveChangesAsync();
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
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", request.Token);
            var resp = await client.GetAsync("https://dnevnik.egov66.ru/api/students");
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("students", out var students) &&
                    students.GetArrayLength() > 0)
                {
                    var s = students[0];
                    studentId = s.TryGetProperty("id",        out var id)  ? id.GetString()  ?? "" : "";
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
        bool done = !_bookmarkletAuth.IsPending(code);
        return Ok(new { done });
    }
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
