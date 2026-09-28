using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MirageDiary.Core;
using MirageDiary.Server.Data;
using MirageDiary.Server.Models;

namespace MirageDiary.Server.Services;

/// <summary>
/// Service responsible for automatically synchronizing tokens, school info,
/// notification settings, subscription, and roles across platforms (Telegram, MAX bot, Web MiniApp).
/// </summary>
public class UserSyncService
{
    private static readonly ConcurrentDictionary<string, (string UserId, DateTime ExpiresAt)> _pendingLinkCodes = new(StringComparer.OrdinalIgnoreCase);

    private readonly AppDbContext _db;
    private readonly ITokenStore _tokenStore;
    private readonly ITokenEncryptionService _encryption;
    private readonly ILogger<UserSyncService> _logger;

    public UserSyncService(
        AppDbContext db,
        ITokenStore tokenStore,
        ITokenEncryptionService encryption,
        ILogger<UserSyncService> logger)
    {
        _db = db;
        _tokenStore = tokenStore;
        _encryption = encryption;
        _logger = logger;
    }

    /// <summary>
    /// Synchronizes all data from sourceUser to all other accounts sharing the same Oid, StudentId,
    /// LinkedUserId, or case-insensitive Username.
    /// </summary>
    public async Task<int> SyncUserAcrossPlatformsAsync(UserEntity sourceUser)
    {
        if (sourceUser == null || string.IsNullOrWhiteSpace(sourceUser.Id))
            return 0;

        try
        {
            string sourceId = sourceUser.Id.Trim();
            string cleanUsername = sourceUser.Username?.Trim().TrimStart('@') ?? "";
            string oid = sourceUser.Oid ?? "";
            string studentId = sourceUser.StudentId ?? "";
            string linkedId = sourceUser.LinkedUserId ?? "";

            // Query potential peer accounts (same individual across Telegram / MAX / MiniApp)
            var peers = await _db.Users
                .Where(u => u.Id != sourceId &&
                    (
                        (!string.IsNullOrEmpty(oid) && u.Oid == oid) ||
                        (!string.IsNullOrEmpty(linkedId) && u.Id == linkedId) ||
                        (u.LinkedUserId == sourceId) ||
                        (!string.IsNullOrEmpty(cleanUsername) && u.Username.ToLower() == cleanUsername.ToLower())
                    ))
                .ToListAsync();

            if (peers.Count == 0)
                return 0;

            int updatedCount = 0;
            foreach (var peer in peers)
            {
                bool peerModified = false;

                // 1. Establish mutual linkage if missing
                if (string.IsNullOrEmpty(peer.LinkedUserId) || peer.LinkedUserId != sourceId)
                {
                    peer.LinkedUserId = sourceId;
                    peerModified = true;
                }
                if (string.IsNullOrEmpty(sourceUser.LinkedUserId))
                {
                    sourceUser.LinkedUserId = peer.Id;
                    _db.Users.Update(sourceUser);
                }

                // 2. Synchronize Tokens and Session (if source has valid session)
                if (!string.IsNullOrEmpty(sourceUser.AccessToken))
                {
                    if (peer.AccessToken != sourceUser.AccessToken || peer.RefreshToken != sourceUser.RefreshToken)
                    {
                        peer.AccessToken = sourceUser.AccessToken ?? "";
                        peer.RefreshToken = sourceUser.RefreshToken ?? "";
                        peer.SessionId = sourceUser.SessionId ?? "";
                        peer.DeviceId = sourceUser.DeviceId ?? "";
                        peer.Oid = sourceUser.Oid ?? "";
                        peer.StudentId = sourceUser.StudentId ?? "";
                        peerModified = true;
                    }
                }
                else if (string.IsNullOrEmpty(sourceUser.AccessToken) && !string.IsNullOrEmpty(peer.AccessToken))
                {
                    // Reverse copy if source has empty token but peer has active token
                    sourceUser.AccessToken = peer.AccessToken ?? "";
                    sourceUser.RefreshToken = peer.RefreshToken ?? "";
                    sourceUser.SessionId = peer.SessionId ?? "";
                    sourceUser.DeviceId = peer.DeviceId ?? "";
                    sourceUser.Oid = peer.Oid ?? "";
                    sourceUser.StudentId = peer.StudentId ?? "";
                    _db.Users.Update(sourceUser);
                }

                // 3. Synchronize School / Class Metadata
                if (!string.IsNullOrEmpty(sourceUser.ClassName) && peer.ClassName != sourceUser.ClassName)
                {
                    peer.ClassName = sourceUser.ClassName;
                    peerModified = true;
                }
                if (!string.IsNullOrEmpty(sourceUser.OrgName) && peer.OrgName != sourceUser.OrgName)
                {
                    peer.OrgName = sourceUser.OrgName;
                    peerModified = true;
                }
                if (peer.Shift != sourceUser.Shift)
                {
                    peer.Shift = sourceUser.Shift;
                    peerModified = true;
                }
                if (!string.IsNullOrEmpty(sourceUser.SelectedStudentId) && peer.SelectedStudentId != sourceUser.SelectedStudentId)
                {
                    peer.SelectedStudentId = sourceUser.SelectedStudentId;
                    peerModified = true;
                }

                // 4. Synchronize Notification & Smart Alarm Settings
                if (peer.IsAlarmEnabled != sourceUser.IsAlarmEnabled)
                {
                    peer.IsAlarmEnabled = sourceUser.IsAlarmEnabled;
                    peerModified = true;
                }
                if (peer.IsScheduleReminderEnabled != sourceUser.IsScheduleReminderEnabled)
                {
                    peer.IsScheduleReminderEnabled = sourceUser.IsScheduleReminderEnabled;
                    peerModified = true;
                }
                if (peer.ScheduleReminderTime != sourceUser.ScheduleReminderTime)
                {
                    peer.ScheduleReminderTime = sourceUser.ScheduleReminderTime;
                    peerModified = true;
                }
                if (peer.IsHomeworkReminderEnabled != sourceUser.IsHomeworkReminderEnabled)
                {
                    peer.IsHomeworkReminderEnabled = sourceUser.IsHomeworkReminderEnabled;
                    peerModified = true;
                }
                if (peer.HomeworkReminderTime != sourceUser.HomeworkReminderTime)
                {
                    peer.HomeworkReminderTime = sourceUser.HomeworkReminderTime;
                    peerModified = true;
                }
                if (peer.IsEquipmentReminderEnabled != sourceUser.IsEquipmentReminderEnabled)
                {
                    peer.IsEquipmentReminderEnabled = sourceUser.IsEquipmentReminderEnabled;
                    peerModified = true;
                }
                if (peer.EquipmentReminderTime != sourceUser.EquipmentReminderTime)
                {
                    peer.EquipmentReminderTime = sourceUser.EquipmentReminderTime;
                    peerModified = true;
                }
                if (peer.IsGradeWatcherEnabled != sourceUser.IsGradeWatcherEnabled)
                {
                    peer.IsGradeWatcherEnabled = sourceUser.IsGradeWatcherEnabled;
                    peerModified = true;
                }
                if (peer.UtcOffsetHours != sourceUser.UtcOffsetHours)
                {
                    peer.UtcOffsetHours = sourceUser.UtcOffsetHours;
                    peerModified = true;
                }
                if (!string.IsNullOrEmpty(sourceUser.NotificationChannel) && peer.NotificationChannel != sourceUser.NotificationChannel)
                {
                    peer.NotificationChannel = sourceUser.NotificationChannel;
                    peerModified = true;
                }
                else if (string.IsNullOrEmpty(sourceUser.NotificationChannel) && !string.IsNullOrEmpty(peer.NotificationChannel))
                {
                    sourceUser.NotificationChannel = peer.NotificationChannel;
                    _db.Users.Update(sourceUser);
                }

                // 5. Synchronize Role (elevate peer if source is Admin or HeadTeacher)
                if (sourceUser.Role == "Admin" && peer.Role != "Admin")
                {
                    peer.Role = "Admin";
                    peerModified = true;
                }
                else if (sourceUser.Role == "HeadTeacher" && peer.Role != "HeadTeacher" && peer.Role != "Admin")
                {
                    peer.Role = "HeadTeacher";
                    peerModified = true;
                }
                else if (peer.Role == "Admin" && sourceUser.Role != "Admin")
                {
                    sourceUser.Role = "Admin";
                    _db.Users.Update(sourceUser);
                }

                // 6. Synchronize Premium Subscription
                if (sourceUser.IsSubscribed && !peer.IsSubscribed)
                {
                    peer.IsSubscribed = true;
                    peer.SubscriptionExpiresAtUtc = sourceUser.SubscriptionExpiresAtUtc;
                    peerModified = true;
                }
                else if (peer.IsSubscribed && !sourceUser.IsSubscribed)
                {
                    sourceUser.IsSubscribed = true;
                    sourceUser.SubscriptionExpiresAtUtc = peer.SubscriptionExpiresAtUtc;
                    _db.Users.Update(sourceUser);
                }

                // 7. Synchronize Privacy Policy Consent (152-FZ)
                if (sourceUser.HasAcceptedPrivacyPolicy && !peer.HasAcceptedPrivacyPolicy)
                {
                    peer.HasAcceptedPrivacyPolicy = true;
                    peer.PrivacyPolicyAcceptedAtUtc = sourceUser.PrivacyPolicyAcceptedAtUtc ?? DateTime.UtcNow;
                    peerModified = true;
                }
                else if (!sourceUser.HasAcceptedPrivacyPolicy && peer.HasAcceptedPrivacyPolicy)
                {
                    sourceUser.HasAcceptedPrivacyPolicy = true;
                    sourceUser.PrivacyPolicyAcceptedAtUtc = peer.PrivacyPolicyAcceptedAtUtc ?? DateTime.UtcNow;
                    _db.Users.Update(sourceUser);
                }

                // 8. Synchronize Anti-Bot & Onboarding flags
                if (sourceUser.HasPassedCaptcha && !peer.HasPassedCaptcha)
                {
                    peer.HasPassedCaptcha = true;
                    peerModified = true;
                }
                else if (!sourceUser.HasPassedCaptcha && peer.HasPassedCaptcha)
                {
                    sourceUser.HasPassedCaptcha = true;
                    _db.Users.Update(sourceUser);
                }

                if (sourceUser.HasCompletedOnboarding && !peer.HasCompletedOnboarding)
                {
                    peer.HasCompletedOnboarding = true;
                    peerModified = true;
                }
                else if (!sourceUser.HasCompletedOnboarding && peer.HasCompletedOnboarding)
                {
                    sourceUser.HasCompletedOnboarding = true;
                    _db.Users.Update(sourceUser);
                }

                if (peerModified)
                {
                    peer.UpdatedAt = DateTime.UtcNow;
                    _db.Users.Update(peer);
                    updatedCount++;
                }
            }

            sourceUser.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            _logger.LogInformation(
                "[UserSyncService] Synchronized user {SourceId} across {Count} peer accounts.",
                sourceId,
                updatedCount);

            return updatedCount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[UserSyncService] Error during user sync for {UserId}", sourceUser.Id);
            return 0;
        }
    }

    /// <summary>
    /// Searches for a user by numeric ID, @username, username without @, or full name.
    /// </summary>
    public async Task<UserEntity?> FindUserByIdOrUsernameAsync(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier)) return null;

        string clean = identifier.Trim();
        string cleanHandle = clean.TrimStart('@');

        // 1. Direct ID match
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == clean);
        if (user != null) return user;

        // 2. Match with "max_" prefix
        user = await _db.Users.FirstOrDefaultAsync(u => u.Id == $"max_{clean}");
        if (user != null) return user;

        // 3. Username match
        user = await _db.Users.FirstOrDefaultAsync(u =>
            u.Username.ToLower() == cleanHandle.ToLower() ||
            u.Username.ToLower() == ("@" + cleanHandle).ToLower());
        if (user != null) return user;

        // 4. Name match (по расшифрованным в памяти полям PII)
        var allUsers = await _db.Users.ToListAsync();
        user = allUsers.FirstOrDefault(u =>
            (!string.IsNullOrEmpty(u.FirstName) && !string.IsNullOrEmpty(u.LastName) && (u.FirstName + " " + u.LastName).Contains(clean, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(u.FirstName) && u.FirstName.Equals(clean, StringComparison.OrdinalIgnoreCase)));

        return user;
    }

    /// <summary>
    /// Clusters all users in the database by Oid and StudentId and syncs data bidirectionally.
    /// </summary>
    public async Task<int> SyncAllUsersAsync()
    {
        try
        {
            var allUsers = await _db.Users.ToListAsync();
            int totalSynced = 0;

            // Cluster by Oid
            var oidGroups = allUsers
                .Where(u => !string.IsNullOrEmpty(u.Oid))
                .GroupBy(u => u.Oid)
                .Where(g => g.Count() > 1);

            foreach (var group in oidGroups)
            {
                var primary = group.OrderByDescending(u => !string.IsNullOrEmpty(u.AccessToken))
                                   .ThenByDescending(u => u.IsSubscribed)
                                   .First();

                foreach (var other in group)
                {
                    if (other.Id != primary.Id)
                    {
                        await SyncUserAcrossPlatformsAsync(primary);
                        totalSynced++;
                    }
                }
            }

            _logger.LogInformation("[UserSyncService] Full database synchronization finished. {Count} links updated.", totalSynced);
            return totalSynced;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[UserSyncService] Error during SyncAllUsersAsync");
            return 0;
        }
    }

    /// <summary>
    /// Generates a short 6-digit link code tied to the user, valid for 15 minutes.
    /// </summary>
    public string GenerateLinkCode(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("UserId cannot be empty", nameof(userId));

        // Purge expired codes
        var now = DateTime.UtcNow;
        foreach (var kvp in _pendingLinkCodes)
        {
            if (kvp.Value.ExpiresAt < now)
            {
                _pendingLinkCodes.TryRemove(kvp.Key, out _);
            }
        }

        // Clean up any existing code for this user
        foreach (var kvp in _pendingLinkCodes)
        {
            if (string.Equals(kvp.Value.UserId, userId, StringComparison.OrdinalIgnoreCase))
            {
                _pendingLinkCodes.TryRemove(kvp.Key, out _);
            }
        }

        string code = RandomNumberGenerator.GetInt32(100000, 999999).ToString();
        _pendingLinkCodes[code] = (userId.Trim(), now.AddMinutes(15));
        return code;
    }

    /// <summary>
    /// Links currentUserId to the account that generated the link code.
    /// Performs mutual linking and bidirectional synchronization of tokens, settings, and profile info.
    /// </summary>
    public async Task<(bool Success, string Message, UserEntity? Peer)> TryLinkAccountsByCodeAsync(string currentUserId, string code)
    {
        if (string.IsNullOrWhiteSpace(currentUserId) || string.IsNullOrWhiteSpace(code))
            return (false, "Укажите код связки.", null);

        string cleanCode = code.Trim();
        if (!_pendingLinkCodes.TryGetValue(cleanCode, out var entry))
            return (false, "Неверный код связки. Проверьте правильность или сгенерируйте новый.", null);

        if (entry.ExpiresAt < DateTime.UtcNow)
        {
            _pendingLinkCodes.TryRemove(cleanCode, out _);
            return (false, "Срок действия кода связки истёк (15 минут). Запросите новый код.", null);
        }

        string sourceUserId = entry.UserId;
        if (string.Equals(sourceUserId, currentUserId.Trim(), StringComparison.OrdinalIgnoreCase))
            return (false, "Нельзя связать аккаунт с самим собой.", null);

        _pendingLinkCodes.TryRemove(cleanCode, out _);

        var currentUser = await _db.Users.FirstOrDefaultAsync(u => u.Id == currentUserId.Trim());
        var peerUser = await _db.Users.FirstOrDefaultAsync(u => u.Id == sourceUserId);

        if (currentUser == null || peerUser == null)
            return (false, "Один из связываемых аккаунтов не найден в базе данных.", null);

        // Mutually link IDs
        currentUser.LinkedUserId = peerUser.Id;
        peerUser.LinkedUserId = currentUser.Id;

        // Choose primary source: prioritize the account with valid active access token
        UserEntity primary = !string.IsNullOrEmpty(peerUser.AccessToken) ? peerUser : currentUser;
        await SyncUserAcrossPlatformsAsync(primary);

        _logger.LogInformation("[UserSyncService] Successfully linked accounts {UserA} <-> {UserB} using link code.", currentUserId, sourceUserId);

        return (true, "Аккаунты успешно связаны!", peerUser);
    }
}
