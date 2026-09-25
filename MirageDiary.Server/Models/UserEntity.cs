using System;

namespace MirageDiary.Server.Models;

public class UserEntity
{
    // Telegram User ID
    public string Id { get; set; } = "";
    
    // MirageDiary Tokens
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string StudentId { get; set; } = "";
    public string Oid { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    
    // Additional Data for P2P Cache keys
    public string ClassName { get; set; } = ""; // e.g. "8Б"
    public string OrgName { get; set; } = ""; // e.g. "МАОУ СОШ № 113"
    
    // Role logic
    public string Role { get; set; } = "Student"; // Student, Admin, HeadTeacher

    // Settings
    public bool IsAlarmEnabled { get; set; } = true; // Legacy toggle

    // Smart Alarm & Notification Settings
    public bool IsScheduleReminderEnabled { get; set; } = true;
    public string ScheduleReminderTime { get; set; } = "07:30"; // "HH:mm"
    public string LastScheduleReminderDate { get; set; } = "";

    public bool IsHomeworkReminderEnabled { get; set; } = true;
    public string HomeworkReminderTime { get; set; } = "17:00"; // "HH:mm"
    public string LastHomeworkReminderDate { get; set; } = "";

    public bool IsEquipmentReminderEnabled { get; set; } = true;
    public string EquipmentReminderTime { get; set; } = "20:30"; // "HH:mm"
    public string LastEquipmentReminderDate { get; set; } = "";

    public int UtcOffsetHours { get; set; } = 5;

    // Selected Child ID for Parent Role
    public string SelectedStudentId { get; set; } = "";

    // Subscription & Live Grade Watcher
    public bool IsSubscribed { get; set; } = false;
    public DateTime? SubscriptionExpiresAtUtc { get; set; }
    public string LastMarksHash { get; set; } = "";
    public int Shift { get; set; } = 1; // 1 or 2

    // 152-FZ Privacy Policy & Anti-Bot Onboarding
    public bool HasAcceptedPrivacyPolicy { get; set; } = false;
    public DateTime? PrivacyPolicyAcceptedAtUtc { get; set; }
    public bool HasPassedCaptcha { get; set; } = false;
    public bool HasCompletedOnboarding { get; set; } = false;

    // Cross-platform Profile Metadata
    public string Username { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string Platform { get; set; } = "telegram"; // "telegram", "max", "miniapp"
    public string LinkedUserId { get; set; } = "";
    public DateTime? UpdatedAt { get; set; } = DateTime.UtcNow;

    public bool HasPremium()
    {
        if (Role == "Admin") return true;
        if (!IsSubscribed) return false;
        if (SubscriptionExpiresAtUtc == null) return true;
        return SubscriptionExpiresAtUtc.Value > DateTime.UtcNow;
    }
}
