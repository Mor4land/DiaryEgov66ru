using System;

namespace MirageDiary.Server.Models;

public class SchoolBellScheduleEntity
{
    public string OrgName { get; set; } = string.Empty;
    public string ScheduleJson { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
