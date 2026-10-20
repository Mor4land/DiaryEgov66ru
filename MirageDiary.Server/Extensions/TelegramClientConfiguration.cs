using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;

namespace MirageDiary.Server.Extensions;

public static class TelegramClientConfiguration
{
    public static IServiceCollection AddConfiguredTelegramBot(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var botToken = (configuration["BotToken"] ?? "").Trim();
        if (string.IsNullOrEmpty(botToken) || botToken == "YOUR_BOT_TOKEN_HERE")
        {
            Console.WriteLine("[TelegramClient] ⚠️ BotToken is not configured. Registering local mock client.");
            services.AddSingleton<ITelegramBotClient>(new TelegramBotClient("123456789:AAGvuUzdEFRP2FXiZ7fgBU6tndmjAcWbA48"));
            return services;
        }

        string proxyUrl = (configuration["Telegram:Proxy"] ?? 
                           Environment.GetEnvironmentVariable("TELEGRAM_PROXY") ?? 
                           "").Trim();

        services.AddHttpClient("tgwebhook")
            .ConfigurePrimaryHttpMessageHandler(() =>
            {
                var handler = new SocketsHttpHandler
                {
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                    PooledConnectionIdleTimeout = TimeSpan.FromSeconds(45),
                    KeepAlivePingDelay = TimeSpan.FromSeconds(20),
                    KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
                    EnableMultipleHttp2Connections = true
                };

                // 1. Explicit proxy configuration (if defined in appsettings/env)
                if (!string.IsNullOrEmpty(proxyUrl))
                {
                    Console.WriteLine($"[TelegramClient] Using configured Telegram proxy: {proxyUrl}");
                    handler.Proxy = new WebProxy(proxyUrl);
                    handler.UseProxy = true;
                    return handler;
                }

                // 2. Non-blocking check for local SOCKS5 proxy (127.0.0.1:1080)
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
                    using var client = new TcpClient();
                    var connectTask = client.ConnectAsync("127.0.0.1", 1080, cts.Token).AsTask();
                    
                    if (connectTask.Wait(TimeSpan.FromMilliseconds(200)) && client.Connected)
                    {
                        Console.WriteLine("[TelegramClient] Local SOCKS5 proxy (socks5://127.0.0.1:1080) verified and enabled.");
                        handler.Proxy = new WebProxy("socks5://127.0.0.1:1080");
                        handler.UseProxy = true;
                    }
                }
                catch
                {
                    // Proxy not available; continue with direct connection without crashing or blocking
                }

                return handler;
            });

        services.AddSingleton<ITelegramBotClient>(sp =>
        {
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient("tgwebhook");
            httpClient.Timeout = TimeSpan.FromSeconds(70);
            return new TelegramBotClient(botToken, httpClient);
        });

        return services;
    }
}
