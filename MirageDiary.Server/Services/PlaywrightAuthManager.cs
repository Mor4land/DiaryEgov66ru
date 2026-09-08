using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;
using MirageDiary.Core;

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
    public string MobileSessionId { get; set; } = "";
    public string Oid { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string ErrorMessage { get; set; } = "";
    public string CaptchaImageBase64 { get; set; } = "";
    
    public DateTime CreatedAt { get; } = DateTime.UtcNow;

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

public class PlaywrightAuthManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, AuthSession> _sessions = new();
    private readonly Microsoft.Extensions.Configuration.IConfiguration? _configuration;
    private static readonly SemaphoreSlim _browserSemaphore = new(3, 3);
    private readonly Timer _cleanupTimer;
    
    public PlaywrightAuthManager(Microsoft.Extensions.Configuration.IConfiguration? configuration = null)
    {
        _configuration = configuration;
        _cleanupTimer = new Timer(_ => CleanupExpiredSessions(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public async ValueTask DisposeAsync()
    {
        await _cleanupTimer.DisposeAsync();
        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync();
        }
        _sessions.Clear();
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

        _ = Task.Run(async () => await RunBrowserWorkflowAsync(session, login, password));

        return session;
    }

    private async Task RunBrowserWorkflowAsync(AuthSession session, string login, string password)
    {
        if (!await _browserSemaphore.WaitAsync(TimeSpan.FromSeconds(30)))
        {
            Console.WriteLine($"[Playwright] ⚠️ Слишком много активных сессий браузера. Отклонен запрос {session.TelegramUserId}");
            session.ErrorMessage = "Сервер временно перегружен запросами авторизации. Повторите через минуту.";
            session.Status = AuthStatus.Failed;
            return;
        }

        try
        {
            Console.WriteLine($"[Playwright] Запуск сессии Госуслуг для {session.TelegramUserId}...");
            try
            {
                session.PlaywrightInstance = await Playwright.CreateAsync();
                
                string? configuredPath = _configuration?["Playwright:ExecutablePath"] ?? Environment.GetEnvironmentVariable("PLAYWRIGHT_CHROME_PATH");
                string chromePath = !string.IsNullOrEmpty(configuredPath) ? configuredPath : System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "chrome", "chrome-win64", "chrome.exe"));
                if (!System.IO.File.Exists(chromePath))
                {
                    chromePath = System.IO.Path.GetFullPath(System.IO.Path.Combine(Environment.CurrentDirectory, "chrome", "chrome-win64", "chrome.exe"));
                }

                var launchOptions = new BrowserTypeLaunchOptions
                {
                    Headless = true,
                    Args = new[] 
                    { 
                        "--disable-blink-features=AutomationControlled",
                        "--no-sandbox",
                        "--disable-setuid-sandbox",
                        "--disable-dev-shm-usage",
                        "--disable-gpu"
                    }
                };

                if (System.IO.File.Exists(chromePath))
                {
                    launchOptions.ExecutablePath = chromePath;
                }

                session.Browser = await session.PlaywrightInstance.Chromium.LaunchAsync(launchOptions);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Playwright] Ошибка запуска Chromium: {ex}. Используйте прямую сессию.");
                session.ErrorMessage = $"Ошибка запуска браузера: {ex.Message}";
                session.Status = AuthStatus.Failed;
                return;
            }

            session.Context = await session.Browser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = MirageDiary.Core.UserAgentRotator.GetUserAgentForUser(session.TelegramUserId)
            });

            var page = await session.Context.NewPageAsync();
            Console.WriteLine($"[Playwright] Страница создана. Переход на Госуслуги (Школьный портал SSO)...");

            await page.GotoAsync("https://www.gosuslugi.ru/auth-provider/login?rUrl=/school", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            Console.WriteLine($"[Playwright] Страница входа открыта (URL: {page.Url}).");

            await EnsureLoginFormAsync(page);
            await CheckAndSkipMaxPromptAsync(page);

            login = (login ?? "").Trim();
            password = (password ?? "").Trim();

            var loginInput = page.Locator("input[id='login'], input[name='login'], input[type='text'], input[type='tel']").First;
            await loginInput.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });
            await loginInput.ClickAsync();
            await loginInput.FillAsync(login);
            await Task.Delay(250);
            
            var passInput = page.Locator("input[id='password'], input[name='password'], input[type='password']").First;
            await passInput.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });
            await passInput.ClickAsync();
            await passInput.FillAsync(password);
            await Task.Delay(300);
            
            var submitBtn = page.Locator("button[type='submit'], button.plain-button_wide, button.plain-button:has-text('Войти')").First;
            if (await submitBtn.CountAsync() > 0 && await submitBtn.IsVisibleAsync())
            {
                await submitBtn.ClickAsync();
            }
            else
            {
                await passInput.PressAsync("Enter");
            }
            
            await Task.Delay(1500);
            
            bool otpAlreadySubmitted = false;
            
            for (int i = 0; i < 200; i++)
            {
                await Task.Delay(300);
                
                string url;
                try { url = page.Url; } catch { break; }
                string urlLower = url.ToLowerInvariant();

                string pageText = "";
                try { pageText = await page.EvaluateAsync<string>("document.body?.innerText || ''") ?? ""; } catch { }
                string pageTextLower = pageText.ToLowerInvariant();

                // ── 1. WRONG PASSWORD / INVALID CREDENTIALS ──
                bool isAuthPage = urlLower.Contains("esia.gosuslugi.ru") || urlLower.Contains("/login");
                bool hasWrongCredsText = pageTextLower.Contains("неверный логин или пароль") || 
                                        pageTextLower.Contains("неправильный логин") || 
                                        pageTextLower.Contains("пароль не подходит") || 
                                        pageTextLower.Contains("учетная запись не найдена");

                if (isAuthPage && hasWrongCredsText)
                {
                    session.ErrorMessage = "Неверный логин или пароль Госуслуг.";
                    session.Status = AuthStatus.Failed;
                    break;
                }

                // ── 2. CAPTCHA ──
                if (pageTextLower.Contains("введите символы") || pageTextLower.Contains("символы с картинки") || urlLower.Contains("captcha"))
                {
                    Console.WriteLine("[Playwright] 🧩 Окно Капчи!");
                    try
                    {
                        var captchaImgEl = page.Locator("img[src*='captcha'], img[class*='captcha']").First;
                        if (await captchaImgEl.CountAsync() > 0 && await captchaImgEl.IsVisibleAsync())
                        {
                            var screenshot = await captchaImgEl.ScreenshotAsync();
                            session.CaptchaImageBase64 = Convert.ToBase64String(screenshot);
                        }
                    }
                    catch { }
                    session.Status = AuthStatus.RequiresCaptcha;
                    break;
                }
                
                // ── 3. 2FA / OTP (SMS / Gosklyuch) ──
                bool isAuthDomain = urlLower.Contains("esia.gosuslugi.ru") || urlLower.Contains("/login") || 
                                   urlLower.Contains("/aas/") || urlLower.Contains("/mfa") || urlLower.Contains("/otp");
                bool isAlreadyInCabinet = urlLower.Contains("lk.gosuslugi.ru") || urlLower.Contains("/school") || 
                                          urlLower.Contains("/settings/account") || urlLower.Contains("/profile") || 
                                          urlLower.Contains("/personal");

                bool hasExplicitOtpText = pageTextLower.Contains("введите код") || 
                                         pageTextLower.Contains("код подтверждения") || 
                                         pageTextLower.Contains("код из смс") || 
                                         pageTextLower.Contains("смс с кодом") || 
                                         pageTextLower.Contains("отправили смс") || 
                                         pageTextLower.Contains("отправили код") || 
                                         pageTextLower.Contains("госключ") || 
                                         pageTextLower.Contains("двухфакторная") || 
                                         pageTextLower.Contains("одноразовый код") || 
                                         pageTextLower.Contains("одноразовый пароль");

                bool isOtpScreen = !otpAlreadySubmitted && isAuthDomain && !isAlreadyInCabinet && 
                                   (hasExplicitOtpText || urlLower.Contains("/mfa") || urlLower.Contains("/otp") || urlLower.Contains("/confirm"));

                if (isOtpScreen)
                {
                    Console.WriteLine("[Playwright] 🔐 Обнаружено окно 2FA Госуслуг!");
                    session.Status = AuthStatus.RequiresOtp;
                    
                    session.UserInputTcs = new TaskCompletionSource<string>();
                    var otpCode = await session.UserInputTcs.Task;
                    
                    session.Status = AuthStatus.InProgress;
                    session.UserInputTcs = null;
                    otpAlreadySubmitted = true;

                    try 
                    {
                        var input = page.Locator("input[id='otp'], input[name='otp'], input[type='tel'], input[type='text'], input[type='number'], input[autocomplete='one-time-code']").First;
                        await input.FillAsync(otpCode);
                    }
                    catch 
                    {
                        await page.Keyboard.TypeAsync(otpCode);
                    }
                    
                    await page.Keyboard.PressAsync("Enter");
                    await Task.Delay(1000);
                    continue;
                }

                // ── 4. MAX PROMPT SKIPPING ──
                if (pageTextLower.Contains("мессенджер") || pageTextLower.Contains("узнать о max") || 
                    (pageTextLower.Contains("max") && pageTextLower.Contains("пропустить")))
                {
                    await CheckAndSkipMaxPromptAsync(page);
                    continue;
                }

                // ── 5. SSO HOP & SCHOOL LANDING CLICK ──
                if (urlLower.Contains("lk.gosuslugi.ru") || urlLower.Contains("esia.gosuslugi.ru/profile") || urlLower.Contains("/settings/account"))
                {
                    try
                    {
                        await page.GotoAsync("https://www.gosuslugi.ru/auth-provider/login?rUrl=/school", new PageGotoOptions { Timeout = 10000, WaitUntil = WaitUntilState.DOMContentLoaded });
                        await Task.Delay(1000);
                        url = page.Url;
                        urlLower = url.ToLowerInvariant();
                    }
                    catch { }
                }
                else if (urlLower.Contains("gosuslugi.ru/school") && (pageTextLower.Contains("войти «госуслуги моя школа»") || (pageTextLower.Contains("войти") && !pageTextLower.Contains("меню пользователя") && !pageTextLower.Contains("профиль"))))
                {
                    try
                    {
                        var schoolLoginBtn = page.Locator("a[href*='auth-provider'], button:has-text('Войти'), a:has-text('Войти')").First;
                        if (await schoolLoginBtn.CountAsync() > 0 && await schoolLoginBtn.First.IsVisibleAsync())
                        {
                            await schoolLoginBtn.ClickAsync();
                            await Task.Delay(1500);
                            url = page.Url;
                            urlLower = url.ToLowerInvariant();
                        }
                    }
                    catch { }
                }

                // ── 6. INSTANT SUCCESS DETECTION ──
                try
                {
                    var cookies = await session.Context.CookiesAsync();
                    var accCookie = cookies.FirstOrDefault(c => c.Domain.Contains("gosuslugi") && 
                        (c.Name == "acc_t" || c.Name == "acc_token") && 
                        !string.IsNullOrEmpty(c.Value) && c.Value.Length > 20);

                    bool isPendingAuthPage = (urlLower.Contains("esia.gosuslugi.ru/login") || 
                                              urlLower.Contains("/aas/oauth2/") || 
                                              urlLower.Contains("/mfa") || 
                                              urlLower.Contains("/otp")) && 
                                             !urlLower.Contains("lk.gosuslugi.ru") && 
                                             !urlLower.Contains("/school") &&
                                             !urlLower.Contains("/settings/account");

                    if (accCookie != null && !isPendingAuthPage)
                    {
                        // ── 7. MOBILE APP OAUTH TOKEN EXCHANGE ──
                        try
                        {
                            Console.WriteLine("[Playwright] 📱 Запуск получения постоянных токенов «Моя Школа»...");
                            var mobileClient = new MobileEsiaClient(new System.Net.Http.HttpClient());
                            var sign = await mobileClient.GetSignAsync();
                            if (sign != null && !string.IsNullOrEmpty(sign.Secret))
                            {
                                var (verifier, challenge) = MobileEsiaClient.GeneratePkce();
                                string androidId = BitConverter.ToString(MD5.HashData(Encoding.UTF8.GetBytes(session.TelegramUserId ?? Guid.NewGuid().ToString()))).Replace("-", "").Substring(0, 16).ToLower();
                                string entToken = MobileEsiaClient.BuildEntToken(androidId);
                                string mobileAuthUrl = mobileClient.BuildAuthUrl(sign.Secret, sign.Time, sign.StateMarker, challenge, entToken);
                                Console.WriteLine($"[Playwright] 📱 Auth URL сгенерирован: {mobileAuthUrl}");

                                string? capturedFatCode = null;

                                // ── Способ 1: Перехват через RouteAsync (гарантированно отлавливает gosuslugi-school:// в браузере) ──
                                var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

                                await session.Context.RouteAsync("gosuslugi-school://**", async route =>
                                {
                                    string routeUrl = route.Request.Url;
                                    Console.WriteLine($"[Playwright] 🎯 RouteAsync перехватил схему: {routeUrl}");
                                    try
                                    {
                                        var uri = new Uri(routeUrl);
                                        var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
                                        string? code = q["fat_code"] ?? q["code"];
                                        if (!string.IsNullOrEmpty(code))
                                        {
                                            tcs.TrySetResult(code);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine($"[Playwright] Ошибка парсинга URL в RouteAsync: {ex.Message}");
                                    }
                                    await route.AbortAsync();
                                });

                                // ── Способ 2: APIRequest напрямую с контекста с MaxRedirects = 0 ──
                                try
                                {
                                    var apiResp = await page.Context.APIRequest.GetAsync(mobileAuthUrl, new() { MaxRedirects = 0 });
                                    Console.WriteLine($"[Playwright] 🌐 APIRequest status: {apiResp.Status}");
                                    string? loc = null;
                                    if (apiResp.Headers != null)
                                    {
                                        foreach (var kvp in apiResp.Headers)
                                        {
                                            if (kvp.Key.Equals("location", StringComparison.OrdinalIgnoreCase))
                                            {
                                                loc = kvp.Value;
                                                break;
                                            }
                                        }
                                    }

                                    if (!string.IsNullOrEmpty(loc))
                                    {
                                        Console.WriteLine($"[Playwright] 🌐 APIRequest Location: {loc}");
                                        if (loc.Contains("fat_code=") || loc.Contains("code="))
                                        {
                                            var u = new Uri(loc);
                                            var q = System.Web.HttpUtility.ParseQueryString(u.Query);
                                            capturedFatCode = q["fat_code"] ?? q["code"];
                                            if (!string.IsNullOrEmpty(capturedFatCode))
                                            {
                                                Console.WriteLine($"[Playwright] 🎯 Fat-code получен через APIRequest: {capturedFatCode.Substring(0, Math.Min(10, capturedFatCode.Length))}...");
                                            }
                                        }
                                    }
                                    else
                                    {
                                        string text = await apiResp.TextAsync();
                                        Console.WriteLine($"[Playwright] 🌐 APIRequest text (len={text?.Length}): {text?.Substring(0, Math.Min(200, text?.Length ?? 0))}");
                                    }
                                }
                                catch (Exception apex)
                                {
                                    Console.WriteLine($"[Playwright] Ошибка APIRequest: {apex.Message}");
                                }

                                // ── Способ 3: Если APIRequest не получил код, навигируем саму страницу ──
                                if (string.IsNullOrEmpty(capturedFatCode))
                                {
                                    Console.WriteLine("[Playwright] 🌐 Переход на страницу mobileAuthUrl для перехвата редиректа браузером...");
                                    try
                                    {
                                        var navTask = page.GotoAsync(mobileAuthUrl, new PageGotoOptions { Timeout = 12000, WaitUntil = WaitUntilState.Commit });
                                        var completed = await Task.WhenAny(tcs.Task, navTask);
                                        if (tcs.Task.IsCompleted)
                                        {
                                            capturedFatCode = await tcs.Task;
                                        }
                                    }
                                    catch (Exception gex)
                                    {
                                        Console.WriteLine($"[Playwright] Навигация завершилась: {gex.Message}");
                                        if (tcs.Task.IsCompleted)
                                        {
                                            capturedFatCode = await tcs.Task;
                                        }
                                    }
                                }

                                try { await session.Context.UnrouteAsync("gosuslugi-school://**"); } catch { }

                                if (!string.IsNullOrEmpty(capturedFatCode))
                                {
                                    Console.WriteLine($"[Playwright] 🎯 Fat-code успешно захвачен ({capturedFatCode.Substring(0, Math.Min(10, capturedFatCode.Length))}...). Обмен на долгоживущие токены...");
                                    var tokens = await mobileClient.ExchangeFatCodeAsync(capturedFatCode, sign.Secret, sign.Time, sign.StateMarker, verifier, entToken);
                                    if (tokens != null && !string.IsNullOrEmpty(tokens.AccessToken))
                                    {
                                        Console.WriteLine($"[Playwright] 🔑 ExchangeFatCode успешен! Oid={tokens.Oid}, ExpiresIn={tokens.ExpiresIn}");
                                        var saveResult = await mobileClient.SaveTokenAsync(androidId, tokens.Oid, tokens.AccessToken, tokens.RefreshToken, tokens.ExpiresIn, entToken);
                                        if (saveResult != null && !string.IsNullOrEmpty(saveResult.AccessToken))
                                        {
                                            session.AccessToken = saveResult.AccessToken;
                                            session.RefreshToken = tokens.RefreshToken;
                                            session.MobileSessionId = saveResult.SessionId;
                                            session.Oid = tokens.Oid;
                                            session.DeviceId = androidId;
                                            session.Status = AuthStatus.Success;
                                            Console.WriteLine($"[Playwright] 🎉 ПОСТОЯННЫЙ МОБИЛЬНЫЙ ТОКЕН ПОЛУЧЕН (Oid={tokens.Oid}, SessionId={saveResult.SessionId})!");
                                            break;
                                        }
                                        else
                                        {
                                            Console.WriteLine("[Playwright] ❌ SaveToken вернул пустой результат!");
                                        }
                                    }
                                    else
                                    {
                                        Console.WriteLine("[Playwright] ❌ ExchangeFatCode вернул null!");
                                    }
                                }
                                else
                                {
                                    Console.WriteLine("[Playwright] ⚠️ Fat-code не был захвачен ни одним из способов.");
                                }
                            }
                            else
                            {
                                Console.WriteLine("[Playwright] ❌ Не удалось получить подпись mobileClient.GetSignAsync()!");
                            }
                        }
                        catch (Exception mex)
                        {
                            Console.WriteLine($"[Playwright] ⚠️ Ошибка мобильного обмена: {mex.Message}. Используем стандартную веб-сессию.");
                        }

                        // Fallback to web session cookies
                        string cookieHeader = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
                        session.AccessToken = cookieHeader;
                        session.RefreshToken = cookieHeader;
                        session.Status = AuthStatus.Success;
                        break;
                    }
                }
                catch { }
            }
            
            if (session.Status == AuthStatus.InProgress)
            {
                Console.WriteLine($"[Playwright] Таймаут авторизации.");
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
        finally
        {
            Console.WriteLine($"[Playwright] Финальный статус: {session.Status}. Закрываем браузер...");
            await CleanupSession(session);
            _browserSemaphore.Release();
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

    private async Task CheckAndSkipMaxPromptAsync(IPage page)
    {
        try
        {
            var skipElements = page.Locator("a:has-text('Пропустить'), button:has-text('Пропустить'), .plain-button-inline:has-text('Пропустить'), [role='button']:has-text('Пропустить'), span:has-text('Пропустить')");
            int count = await skipElements.CountAsync();
            for (int i = 0; i < count; i++)
            {
                var el = skipElements.Nth(i);
                if (await el.IsVisibleAsync())
                {
                    await el.ClickAsync(new LocatorClickOptions { Force = true, Timeout = 3000 });
                    await Task.Delay(500);
                    return;
                }
            }
        }
        catch { }
    }

    private async Task EnsureLoginFormAsync(IPage page)
    {
        for (int attempt = 1; attempt <= 30; attempt++)
        {
            try
            {
                var loginInput = page.Locator("input[id='login'], input[name='login'], input[type='text'], input[type='tel']");
                if (await loginInput.CountAsync() > 0 && await loginInput.First.IsVisibleAsync())
                {
                    return;
                }

                var passwordTabBtn = page.Locator("button[aria-label='Логин и пароль'], button:has-text('Логин и пароль'), button:has-text('Вход по паролю'), a:has-text('Вход по паролю')");
                if (await passwordTabBtn.CountAsync() > 0 && await passwordTabBtn.First.IsVisibleAsync())
                {
                    await passwordTabBtn.First.ClickAsync(new LocatorClickOptions { Force = true });
                    await Task.Delay(500);
                    continue;
                }
            }
            catch { }

            await Task.Delay(300);
        }
    }

    private async Task CleanupSession(AuthSession session)
    {
        await session.DisposeAsync();
    }
}
