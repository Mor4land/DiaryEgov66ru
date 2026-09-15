using System;
using System.Collections.Generic;
using MirageDiary.Server.Services;

namespace MirageDiary.Server.Models;

public class TelemetryResponseDto
{
    public SystemTelemetryDto System { get; set; } = new();
    public StorageTelemetryDto Storage { get; set; } = new();
    public UsersTelemetryDto Users { get; set; } = new();
    public CacheTelemetryDto Cache { get; set; } = new();
    public IntegrationsTelemetryDto Integrations { get; set; } = new();
    public ServicesTelemetryDto Services { get; set; } = new();
    public List<ServerEventDto> Events { get; set; } = new();
    public string EventLogStartedAtUtc { get; set; } = "";
    public TrafficTimelineDto Metrics { get; set; } = new();
    public TrafficTimelineDto TrafficTimeline { get; set; } = new();
}

public class SystemTelemetryDto
{
    public string Os { get; set; } = "";
    public string Architecture { get; set; } = "";
    public string Runtime { get; set; } = "";
    public int ProcessId { get; set; }
    public string StartedAtUtc { get; set; } = "";
    public long UptimeSeconds { get; set; }
    public string Uptime { get; set; } = "";
    public double MemoryWorkingSetMb { get; set; }
    public double MemoryPrivateMb { get; set; }
    public double MemoryGcAllocatedMb { get; set; }
    public int ThreadsCount { get; set; }
    public int GcGen0 { get; set; }
    public int GcGen1 { get; set; }
    public int GcGen2 { get; set; }
}

public class StorageTelemetryDto
{
    public long DbSizeBytes { get; set; }
    public string DbSizeFormatted { get; set; } = "";
    public int BackupsCount { get; set; }
    public string LatestBackupName { get; set; } = "";
    public long? LatestBackupSizeBytes { get; set; }
    public string LatestBackupTimeUtc { get; set; } = "";
}

public class UsersTelemetryDto
{
    public int Total { get; set; }
    public int ActiveSessions { get; set; }
    public int MobileSessions { get; set; }
    public int LegacySessions { get; set; }
    public UserRolesDto Roles { get; set; } = new();
}

public class UserRolesDto
{
    public int Students { get; set; }
    public int Parents { get; set; }
    public int Teachers { get; set; }
    public int Admins { get; set; }
}

public class CacheTelemetryDto
{
    public int CachedEntries { get; set; }
    public bool IsMaintenance { get; set; }
}

public class IntegrationsTelemetryDto
{
    public bool TelegramConfigured { get; set; }
    public string TelegramTokenMasked { get; set; } = "";
    public bool MaxConfigured { get; set; }
    public string MaxTokenMasked { get; set; } = "";
    public long TelegramCommandsHandled { get; set; }
}

public class ServicesTelemetryDto
{
    public string Overall { get; set; } = "";
    public List<ServiceComponentDto> Components { get; set; } = new();
}

public class ServiceComponentDto
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string State { get; set; } = "";
    public string? RunningSinceUtc { get; set; }
    public string? LastSuccessUtc { get; set; }
    public string? LastFailureUtc { get; set; }
    public string? LastError { get; set; }
    public long SuccessCount { get; set; }
    public long FailureCount { get; set; }
}

public class ServerEventDto
{
    public string TimeUtc { get; set; } = "";
    public string Time { get; set; } = "";
    public string Level { get; set; } = "";
    public string Source { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Actor { get; set; }
}

public class UserListItemDto
{
    public string Id { get; set; } = "";
    public string Username { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string Platform { get; set; } = "";
    public string LinkedUserId { get; set; } = "";
    public string Role { get; set; } = "";
    public string OrgName { get; set; } = "";
    public string ClassName { get; set; } = "";
    public bool IsAlarmEnabled { get; set; }
    public string? SelectedStudentId { get; set; }
    public bool HasSession { get; set; }
    public bool IsSubscribed { get; set; }
    public string? SubscriptionExpiresAtUtc { get; set; }
    public bool HasPremium { get; set; }
}

public class UpdateSubscriptionRequest
{
    public string UserId { get; set; } = "";
    public bool IsSubscribed { get; set; }
    public int Days { get; set; } = 30;
}

public class CheckAuthResponse
{
    public bool IsAuthorized { get; set; }
}

public class StartAuthResponse
{
    public string SessionId { get; set; } = "";
    public string Status { get; set; } = "";
}

public class AuthStatusResponse
{
    public string Status { get; set; } = "";
    public string? StudentId { get; set; }
    public string? ClassName { get; set; }
    public string? OrgName { get; set; }
    public string? CaptchaImage { get; set; }
    public string? ErrorMessage { get; set; }
}

public class SimpleSuccessResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }
}

public class UpdateSubscriptionResponse
{
    public bool Success { get; set; }
    public bool IsSubscribed { get; set; }
    public bool HasPremium { get; set; }
    public string? ExpiresAt { get; set; }
}

public class UpdateRoleResponse
{
    public bool Success { get; set; }
    public string Role { get; set; } = "";
}

public class GcCollectResponse
{
    public bool Success { get; set; } = true;
    public long FreedBytes { get; set; }
    public double FreedMb { get; set; }
    public double CurrentMemoryMb { get; set; }
}

public class SyncAllUsersResponse
{
    public bool Success { get; set; } = true;
    public int SyncedLinks { get; set; }
}

public class SessionRefreshResponse
{
    public bool Success { get; set; } = true;
    public int QueuedUsers { get; set; }
    public string Message { get; set; } = "";
}

public class AdminRolesStats
{
    public int Students { get; set; }
    public int Parents { get; set; }
    public int Teachers { get; set; }
    public int Admins { get; set; }
}

public class AdminStatsResponse
{
    public int TotalUsers { get; set; }
    public int ActiveSessions { get; set; }
    public int CachedItems { get; set; }
    public AdminRolesStats Roles { get; set; } = new();
    public bool IsMaintenance { get; set; }
    public string TelegramBotToken { get; set; } = "";
    public string MaxBotToken { get; set; } = "";
}

public class AdminConfigResponse
{
    public string BotToken { get; set; } = "";
    public string MaxBotToken { get; set; } = "";
    public string MiniAppUrl { get; set; } = "";
    public bool IsMaintenance { get; set; }
}

public class UpdateTokensResponse
{
    public bool Success { get; set; }
    public bool RequiresRestart { get; set; }
    public string Message { get; set; } = "";
}

public class MaintenanceToggleResponse
{
    public bool Success { get; set; } = true;
    public bool IsMaintenance { get; set; }
    public int Minutes { get; set; }
}

public class ClearCacheResponse
{
    public bool Success { get; set; } = true;
    public int ClearedEntries { get; set; }
}

public class BackupFileInfoDto
{
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public string SizeFormatted { get; set; } = "";
    public string CreatedAtUtc { get; set; } = "";
}

public class BackupCreateResponse
{
    public bool Success { get; set; } = true;
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public string SizeFormatted { get; set; } = "";
}

public class SimpleErrorResponse
{
    public string Error { get; set; } = "";
}

