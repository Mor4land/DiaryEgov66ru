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
    
    // Additional Data for P2P Cache keys
    public string ClassName { get; set; } = ""; // e.g. "8Б"
    public string OrgName { get; set; } = ""; // e.g. "МАОУ СОШ № 113"
    
    // Role logic
    public string Role { get; set; } = "Student"; // Student, Admin, HeadTeacher

    // Settings
    public bool IsAlarmEnabled { get; set; } = true;
}
