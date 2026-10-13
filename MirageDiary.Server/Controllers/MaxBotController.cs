using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MirageDiary.Server.Services;
using MaxUpdate = MAX.Bot.Interfaces.Models.Update;

namespace MirageDiary.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MaxBotController : ControllerBase
{
    private static readonly JsonSerializerOptions MaxJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowOutOfOrderMetadataProperties = true
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MaxBotController> _logger;
    private readonly IConfiguration _configuration;
    private readonly ServiceHealthService _health;

    public MaxBotController(
        IServiceScopeFactory scopeFactory,
        ILogger<MaxBotController> logger,
        IConfiguration configuration,
        ServiceHealthService health)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _configuration = configuration;
        _health = health;
    }

    [HttpGet]
    public IActionResult Get()
    {
        return Ok(new
        {
            status = "active",
            service = "MirageDiary MAX Bot Webhook",
            timestamp = DateTime.UtcNow
        });
    }

    [HttpPost]
    public async Task<IActionResult> Post()
    {
        // 1. Опциональная проверка секретного ключа Webhook
        var configuredSecret = (_configuration["MaxBot:Secret"] ?? _configuration["MaxBotSecret"])?.Trim();
        if (!string.IsNullOrEmpty(configuredSecret))
        {
            if (!Request.Headers.TryGetValue("X-Max-Bot-Api-Secret", out var headerSecret) ||
                !string.Equals(headerSecret.ToString(), configuredSecret, StringComparison.Ordinal))
            {
                _logger.LogWarning("[MaxWebhook] Отклонён запрос: неверный или отсутствующий заголовок X-Max-Bot-Api-Secret.");
                return Unauthorized();
            }
        }

        MaxUpdate? update;
        try
        {
            update = await JsonSerializer.DeserializeAsync<MaxUpdate>(Request.Body, MaxJsonOptions, HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MaxWebhook] Ошибка десериализации входящего пакета MAX");
            return Ok();
        }

        if (update == null)
        {
            return Ok();
        }

        _health.ReportRunning("max", "MAX-бот");

        // 2. Асинхронная обработка в изолированном ServiceScope (немедленный 200 OK шлюзу MAX)
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var handleUpdateService = scope.ServiceProvider.GetRequiredService<MaxCommandService>();
                await handleUpdateService.HandleUpdateAsync(update);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MaxWebhook] Ошибка при обработке входящего обновления MAX");
            }
        });

        return Ok();
    }
}
