using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace MirageDiary.Server.Services;

public static class PlaywrightSessionRefresher
{
    private static IPlaywright? _playwright;
    private static IBrowser? _browser;
    private static readonly object _lock = new object();

    private static string? ResolveChromeExecutablePath()
    {
        string? envPath = Environment.GetEnvironmentVariable("PLAYWRIGHT_CHROME_PATH");
        if (!string.IsNullOrEmpty(envPath) && System.IO.File.Exists(envPath))
            return envPath;

        string[] linuxCandidates = new[]
        {
            "/home/mlowers/.cache/ms-playwright/chromium-1228/chrome-linux64/chrome",
            "/home/mlowers/.cache/ms-playwright/chromium_headless_shell-1228/chrome-headless-shell-linux64/headless_shell",
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache/ms-playwright/chromium-1228/chrome-linux64/chrome")
        };

        foreach (var c in linuxCandidates)
        {
            if (System.IO.File.Exists(c)) return c;
        }

        string winPath = System.IO.Path.Combine(AppContext.BaseDirectory, "chrome", "chrome-win64", "chrome.exe");
        if (System.IO.File.Exists(winPath)) return winPath;

        return null;
    }

    private static async Task<IBrowser> GetBrowserAsync()
    {
        if (_playwright == null)
        {
            _playwright = await Playwright.CreateAsync();
        }

        if (_browser == null)
        {
            var options = new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = new[]
                {
                    "--disable-blink-features=AutomationControlled",
                    "--no-sandbox",
                    "--disable-setuid-sandbox",
                    "--disable-dev-shm-usage",
                    "--disable-accelerated-2d-canvas",
                    "--disable-gpu"
                }
            };

            string? execPath = ResolveChromeExecutablePath();
            if (!string.IsNullOrEmpty(execPath))
            {
                options.ExecutablePath = execPath;
            }

            _browser = await _playwright.Chromium.LaunchAsync(options);
        }

        return _browser;
    }

    /// <summary>
    /// Attempts to silently refresh expired Gosuslugi / ESIA cookies in the background
    /// by navigating to the Gosuslugi SSO portal with existing session cookies.
    /// </summary>
    public static async Task<string?> TryRefreshCookiesAsync(string rawCookieHeader)
    {
        if (string.IsNullOrWhiteSpace(rawCookieHeader))
            return null;

        IBrowserContext? context = null;
        try
        {
            var browser = await GetBrowserAsync();

            context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
                Locale = "ru-RU",
                TimezoneId = "Asia/Yekaterinburg"
            });

            // Parse raw cookie string into Playwright Cookie objects
            var cookieParts = rawCookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var playwrightCookies = new List<Cookie>();
            string oldAccT = "";

            foreach (var part in cookieParts)
            {
                int eqIdx = part.IndexOf('=');
                if (eqIdx > 0)
                {
                    string name = part[..eqIdx].Trim();
                    string val = part[(eqIdx + 1)..].Trim();

                    if (name.Equals("acc_t", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("acc_token", StringComparison.OrdinalIgnoreCase))
                    {
                        oldAccT = val;
                        // CRITICAL: Do not seed old acc_t so we only read newly issued tokens!
                        continue;
                    }

                    if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(val))
                    {
                        playwrightCookies.Add(new Cookie
                        {
                            Name = name,
                            Value = val,
                            Domain = ".gosuslugi.ru",
                            Path = "/"
                        });

                        playwrightCookies.Add(new Cookie
                        {
                            Name = name,
                            Value = val,
                            Domain = ".esia.gosuslugi.ru",
                            Path = "/"
                        });
                    }
                }
            }

            if (playwrightCookies.Count == 0)
                return null;

            await context.AddCookiesAsync(playwrightCookies);

            var page = await context.NewPageAsync();
            Console.WriteLine("[PlaywrightSessionRefresher] Запуск фонового продления сессии Госуслуг через SSO...");

            try
            {
                await page.GotoAsync("https://www.gosuslugi.ru/auth-provider/login?rUrl=/school", new PageGotoOptions
                {
                    Timeout = 25000,
                    WaitUntil = WaitUntilState.DOMContentLoaded
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PlaywrightSessionRefresher] ⚠️ Навигация на SSO: {ex.Message}");
            }

            await Task.Delay(4000);

            string currentUrl = "";
            try { currentUrl = page.Url; } catch { }

            if (currentUrl.Contains("esia.gosuslugi.ru/login") || currentUrl.Contains("/aas/oauth2/"))
            {
                Console.WriteLine("[PlaywrightSessionRefresher] ⚠️ Перенаправление на форму входа. Сессия ESIA завершена.");
                return null;
            }

            var currentCookies = await context.CookiesAsync();
            var accCookie = currentCookies.FirstOrDefault(c =>
                (c.Domain.Contains("gosuslugi") || c.Domain.Contains("esia")) &&
                (c.Name == "acc_t" || c.Name == "acc_token") &&
                !string.IsNullOrEmpty(c.Value) &&
                c.Value.Length > 20);

            if (accCookie != null && accCookie.Value != oldAccT)
            {
                string newCookieHeader = string.Join("; ", currentCookies.Select(c => $"{c.Name}={c.Value}"));
                Console.WriteLine($"[PlaywrightSessionRefresher] ✅ Сессия успешно продлена в фоне! (acc_t длина={accCookie.Value.Length})");
                return newCookieHeader;
            }
            else
            {
                Console.WriteLine("[PlaywrightSessionRefresher] ⚠️ Не удалось выпустить новый acc_t после обновления страницы.");
                return null;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PlaywrightSessionRefresher] ❌ Ошибка фонового обновления сессии: {ex.Message}");
            return null;
        }
        finally
        {
            if (context != null)
            {
                try { await context.CloseAsync(); } catch { }
            }
        }
    }
}
