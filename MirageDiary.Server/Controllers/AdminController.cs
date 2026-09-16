using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MirageDiary.Server.Data;
using MirageDiary.Server.Models;
using MirageDiary.Server.Services;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using MAX.Bot.Interfaces;
using MAX.Bot.Interfaces.Models.Request.Message;

namespace MirageDiary.Server.Controllers;

[ApiController]
[Route("api/admin")]
public class AdminController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly MaintenanceModeService _maintenanceService;
    private readonly SharedClassroomCacheService _classroomCache;
    private readonly DatabaseBackupService _backupService;
    private readonly ServerMetricsService _metricsService;
    private readonly ServiceHealthService _health;
    private readonly ServerEventLog _eventLog;
    private readonly UserSyncService _userSyncService;
    private readonly IServiceProvider _serviceProvider;

    public AdminController(
        AppDbContext db,
        IConfiguration config,
        MaintenanceModeService maintenanceService,
        SharedClassroomCacheService classroomCache,
        DatabaseBackupService backupService,
        ServerMetricsService metricsService,
        ServiceHealthService health,
        ServerEventLog eventLog,
        UserSyncService userSyncService,
        IServiceProvider serviceProvider)
    {
        _db = db;
        _config = config;
        _maintenanceService = maintenanceService;
        _classroomCache = classroomCache;
        _backupService = backupService;
        _metricsService = metricsService;
        _health = health;
        _eventLog = eventLog;
        _userSyncService = userSyncService;
        _serviceProvider = serviceProvider;
    }

    private bool ValidateAdminKey()
    {
        string? expectedKey = Environment.GetEnvironmentVariable("ADMIN_KEY") ?? _config["Security:AdminKey"];
        if (string.IsNullOrEmpty(expectedKey))
        {
            // If AdminKey is not explicitly configured, access is disabled for safety
            return false;
        }

        string headerKey = Request.Headers["X-Admin-Key"].ToString();
        if (string.IsNullOrEmpty(headerKey))
        {
            string authHeader = Request.Headers.Authorization.ToString();
            if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                headerKey = authHeader.Substring("Bearer ".Length).Trim();
            }
        }

        if (string.IsNullOrEmpty(headerKey))
            return false;

        byte[] expectedBytes = System.Text.Encoding.UTF8.GetBytes(expectedKey);
        byte[] providedBytes = System.Text.Encoding.UTF8.GetBytes(headerKey);

        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }

    private IActionResult AdminUnauthorized() => Unauthorized(new SimpleSuccessResponse { Success = false, Error = "Недействительный Admin Key" });

    // ── STATS & DASHBOARD ─────────────────────────────────────────────────────
    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        if (!ValidateAdminKey()) return AdminUnauthorized();

        var totalUsers = await _db.Users.CountAsync();
        var activeSessions = await _db.Users.CountAsync(u => !string.IsNullOrEmpty(u.AccessToken));
        var cachedItems = await _db.Cache.CountAsync();
        var studentsCount = await _db.Users.CountAsync(u => u.Role == "Student");
        var parentsCount = await _db.Users.CountAsync(u => u.Role == "Parent");
        var teachersCount = await _db.Users.CountAsync(u => u.Role == "HeadTeacher");
        var adminsCount = await _db.Users.CountAsync(u => u.Role == "Admin");

        var isMaintenance = _maintenanceService.IsMaintenanceModeActive();
        string currentTgToken = _config["BotToken"] ?? "";
        string currentMaxToken = _config["MaxBotToken"] ?? "";

        return Ok(new AdminStatsResponse
        {
            TotalUsers = totalUsers,
            ActiveSessions = activeSessions,
            CachedItems = cachedItems,
            Roles = new AdminRolesStats
            {
                Students = studentsCount,
                Parents = parentsCount,
                Teachers = teachersCount,
                Admins = adminsCount
            },
            IsMaintenance = isMaintenance,
            TelegramBotToken = MaskToken(currentTgToken),
            MaxBotToken = MaskToken(currentMaxToken)
        });
    }

    // ── DYNAMIC TOKEN MANAGEMENT ──────────────────────────────────────────────
    [HttpGet("config")]
    public IActionResult GetConfig()
    {
        if (!ValidateAdminKey()) return AdminUnauthorized();

        return Ok(new AdminConfigResponse
        {
            BotToken = _config["BotToken"] ?? "",
            MaxBotToken = _config["MaxBotToken"] ?? "",
            MiniAppUrl = _config["MiniAppUrl"] ?? "",
            IsMaintenance = _maintenanceService.IsMaintenanceModeActive()
        });
    }

    [HttpPost("config/update-tokens")]
    public async Task<IActionResult> UpdateTokens([FromBody] UpdateTokensRequest req)
    {
        if (!ValidateAdminKey()) return AdminUnauthorized();

        try
        {
            string appSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!System.IO.File.Exists(appSettingsPath))
            {
                appSettingsPath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
            }

            if (System.IO.File.Exists(appSettingsPath))
            {
                string jsonText = await System.IO.File.ReadAllTextAsync(appSettingsPath);
                var jsonObj = JsonNode.Parse(jsonText) as JsonObject;
                if (jsonObj != null)
                {
                    if (!string.IsNullOrWhiteSpace(req.BotToken))
                    {
                        jsonObj["BotToken"] = req.BotToken.Trim();
                    }
                    if (!string.IsNullOrWhiteSpace(req.MaxBotToken))
                    {
                        jsonObj["MaxBotToken"] = req.MaxBotToken.Trim();
                    }

                    string updatedJson = jsonObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                    await System.IO.File.WriteAllTextAsync(appSettingsPath, updatedJson);
                }
            }

            _eventLog.Warning("Настройки", "Токены ботов перезаписаны в appsettings.json — требуется перезапуск сервера", Actor());

            // Клиенты ботов создаются один раз при старте, поэтому новый токен подхватится только после перезапуска.
            return Ok(new UpdateTokensResponse
            {
                Success = true,
                RequiresRestart = true,
                Message = "Токены сохранены в appsettings.json. Боты подхватят их после перезапуска сервера."
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new SimpleErrorResponse { Error = $"Ошибка обновления токенов: {ex.Message}" });
        }
    }

    // ── USER MANAGEMENT ───────────────────────────────────────────────────────
    [HttpGet("users")]
    public async Task<IActionResult> GetUsers([FromQuery] string? search = null)
    {
        if (!ValidateAdminKey()) return AdminUnauthorized();

        List<UserEntity> rawUsers;
        if (!string.IsNullOrWhiteSpace(search))
        {
            string s = search.Trim().ToLower();
            string cleanHandle = s.TrimStart('@');

            // Загружаем пользователей (EF Core прозрачно расшифровывает PII-поля) и фильтруем в памяти
            var allUsers = await _db.Users.ToListAsync();
            rawUsers = allUsers.Where(u =>
                (u.Id != null && u.Id.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
                (u.Username != null && u.Username.TrimStart('@').Contains(cleanHandle, StringComparison.OrdinalIgnoreCase)) ||
                (u.FirstName != null && u.FirstName.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
                (u.LastName != null && u.LastName.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
                (u.OrgName != null && u.OrgName.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
                (u.ClassName != null && u.ClassName.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
                (u.Role != null && u.Role.Contains(s, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(u => u.UpdatedAt)
                .Take(100)
                .ToList();
        }
        else
        {
            rawUsers = await _db.Users.OrderByDescending(u => u.UpdatedAt).Take(100).ToListAsync();
        }
        var users = rawUsers.Select(u => new UserListItemDto
        {
            Id = u.Id,
            Username = u.Username,
            FirstName = u.FirstName,
            LastName = u.LastName,
            Platform = u.Platform,
            LinkedUserId = u.LinkedUserId,
            Role = u.Role,
            OrgName = u.OrgName,
            ClassName = u.ClassName,
            IsAlarmEnabled = u.IsAlarmEnabled,
            SelectedStudentId = u.SelectedStudentId,
            HasSession = !string.IsNullOrEmpty(u.AccessToken),
            IsSubscribed = u.IsSubscribed,
            SubscriptionExpiresAtUtc = u.SubscriptionExpiresAtUtc?.ToString("yyyy-MM-dd HH:mm"),
            HasPremium = u.HasPremium()
        }).ToList();

        return Ok(users);
    }

    [HttpPost("users/update-role")]
    public async Task<IActionResult> UpdateUserRole([FromBody] UpdateRoleRequest req)
    {
        if (!ValidateAdminKey()) return AdminUnauthorized();

        var user = await _userSyncService.FindUserByIdOrUsernameAsync(req.UserId);
        if (user == null) return NotFound(new SimpleSuccessResponse { Success = false, Error = "Пользователь не найден" });

        string oldRole = user.Role;
        user.Role = req.NewRole;
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Propagate across platforms
        await _userSyncService.SyncUserAcrossPlatformsAsync(user);

        _eventLog.Info("Пользователи", $"Роль {user.Id} (@{user.Username}) изменена: {oldRole} → {user.Role}", Actor());

        return Ok(new UpdateRoleResponse { Success = true, Role = user.Role });
    }

    [HttpPost("users/update-subscription")]
    public async Task<IActionResult> UpdateUserSubscription([FromBody] UpdateSubscriptionRequest req)
    {
        if (!ValidateAdminKey()) return AdminUnauthorized();

        var user = await _userSyncService.FindUserByIdOrUsernameAsync(req.UserId);
        if (user == null) return NotFound(new SimpleSuccessResponse { Success = false, Error = "Пользователь не найден" });

        user.IsSubscribed = req.IsSubscribed;
        if (req.IsSubscribed)
        {
            user.SubscriptionExpiresAtUtc = req.Days > 0 ? DateTime.UtcNow.AddDays(req.Days) : null;
            _eventLog.Info("Подписки", $"Премиум выдан пользователю {user.Id} (@{user.Username}) на {(req.Days > 0 ? req.Days + " дн." : "бессрочно")}", Actor());
        }
        else
        {
            user.SubscriptionExpiresAtUtc = null;
            _eventLog.Warning("Подписки", $"Премиум отозван у пользователя {user.Id} (@{user.Username})", Actor());
        }

        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Propagate across platforms
        await _userSyncService.SyncUserAcrossPlatformsAsync(user);

        return Ok(new UpdateSubscriptionResponse
        {
            Success = true,
            IsSubscribed = user.IsSubscribed,
            HasPremium = user.HasPremium(),
            ExpiresAt = user.SubscriptionExpiresAtUtc?.ToString("yyyy-MM-dd HH:mm")
        });
    }

    [HttpPost("users/sync-all")]
    public async Task<IActionResult> SyncAllUsers()
    {
        if (!ValidateAdminKey()) return AdminUnauthorized();

        int count = await _userSyncService.SyncAllUsersAsync();
        _eventLog.Success("Синхронизация", $"Глобальная синхронизация аккаунтов выполнена: {count} связей обновлено", Actor());

        return Ok(new SyncAllUsersResponse { Success = true, SyncedLinks = count });
    }

    [HttpPost("users/refresh-sessions")]
    public async Task<IActionResult> RefreshAllSessions()
    {
        if (!ValidateAdminKey()) return AdminUnauthorized();

        var usersWithTokens = await _db.Users
            .Where(u => !string.IsNullOrEmpty(u.AccessToken) || !string.IsNullOrEmpty(u.RefreshToken))
            .CountAsync();

        var tokenRefreshService = _serviceProvider.GetServices<IHostedService>()
            .OfType<TokenRefreshBackgroundService>()
            .FirstOrDefault();

        if (tokenRefreshService == null)
        {
            return StatusCode(500, new SimpleErrorResponse { Error = "Фоновая служба обновления токенов не найдена." });
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await tokenRefreshService.RefreshAllTokensAsync();
                await _userSyncService.SyncAllUsersAsync();
            }
            catch (Exception ex)
            {
                _eventLog.Error("Сессии", $"Ошибка фонового обновления сессий: {ex.Message}", Actor());
            }
        });

        _eventLog.Info("Сессии", $"Администратор запустил принудительное обновление сессий для {usersWithTokens} пользователей", Actor());

        return Ok(new SessionRefreshResponse
        {
            Success = true,
            QueuedUsers = usersWithTokens,
            Message = $"Запущено фоновое обновление сессий для {usersWithTokens} пользователей"
        });
    }

    // ── BROADCAST MESSAGES ────────────────────────────────────────────────────
    [HttpPost("broadcast")]
    public async Task<IActionResult> SendBroadcast([FromBody] BroadcastRequest req)
    {
        if (!ValidateAdminKey()) return AdminUnauthorized();
        if (string.IsNullOrWhiteSpace(req.Text)) return BadRequest(new SimpleErrorResponse { Error = "Текст сообщения не может быть пустым" });

        var tgBot = _serviceProvider.GetService<ITelegramBotClient>();
        var maxBot = _serviceProvider.GetService<IMaxBotClient>();
        if (tgBot == null && maxBot == null) return StatusCode(500, new SimpleErrorResponse { Error = "Клиенты ботов не инициализированы" });

        var usersQuery = _db.Users.AsQueryable();
        if (!string.IsNullOrWhiteSpace(req.TargetRole) && req.TargetRole != "All")
        {
            usersQuery = usersQuery.Where(u => u.Role == req.TargetRole);
        }

        var targetUsers = await usersQuery.Select(u => u.Id).ToListAsync();
        int sentCount = 0;
        int errorCount = 0;

        foreach (var userId in targetUsers)
        {
            try
            {
                if (userId.StartsWith("max_"))
                {
                    if (long.TryParse(userId.Replace("max_", ""), out long maxChatId) && maxBot != null)
                    {
                        await maxBot.SendMessageAsync(new SendMessageRequest
                        {
                            UserId = maxChatId,
                            Text = req.Text,
                            Format = MessageFormat.Markdown
                        });
                        sentCount++;
                        await Task.Delay(50);
                    }
                }
                else if (long.TryParse(userId, out long chatId) && tgBot != null)
                {
                    try
                    {
                        await tgBot.SendMessage(chatId, req.Text, parseMode: ParseMode.Markdown);
                    }
                    catch
                    {
                        await tgBot.SendMessage(chatId, req.Text);
                    }
                    sentCount++;
                    // Throttling: Cap at ~25 msg/sec to protect from Telegram 429 Too Many Requests
                    await Task.Delay(40);
                }
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (ex.ErrorCode == 403 || ex.ErrorCode == 400)
            {
                // User blocked bot or deleted account
                errorCount++;
            }
            catch
            {
                errorCount++;
            }
        }

        string audience = req.TargetRole switch
        {
            "Student" => "ученикам",
            "Parent" => "родителям",
            "HeadTeacher" => "завучам",
            "Admin" => "администраторам",
            _ => "всем"
        };
        _eventLog.Info("Рассылка", $"Отправлено {audience}: доставлено {sentCount}, ошибок {errorCount} из {targetUsers.Count}", Actor());

        return Ok(new BroadcastSendResponse
        {
            Success = true,
            SentCount = sentCount,
            ErrorCount = errorCount,
            TotalTargeted = targetUsers.Count
        });
    }

    /// <summary>Сколько получателей затронет рассылка — считается до отправки, чтобы подтверждение не врало.</summary>
    [HttpGet("broadcast/audience")]
    public async Task<IActionResult> GetBroadcastAudience([FromQuery] string targetRole = "All")
    {
        if (!ValidateAdminKey()) return AdminUnauthorized();

        var usersQuery = _db.Users.AsQueryable();
        if (!string.IsNullOrWhiteSpace(targetRole) && targetRole != "All")
        {
            usersQuery = usersQuery.Where(u => u.Role == targetRole);
        }

        var ids = await usersQuery.Select(u => u.Id).ToListAsync();
        // Считаем так же, как считает сама отправка: Telegram (числовой ID) или MAX (max_...)
        var reachable = ids.Count(id => id.StartsWith("max_") || long.TryParse(id, out _));

        return Ok(new BroadcastAudienceResponse
        {
            TargetRole = targetRole,
            Total = ids.Count,
            Reachable = reachable
        });
    }

    // ── MAINTENANCE & CACHE ───────────────────────────────────────────────────
    [HttpPost("maintenance/toggle")]
    public IActionResult ToggleMaintenance([FromBody] MaintenanceToggleRequest req)
    {
        if (!ValidateAdminKey()) return AdminUnauthorized();

        const int maintenanceMinutes = 60;

        if (req.Enable)
        {
            _maintenanceService.ActivateMaintenanceMode(maintenanceMinutes);
            _eventLog.Warning("Техработы", $"Резервный режим включён на {maintenanceMinutes} мин: боты отдают данные из кэша класса", Actor());
        }
        else
        {
            _maintenanceService.DeactivateMaintenanceMode();
            _eventLog.Success("Техработы", "Резервный режим выключен, штатная работа восстановлена", Actor());
        }

        return Ok(new MaintenanceToggleResponse
        {
            Success = true,
            IsMaintenance = _maintenanceService.IsMaintenanceModeActive(),
            Minutes = maintenanceMinutes
        });
    }

    [HttpPost("cache/clear")]
    public async Task<IActionResult> ClearCache()
    {
        if (!ValidateAdminKey()) return AdminUnauthorized();

        _db.Cache.RemoveRange(_db.Cache);
        var cleared = await _db.SaveChangesAsync();

        _eventLog.Warning("Кэш класса", $"Кэш очищен вручную, удалено записей: {cleared}", Actor());

        return Ok(new ClearCacheResponse { Success = true, ClearedEntries = cleared });
    }

    // ── SYSTEM TELEMETRY & METRICS ──────────────────────────────────────────
    [HttpGet("telemetry")]
    public async Task<IActionResult> GetTelemetry()
    {
        if (!ValidateAdminKey()) return AdminUnauthorized();

        var process = Process.GetCurrentProcess();
        var uptimeSpan = DateTime.UtcNow - process.StartTime.ToUniversalTime();
        string uptimeFormatted = $"{(int)uptimeSpan.TotalDays}d {uptimeSpan.Hours}h {uptimeSpan.Minutes}m {uptimeSpan.Seconds}s";

        // Memory metrics
        long workingSetBytes = process.WorkingSet64;
        long privateMemoryBytes = process.PrivateMemorySize64;
        long gcMemoryBytes = GC.GetTotalMemory(false);

        // Database size
        string baseDir = AppContext.BaseDirectory;
        string dbPath = Path.Combine(baseDir, "miragediary.db");
        if (!System.IO.File.Exists(dbPath)) dbPath = "miragediary.db";
        long dbSizeBytes = System.IO.File.Exists(dbPath) ? new FileInfo(dbPath).Length : 0;

        // Backups info
        string backupDir = Path.Combine(baseDir, "backups");
        var backupFiles = Directory.Exists(backupDir)
            ? new DirectoryInfo(backupDir).GetFiles("miragediary_*.db").OrderByDescending(f => f.CreationTimeUtc).ToList()
            : new List<FileInfo>();

        var latestBackup = backupFiles.FirstOrDefault();

        // User and session statistics
        var totalUsers = await _db.Users.CountAsync();
        var activeSessions = await _db.Users.CountAsync(u => !string.IsNullOrEmpty(u.AccessToken));
        var mobileSessions = await _db.Users.CountAsync(u => !string.IsNullOrEmpty(u.Oid) && !string.IsNullOrEmpty(u.DeviceId));
        var cachedEntries = await _db.Cache.CountAsync();

        var studentsCount = await _db.Users.CountAsync(u => u.Role == "Student");
        var parentsCount = await _db.Users.CountAsync(u => u.Role == "Parent");
        var teachersCount = await _db.Users.CountAsync(u => u.Role == "HeadTeacher");
        var adminsCount = await _db.Users.CountAsync(u => u.Role == "Admin");

        var isMaintenance = _maintenanceService.IsMaintenanceModeActive();
        string currentTgToken = _config["BotToken"] ?? "";
        string currentMaxToken = _config["MaxBotToken"] ?? "";

        // Живая проверка отзывчивости SQLite вместо предположения, что база в порядке
        await ProbeDatabaseAsync();

        var timeline = _metricsService.GetTimeline(12);

        var responseDto = new TelemetryResponseDto
        {
            System = new SystemTelemetryDto
            {
                Os = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.OSArchitecture.ToString(),
                Runtime = RuntimeInformation.FrameworkDescription,
                ProcessId = process.Id,
                Uptime = uptimeFormatted,
                UptimeSeconds = (long)uptimeSpan.TotalSeconds,
                StartedAtUtc = process.StartTime.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                MemoryWorkingSetMb = Math.Round((double)workingSetBytes / (1024 * 1024), 2),
                MemoryPrivateMb = Math.Round((double)privateMemoryBytes / (1024 * 1024), 2),
                MemoryGcAllocatedMb = Math.Round((double)gcMemoryBytes / (1024 * 1024), 2),
                ThreadsCount = process.Threads.Count,
                GcGen0 = GC.CollectionCount(0),
                GcGen1 = GC.CollectionCount(1),
                GcGen2 = GC.CollectionCount(2)
            },
            Storage = new StorageTelemetryDto
            {
                DbSizeBytes = dbSizeBytes,
                DbSizeFormatted = FormatBytes(dbSizeBytes),
                BackupsCount = backupFiles.Count,
                LatestBackupName = latestBackup?.Name ?? "",
                LatestBackupSizeBytes = latestBackup?.Length,
                LatestBackupTimeUtc = latestBackup?.CreationTimeUtc.ToString("yyyy-MM-dd HH:mm:ss") ?? ""
            },
            Users = new UsersTelemetryDto
            {
                Total = totalUsers,
                ActiveSessions = activeSessions,
                MobileSessions = mobileSessions,
                LegacySessions = Math.Max(0, activeSessions - mobileSessions),
                Roles = new UserRolesDto
                {
                    Students = studentsCount,
                    Parents = parentsCount,
                    Teachers = teachersCount,
                    Admins = adminsCount
                }
            },
            Cache = new CacheTelemetryDto
            {
                CachedEntries = cachedEntries,
                IsMaintenance = isMaintenance
            },
            Integrations = new IntegrationsTelemetryDto
            {
                TelegramConfigured = !string.IsNullOrEmpty(currentTgToken) && currentTgToken != "YOUR_BOT_TOKEN_HERE",
                TelegramTokenMasked = MaskToken(currentTgToken),
                MaxConfigured = !string.IsNullOrEmpty(currentMaxToken) && currentMaxToken != "YOUR_MAX_BOT_TOKEN",
                MaxTokenMasked = MaskToken(currentMaxToken),
                TelegramCommandsHandled = _metricsService.TotalTelegramCommands
            },
            Services = new ServicesTelemetryDto
            {
                Overall = _health.Overall().ToString().ToLowerInvariant(),
                Components = _health.Snapshot().Select(c => new ServiceComponentDto
                {
                    Key = c.Key,
                    Name = c.Name,
                    State = c.State.ToString().ToLowerInvariant(),
                    RunningSinceUtc = c.RunningSinceUtc?.ToString("yyyy-MM-dd HH:mm:ss"),
                    LastSuccessUtc = c.LastSuccessUtc?.ToString("yyyy-MM-dd HH:mm:ss"),
                    LastFailureUtc = c.LastFailureUtc?.ToString("yyyy-MM-dd HH:mm:ss"),
                    LastError = c.LastError,
                    SuccessCount = c.SuccessCount,
                    FailureCount = c.FailureCount
                }).ToList()
            },
            Events = _eventLog.GetRecent(40).Select(e => new ServerEventDto
            {
                TimeUtc = e.TimestampUtc.ToString("yyyy-MM-dd HH:mm:ss"),
                Time = e.TimestampUtc.ToString("HH:mm:ss"),
                Level = e.Level.ToString().ToLowerInvariant(),
                Source = e.Source,
                Message = e.Message,
                Actor = e.Actor
            }).ToList(),
            EventLogStartedAtUtc = _eventLog.StartedAtUtc.ToString("yyyy-MM-dd HH:mm:ss"),
            Metrics = timeline,
            TrafficTimeline = timeline
        };

        return Ok(responseDto);
    }

    /// <summary>Проверяет, отвечает ли SQLite, и записывает результат в регистр состояния.</summary>
    private async Task ProbeDatabaseAsync()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await _db.Database.ExecuteSqlRawAsync("SELECT 1;");
            sw.Stop();
            _health.ReportSuccess("database", "SQLite");
        }
        catch (Exception ex)
        {
            sw.Stop();
            _health.ReportFailure("database", "SQLite", ex.Message);
            _eventLog.Error("SQLite", $"База не ответила на проверку: {ex.Message}");
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{Math.Round((double)bytes / (1024 * 1024 * 1024), 2)} ГБ";
        if (bytes >= 1024L * 1024) return $"{Math.Round((double)bytes / (1024 * 1024), 1)} МБ";
        if (bytes >= 1024) return $"{Math.Round((double)bytes / 1024, 1)} КБ";
        return $"{bytes} Б";
    }

    [HttpGet("metrics")]
    public IActionResult GetMetrics([FromQuery] int hours = 12)
    {
        if (!ValidateAdminKey()) return AdminUnauthorized();
        return Ok(_metricsService.GetTimeline(Math.Clamp(hours, 6, 24)));
    }

    // ── SYSTEM ACTIONS ───────────────────────────────────────────────────────
    [HttpPost("system/gc")]
    public IActionResult CollectGarbage()
    {
        if (!ValidateAdminKey()) return AdminUnauthorized();

        long memBefore = GC.GetTotalMemory(false);
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();
        long memAfter = GC.GetTotalMemory(true);

        long freed = Math.Max(0, memBefore - memAfter);
        _eventLog.Info("Память", $"Сборка мусора вручную: освобождено {Math.Round((double)freed / (1024 * 1024), 2)} МБ", Actor());

        return Ok(new GcCollectResponse
        {
            Success = true,
            FreedBytes = freed,
            FreedMb = Math.Round((double)freed / (1024 * 1024), 2),
            CurrentMemoryMb = Math.Round((double)memAfter / (1024 * 1024), 2)
        });
    }

    // ── BACKUP MANAGEMENT ────────────────────────────────────────────────────
    [HttpGet("backups")]
    public IActionResult GetBackupsList()
    {
        if (!ValidateAdminKey()) return AdminUnauthorized();

        string backupDir = Path.Combine(AppContext.BaseDirectory, "backups");
        if (!Directory.Exists(backupDir)) return Ok(new List<BackupFileInfoDto>());

        var files = new DirectoryInfo(backupDir)
            .GetFiles("miragediary_*.db")
            .OrderByDescending(f => f.CreationTimeUtc)
            .Select(f => new BackupFileInfoDto
            {
                FileName = f.Name,
                SizeBytes = f.Length,
                SizeFormatted = FormatBytes(f.Length),
                CreatedAtUtc = f.CreationTimeUtc.ToString("yyyy-MM-dd HH:mm:ss")
            })
            .ToList();

        return Ok(files);
    }

    [HttpPost("backups/create")]
    public async Task<IActionResult> TriggerBackup()
    {
        if (!ValidateAdminKey()) return AdminUnauthorized();

        var path = await _backupService.CreateBackupAsync("manual_admin");
        if (string.IsNullOrEmpty(path))
        {
            _eventLog.Error("Снимки БД", "Не удалось создать снимок базы", Actor());
            return StatusCode(500, new SimpleErrorResponse { Error = "Не удалось создать резервную копию базы данных." });
        }

        var fileInfo = new FileInfo(path);
        _eventLog.Success("Снимки БД", $"Создан снимок {fileInfo.Name} ({FormatBytes(fileInfo.Length)})", Actor());

        return Ok(new BackupCreateResponse
        {
            Success = true,
            FileName = fileInfo.Name,
            SizeBytes = fileInfo.Length,
            SizeFormatted = FormatBytes(fileInfo.Length)
        });
    }

    private static string MaskToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return "";
        // Короткий токен нельзя показывать целиком: маскируем полностью.
        if (token.Length < 12) return new string('•', 8);
        return token.Substring(0, 6) + "…" + token.Substring(token.Length - 4);
    }

    /// <summary>Кто выполнил действие. Ключ администратора общий, поэтому единственный честный признак — адрес.</summary>
    private string Actor()
    {
        var ip = HttpContext?.Connection?.RemoteIpAddress?.ToString();
        return string.IsNullOrEmpty(ip) ? "админ" : $"админ · {ip}";
    }
}

public class UpdateTokensRequest
{
    public string? BotToken { get; set; }
    public string? MaxBotToken { get; set; }
}

public class UpdateRoleRequest
{
    public string UserId { get; set; } = "";
    public string NewRole { get; set; } = "";
}

public class BroadcastRequest
{
    public string Text { get; set; } = "";
    public string TargetRole { get; set; } = "All";
}

public class BroadcastAudienceResponse
{
    public string TargetRole { get; set; } = "";
    public int Total { get; set; }
    public int Reachable { get; set; }
}

public class BroadcastSendResponse
{
    public bool Success { get; set; }
    public int SentCount { get; set; }
    public int ErrorCount { get; set; }
    public int TotalTargeted { get; set; }
}

public class MaintenanceToggleRequest
{
    public bool Enable { get; set; }
}
