using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace MirageDiary.Core;

public static class PlaywrightHttpFetcher
{
    private static IPlaywright? _playwright;
    private static readonly object _initLock = new object();

    private static async Task<IPlaywright> GetPlaywrightAsync()
    {
        if (_playwright == null)
        {
            _playwright = await Playwright.CreateAsync();
        }
        return _playwright;
    }

    public static async Task<(int StatusCode, string ResponseBody)> FetchAsync(
        string url, 
        string method = "GET", 
        string? cookieHeader = null, 
        string? jsonBody = null)
    {
        try
        {
            var pw = await GetPlaywrightAsync();
            var headers = new Dictionary<string, string>
            {
                { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36" },
                { "Accept", "application/json, text/plain, */*" },
                { "Accept-Language", "ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7" },
                { "Referer", "https://www.gosuslugi.ru/school" }
            };

            if (!string.IsNullOrEmpty(cookieHeader))
            {
                headers["Cookie"] = cookieHeader;
            }

            var requestContext = await pw.APIRequest.NewContextAsync(new APIRequestNewContextOptions
            {
                ExtraHTTPHeaders = headers
            });

            IAPIResponse response;
            if (method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                response = await requestContext.PostAsync(url, new APIRequestContextOptions
                {
                    Data = jsonBody
                });
            }
            else
            {
                response = await requestContext.GetAsync(url);
            }

            string text = await response.TextAsync();
            int status = response.Status;
            await requestContext.DisposeAsync();

            return (status, text);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PlaywrightHttpFetcher] Ошибка вызова {url}: {ex.Message}");
            return (500, "");
        }
    }
}
