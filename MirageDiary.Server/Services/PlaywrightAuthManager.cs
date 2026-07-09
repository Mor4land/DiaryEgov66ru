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
                Headless = false,
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

            await page.GotoAsync("https://dnevnik.egov66.ru/login/", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            Console.WriteLine($"[Playwright] Страница открыта.");

            Console.WriteLine($"[Playwright] Ищем кнопку Госуслуг...");
            var loginBtn = await page.QuerySelectorAsync("a[href*='esia'], button:has-text('Госуслуги'), button:has-text('Войти')");
            if (loginBtn != null)
            {
                Console.WriteLine($"[Playwright] Кнопка найдена. Кликаем...");
                await loginBtn.ClickAsync();
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            }

            Console.WriteLine($"[Playwright] Текущий URL: {page.Url}");
            Console.WriteLine($"[Playwright] Проверяем, не показан ли QR-код...");
            
            // Госуслуги могут показать QR-код вместо логина. Нажимаем "Логин и пароль"
            try
            {
                var loginPasswordBtn = page.Locator("text=Логин и пароль");
                if (await loginPasswordBtn.CountAsync() > 0)
                {
                    Console.WriteLine($"[Playwright] QR-код обнаружен! Нажимаем 'Логин и пароль'...");
                    await loginPasswordBtn.ClickAsync();
                    await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                    Console.WriteLine($"[Playwright] Переключились на форму логина.");
                }
            }
            catch { }

            Console.WriteLine($"[Playwright] Вводим логин и пароль...");

            var loginInput = page.Locator("input[id='login']");
            await loginInput.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            await Task.Delay(1000);
            
            await loginInput.FillAsync(login);
            await page.FillAsync("input[id='password']", password);
            await page.PressAsync("input[id='password']", "Enter");
            
            Console.WriteLine($"[Playwright] Данные отправлены. Ожидаем ответ...");
            
            // Main loop: wait for redirect or error
            for (int i = 0; i < 60; i++)
            {
                await Task.Delay(2000);
                
                string url;
                try { url = page.Url; } catch { break; }
                Console.WriteLine($"[Playwright] Итерация {i + 1}. URL: {url}");
                
                // Read page text safely
                string pageText = "";
                try { pageText = await page.EvaluateAsync<string>("document.body?.innerText || ''") ?? ""; } catch { }

                // Wrong password
                if (pageText.Contains("Неверный") || pageText.Contains("не найден") || pageText.Contains("Неправильн"))
                {
                    Console.WriteLine("[Playwright] Ошибка: Неверный логин или пароль.");
                    session.ErrorMessage = "Неверный логин или пароль.";
                    session.Status = AuthStatus.Failed;
                    break;
                }

                // Captcha
                if (pageText.Contains("Введите символы") || url.Contains("captcha"))
                {
                    Console.WriteLine("[Playwright] Обнаружена капча!");
                    session.Status = AuthStatus.RequiresCaptcha;
                    break;
                }
                
                // 2FA / OTP
                if (pageText.Contains("Введите код") || url.Contains("otp") || url.Contains("login/mfa"))
                {
                    Console.WriteLine("[Playwright] Обнаружено окно 2FA!");
                    session.Status = AuthStatus.RequiresOtp;
                    break;
                }

                // Check if we're on the diary main page (NOT /login)
                if (url.Contains("dnevnik.egov66.ru") && !url.Contains("login"))
                {
                    Console.WriteLine($"[Playwright] Мы на главной дневника! Ждем полной загрузки страницы...");
                    
                    // Wait for the SPA to fully load and populate localStorage
                    try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle); } catch { }
                    Console.WriteLine($"[Playwright] NetworkIdle достигнут. Ждём 5 секунд для SPA...");
                    await Task.Delay(5000);
                    
                    // Now try reading localStorage multiple times
                    string? token = null;
                    string? refresh = null;
                    
                    for (int attempt = 1; attempt <= 10; attempt++)
                    {
                        try
                        {
                            token = await page.EvaluateAsync<string>("localStorage.getItem('Auth__token')");
                            refresh = await page.EvaluateAsync<string>("localStorage.getItem('Auth__refresh')");
                            Console.WriteLine($"[Playwright] Попытка {attempt}: token={(string.IsNullOrEmpty(token) || token == "null" ? "ПУСТО" : token.Substring(0, Math.Min(15, token.Length)) + "...")}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[Playwright] Попытка {attempt}: Ошибка JS: {ex.Message}");
                        }
                        
                        if (!string.IsNullOrEmpty(token) && token != "null") break;
                        await Task.Delay(2000);
                    }

                    // Clean up JSON quotes
                    if (!string.IsNullOrEmpty(token) && token.StartsWith("\"")) token = token.Trim('"');
                    if (!string.IsNullOrEmpty(refresh) && refresh.StartsWith("\"")) refresh = refresh.Trim('"');

                    if (!string.IsNullOrEmpty(token) && token != "null")
                    {
                        Console.WriteLine($"[Playwright] ТОКЕН ПОЛУЧЕН! Длина: {token.Length}");
                        session.AccessToken = token;
                        session.RefreshToken = refresh ?? "";
                        session.Status = AuthStatus.Success;
                    }
                    else
                    {
                        Console.WriteLine($"[Playwright] Токен так и не появился в localStorage!");
                        session.ErrorMessage = "Не удалось извлечь токен из дневника.";
                        session.Status = AuthStatus.Failed;
                    }
                    break;
                }
            }
            
            if (session.Status == AuthStatus.InProgress)
            {
                Console.WriteLine($"[Playwright] Таймаут авторизации!");
                session.ErrorMessage = "Таймаут авторизации.";
                session.Status = AuthStatus.Failed;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Playwright] ИСКЛЮЧЕНИЕ: {ex}");
            session.ErrorMessage = "Ошибка браузера: " + ex.Message;
            session.Status = AuthStatus.Failed;
        }
        
        Console.WriteLine($"[Playwright] Финальный статус: {session.Status}. Закрываем браузер через 3 секунды...");
        await Task.Delay(3000); // Даём Mini App время получить статус
        await CleanupSession(session);
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
