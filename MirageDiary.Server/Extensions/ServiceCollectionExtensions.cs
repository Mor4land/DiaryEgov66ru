using System;
using System.Net.Http;
using System.Net.Security;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MirageDiary.Core;
using MirageDiary.Server.Services;
using MAX.Bot;
using MAX.Bot.Interfaces;

namespace MirageDiary.Server.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMirageServices(this IServiceCollection services, IConfiguration configuration)
    {
        // 1. Core Security & Token Services
        services.AddMemoryCache(options => { options.SizeLimit = 10000; });
        services.AddSingleton<ITokenEncryptionService, TokenEncryptionService>();
        services.AddSingleton<ITelegramInitDataValidator, TelegramInitDataValidator>();
        services.AddScoped<ITokenStore, DbTokenStore>();
        services.AddScoped<SharedClassroomCacheService>();

        // 2. Business Services
        services.AddScoped<RoleService>();
        services.AddScoped<UserSyncService>();
        services.AddScoped<DiaryBotWorkflowEngine>();
        services.AddScoped<DiaryInteractionService>();
        services.AddScoped<BotCommandService>();
        services.AddSingleton<BookmarkletAuthService>();
        services.AddSingleton<PlaywrightAuthManager>();
        services.AddSingleton<MaintenanceModeService>();
        services.AddSingleton<BotCaptchaService>();
        services.AddSingleton<BotStateService>();
        services.AddSingleton<SchoolBellScheduleService>();

        // 3. Background Workers & Automated Backups
        services.AddSingleton<ServerMetricsService>();
        services.AddSingleton<ServiceHealthService>();
        services.AddSingleton<ServerEventLog>();
        services.AddSingleton<DatabaseBackupService>();
        services.AddHostedService<DatabaseBackupWorker>();
        services.AddHostedService<SmartAlarmWorker>();
        services.AddHostedService<TelegramBotPollingWorker>();
        services.AddHostedService<TokenRefreshBackgroundService>();
        services.AddHostedService<GradeWatcherWorker>();
        services.AddHostedService<AccountCleanupWorker>();

        // 4. Rate Limiter
        services.AddMirageRateLimiting();

        // 5. Http Clients (with scoped Russian TLS validation instead of global disabling)
        services.AddMirageHttpClients();

        // 6. Telegram Bot
        services.AddConfiguredTelegramBot(configuration);

        // 7. MAX Bot (if configured)
        services.AddConfiguredMaxBot(configuration);

        return services;
    }

    public static IServiceCollection AddMirageHttpClients(this IServiceCollection services)
    {
        // Default unnamed HttpClient with secure Russian CA validation and 15s hard socket timeout
        services.AddHttpClient(string.Empty, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            ConnectTimeout = TimeSpan.FromSeconds(8),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
                {
                    var cert2 = cert as System.Security.Cryptography.X509Certificates.X509Certificate2;
                    return RussianTrustedCertificateValidator.Validate(null, cert2, chain, errors);
                }
            }
        });

        // DiaryClient with secure TLS validation scoped to Russian Ministry certificates and 15s hard socket timeout
        services.AddHttpClient<DiaryClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            ConnectTimeout = TimeSpan.FromSeconds(8),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
                {
                    var cert2 = cert as System.Security.Cryptography.X509Certificates.X509Certificate2;
                    return RussianTrustedCertificateValidator.Validate(null, cert2, chain, errors);
                }
            }
        });

        // HttpClient for diary reverse-proxy (isolated handler)
        services.AddHttpClient("diary-proxy")
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
                    {
                        var cert2 = cert as System.Security.Cryptography.X509Certificates.X509Certificate2;
                        return RussianTrustedCertificateValidator.Validate(null, cert2, chain, errors);
                    }
                }
            });

        return services;
    }

    public static IServiceCollection AddMirageRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddFixedWindowLimiter("auth-policy", opt =>
            {
                opt.Window = TimeSpan.FromMinutes(1);
                opt.PermitLimit = 5;
                opt.QueueLimit = 0;
            });
            options.AddFixedWindowLimiter("api-policy", opt =>
            {
                opt.Window = TimeSpan.FromMinutes(1);
                opt.PermitLimit = 60;
                opt.QueueLimit = 0;
            });
        });

        return services;
    }

    public static IServiceCollection AddConfiguredMaxBot(this IServiceCollection services, IConfiguration configuration)
    {
        var maxBotToken = (configuration["MaxBotToken"] ?? configuration["MaxBot:Token"])?.Trim();
        if (!string.IsNullOrEmpty(maxBotToken) && maxBotToken != "YOUR_MAX_BOT_TOKEN")
        {
            services.AddSingleton<IMaxBotClient>(sp =>
            {
                var handler = new SocketsHttpHandler
                {
                    PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                    ConnectTimeout = TimeSpan.FromSeconds(10),
                    SslOptions = new SslClientAuthenticationOptions
                    {
                        RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
                        {
                            var cert2 = cert as System.Security.Cryptography.X509Certificates.X509Certificate2;
                            return RussianTrustedCertificateValidator.Validate(null, cert2, chain, errors);
                        }
                    }
                };
                var maxHttpClient = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(45)
                };
                return new MaxBotClient(maxBotToken, maxHttpClient);
            });
            services.AddHostedService<MaxBotPollingWorker>();
            services.AddScoped<MaxCommandService>();
        }
        else
        {
            // Бот не настроен — это не сбой, и консоль должна показывать именно это,
            // а не «нет данных» и не «работает».
            services.AddHostedService<MaxBotNotConfiguredReporter>();
        }

        return services;
    }
}

/// <summary>Один раз при старте отмечает MAX-бота как ненастроенного, чтобы он был виден в консоли.</summary>
internal sealed class MaxBotNotConfiguredReporter : Microsoft.Extensions.Hosting.IHostedService
{
    private readonly ServiceHealthService _health;

    public MaxBotNotConfiguredReporter(ServiceHealthService health) => _health = health;

    public System.Threading.Tasks.Task StartAsync(System.Threading.CancellationToken cancellationToken)
    {
        _health.ReportNotConfigured("max", "MAX-бот");
        return System.Threading.Tasks.Task.CompletedTask;
    }

    public System.Threading.Tasks.Task StopAsync(System.Threading.CancellationToken cancellationToken)
        => System.Threading.Tasks.Task.CompletedTask;
}
