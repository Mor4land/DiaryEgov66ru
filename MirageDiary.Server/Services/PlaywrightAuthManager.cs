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
            Console.WriteLine($"[Playwright] Запуск сессии для {session.TelegramUserId}...");
            session.PlaywrightInstance = await Playwright.CreateAsync();
            Console.WriteLine($"[Playwright] Playwright создан. Запуск Chromium...");
            
            session.Browser = await session.PlaywrightInstance.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = false, // Покажем браузер юзеру, чтобы он видел, что происходит!
                ExecutablePath = System.IO.Path.GetFullPath(System.IO.Path.Combine(Environment.CurrentDirectory, "chrome", "chrome-win64", "chrome.exe")),
                Args = new[] { "--disable-blink-features=AutomationControlled" }
            });
            Console.WriteLine($"[Playwright] Chromium запущен. Создаем контекст...");

            session.Context = await session.Browser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
            });

            var page = await session.Context.NewPageAsync();
            Console.WriteLine($"[Playwright] Страница создана. Переход на login...");

            // 1. Navigate to Dnevnik Login page
            await page.GotoAsync("https://dnevnik.egov66.ru/login/", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            Console.WriteLine($"[Playwright] Страница открыта. Ждем NetworkIdle...");

            // 2. Click "Войти через Госуслуги"
            Console.WriteLine($"[Playwright] Ищем кнопку Госуслуг...");
            var loginBtn = await page.QuerySelectorAsync("a[href*='esia'], button:has-text('Госуслуги'), button:has-text('Войти')");
            if (loginBtn != null)
            {
                Console.WriteLine($"[Playwright] Кнопка найдена. Кликаем...");
                await loginBtn.ClickAsync();
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            }

            Console.WriteLine($"[Playwright] Текущий URL: {page.Url}");

            Console.WriteLine($"[Playwright] Вводим логин и пароль...");
            // 4. Input login
            var loginInput = page.Locator("input[id='login']");
            await loginInput.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            await Task.Delay(1000); 
            
            await loginInput.FillAsync(login);
            await page.FillAsync("input[id='password']", password);
            // Press Enter
            await page.PressAsync("input[id='password']", "Enter");
            
            Console.WriteLine($"[Playwright] Данные отправлены. Ожидаем ответ...");
            
            // Loop to handle redirects, captchas, and errors
            for (int i = 0; i < 30; i++)
            {
                var url = page.Url;
                Console.WriteLine($"[Playwright] Итерация проверки {i + 1}. URL: {url}");
                
                // Get page text to check for Angular error messages
                var pageText = "";
                try { pageText = await page.EvaluateAsync<string>("document.body.innerText") ?? ""; } catch { }

                // Check for generic error message on ESIA (wrong password)
                if (pageText.Contains("Неверный логин") || pageText.Contains("учетная запись не найдена") || pageText.Contains("Неверный пароль") || pageText.Contains("Неверно указан логин"))
                {
                    Console.WriteLine("[Playwright] Обнаружена ошибка авторизации: Неверный логин или пароль.");
                    session.ErrorMessage = "Неверный логин или пароль.";
                    session.Status = AuthStatus.Failed;
                    await CleanupSession(session);
                    return;
                }

                // Check for CAPTCHA
                if (pageText.Contains("Введите символы") || pageText.Contains("капча") || url.Contains("captcha"))
                {
                    Console.WriteLine("[Playwright] Обнаружена капча!");
                    session.Status = AuthStatus.RequiresCaptcha;
                    return;
                }
                
                // Check for 2FA / OTP
                if (pageText.Contains("Введите код") || url.Contains("otp") || url.Contains("login/mfa"))
                {
                    Console.WriteLine("[Playwright] Обнаружено окно 2FA/ОТП!");
                    session.Status = AuthStatus.RequiresOtp;
                    return;
                }

                if (url.Contains("dnevnik.egov66.ru") && !url.Contains("login"))
                {
                    Console.WriteLine($"[Playwright] Успешный редирект на главную дневника!");
                    await Task.Delay(2000);
                    
                    var token = await page.EvaluateAsync<string>("localStorage.getItem('Auth__token')");
                    var refresh = await page.EvaluateAsync<string>("localStorage.getItem('Auth__refresh')");

                    if (!string.IsNullOrEmpty(token) && token.StartsWith("\"")) token = token.Trim('"');
                    if (!string.IsNullOrEmpty(refresh) && refresh.StartsWith("\"")) refresh = refresh.Trim('"');

                    if (!string.IsNullOrEmpty(token))
                    {
                        session.AccessToken = token;
                        session.RefreshToken = refresh ?? "";
                        session.Status = AuthStatus.Success;
                    }
                    else
                    {
                        Console.WriteLine($"[Playwright] ОШИБКА: Токен пустой!");
                        session.ErrorMessage = "Не удалось извлечь токен из Дневника.";
                        session.Status = AuthStatus.Failed;
                    }

                    await CleanupSession(session);
                    return;
                }

                await Task.Delay(2000);
            }

            Console.WriteLine($"[Playwright] Истек таймаут авторизации!");
            session.ErrorMessage = "Таймаут авторизации.";
            session.Status = AuthStatus.Failed;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Playwright] ИСКЛЮЧЕНИЕ: {ex}");
            session.ErrorMessage = "Ошибка браузера: " + ex.Message;
            session.Status = AuthStatus.Failed;
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
