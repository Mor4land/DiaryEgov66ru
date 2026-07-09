using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace MirageDiary.Server.Services;

public enum AuthStatus
{
    InProgress,
    RequiresOtp,
    RequiresCaptcha,
    Success,
    Failed,
    Expired
}

public class AuthSession : IAsyncDisposable
{
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public string TelegramUserId { get; }
    public AuthStatus Status { get; set; } = AuthStatus.InProgress;
    
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string ErrorMessage { get; set; } = "";
    public string CaptchaImageBase64 { get; set; } = "";
    
    public DateTime CreatedAt { get; } = DateTime.UtcNow;

    // We store the TaskCompletionSource to wait for user input (OTP or Captcha)
    public TaskCompletionSource<string>? UserInputTcs { get; set; }

    public IPlaywright? PlaywrightInstance { get; set; }
    public IBrowser? Browser { get; set; }
    public IBrowserContext? Context { get; set; }

    public AuthSession(string telegramUserId)
    {
        TelegramUserId = telegramUserId;
    }

    public async ValueTask DisposeAsync()
    {
        if (Context != null) await Context.CloseAsync();
        if (Browser != null) await Browser.CloseAsync();
        PlaywrightInstance?.Dispose();
    }
}

public class PlaywrightAuthManager
{
    private readonly ConcurrentDictionary<string, AuthSession> _sessions = new();
    
    // We clean up sessions older than 5 minutes to prevent memory leaks
    public PlaywrightAuthManager()
    {
        // Simple background cleanup timer
        var timer = new Timer(_ => CleanupExpiredSessions(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    private void CleanupExpiredSessions()
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in _sessions)
        {
            if (now - kvp.Value.CreatedAt > TimeSpan.FromMinutes(5))
            {
                if (_sessions.TryRemove(kvp.Key, out var session))
                {
                    session.Status = AuthStatus.Expired;
                    _ = session.DisposeAsync();
                }
            }
        }
    }

    public AuthSession? GetSession(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return session;
    }

    public async Task<AuthSession> StartAuthAsync(string telegramUserId, string login, string password)
    {
        var session = new AuthSession(telegramUserId);
        _sessions[session.SessionId] = session;

        // Start the browser workflow in the background
        _ = Task.Run(async () => await RunBrowserWorkflowAsync(session, login, password));

        return session;
    }

    private async Task RunBrowserWorkflowAsync(AuthSession session, string login, string password)
    {
        try
        {
            session.PlaywrightInstance = await Playwright.CreateAsync();
            session.Browser = await session.PlaywrightInstance.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                ExecutablePath = System.IO.Path.GetFullPath(System.IO.Path.Combine(Environment.CurrentDirectory, "chrome", "chrome-win64", "chrome.exe")),
                Args = new[] { "--disable-blink-features=AutomationControlled" } // Try to evade basic bot detection
            });

            session.Context = await session.Browser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
            });

            var page = await session.Context.NewPageAsync();

            // 1. Go to Diary login page
            await page.GotoAsync("https://dnevnik.egov66.ru/login");
            
            // 2. Click "Войти через Госуслуги"
            // Let's assume there's a button or link that goes to ESIA.
            // Often dnevnik redirects immediately or has a specific login button.
            // Wait for network idle to let any auto-redirects happen
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            var loginBtn = await page.QuerySelectorAsync("a[href*='esia'], button:has-text('Госуслуги'), button:has-text('Войти')");
            if (loginBtn != null)
            {
                await loginBtn.ClickAsync();
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            }

            // 3. We should now be on Gosuslugi login page
            if (!page.Url.Contains("esia.gosuslugi.ru"))
            {
                session.Status = AuthStatus.Failed;
                session.ErrorMessage = "Не удалось перейти на страницу Госуслуг.";
                await CleanupSession(session);
                return;
            }

            // 4. Input login
            await page.FillAsync("input[id='login']", login);
            await page.FillAsync("input[id='password']", password);
            await page.ClickAsync("button[type='submit']");
            
            // 5. Wait for the next screen (could be success, CAPTCHA, or 2FA)
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            
            // Loop to handle Captcha & 2FA if they appear
            for (int i = 0; i < 3; i++) // Max 3 iterations of challenge
            {
                // Check if we got redirected back to dnevnik
                if (page.Url.Contains("dnevnik.egov66.ru"))
                {
                    break; // Success!
                }

                // Check for CAPTCHA
                var captchaImg = await page.QuerySelectorAsync("img[class*='captcha']"); // Adjust selector based on ESIA
                if (captchaImg != null)
                {
                    var buffer = await captchaImg.ScreenshotAsync();
                    session.CaptchaImageBase64 = Convert.ToBase64String(buffer);
                    session.Status = AuthStatus.RequiresCaptcha;
                    
                    // Wait for user input from API
                    session.UserInputTcs = new TaskCompletionSource<string>();
                    var captchaText = await session.UserInputTcs.Task;
                    
                    await page.FillAsync("input[id='captcha']", captchaText); // Adjust selector
                    await page.ClickAsync("button[type='submit']");
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                    continue;
                }

                // Check for 2FA (SMS or TOTP)
                var otpInput = await page.QuerySelectorAsync("input[id='otp'], input[name='otp'], input[id='code']"); // Adjust selector
                if (otpInput != null)
                {
                    session.Status = AuthStatus.RequiresOtp;
                    
                    // Wait for user input from API
                    session.UserInputTcs = new TaskCompletionSource<string>();
                    var otpCode = await session.UserInputTcs.Task;
                    
                    await otpInput.FillAsync(otpCode);
                    await page.ClickAsync("button[type='submit']");
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                    continue;
                }

                // Check for generic error message on ESIA (wrong password)
                var errorElem = await page.QuerySelectorAsync(".error-message, .alert-danger, .ui-error");
                if (errorElem != null)
                {
                    session.ErrorMessage = await errorElem.InnerTextAsync();
                    session.Status = AuthStatus.Failed;
                    await CleanupSession(session);
                    return;
                }

                // Wait a bit and check again if page is still loading
                await Task.Delay(2000);
            }

            // 6. After breaking loop, verify we are on dnevnik
            if (page.Url.Contains("dnevnik.egov66.ru"))
            {
                // Extract tokens from localStorage
                var token = await page.EvaluateAsync<string>("localStorage.getItem('Auth__token')");
                var refresh = await page.EvaluateAsync<string>("localStorage.getItem('Auth__refresh')");

                if (!string.IsNullOrEmpty(token))
                {
                    session.AccessToken = token;
                    session.RefreshToken = refresh ?? "";
                    session.Status = AuthStatus.Success;
                }
                else
                {
                    session.Status = AuthStatus.Failed;
                    session.ErrorMessage = "Авторизация прошла, но токен не найден.";
                }
            }
            else
            {
                session.Status = AuthStatus.Failed;
                session.ErrorMessage = "Превышено время ожидания или неизвестный экран.";
            }
        }
        catch (Exception ex)
        {
            session.Status = AuthStatus.Failed;
            session.ErrorMessage = "Внутренняя ошибка: " + ex.Message;
        }
        finally
        {
            await CleanupSession(session);
        }
    }

    public bool SubmitUserInput(string sessionId, string input)
    {
        if (_sessions.TryGetValue(sessionId, out var session) && session.UserInputTcs != null && !session.UserInputTcs.Task.IsCompleted)
        {
            session.Status = AuthStatus.InProgress;
            session.UserInputTcs.SetResult(input);
            return true;
        }
        return false;
    }

    private async Task CleanupSession(AuthSession session)
    {
        await session.DisposeAsync();
        // We keep it in the dictionary so the client can query its final status (Success/Failed),
        // but it will be removed after 5 minutes by the cleanup timer.
    }
}
