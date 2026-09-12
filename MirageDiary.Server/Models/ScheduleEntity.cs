using System;

namespace MirageDiary.Server.Models;

public class ScheduleEntity
{
    public string OrgName { get; set; } = ""; // e.g. "МАОУ СОШ № 113"
    public string FileId { get; set; } = ""; // Telegram File ID
    public string FileType { get; set; } = ""; // "photo" or "document"
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}
