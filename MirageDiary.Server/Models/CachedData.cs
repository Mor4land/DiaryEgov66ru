using System;

namespace MirageDiary.Server.Models;

public class CachedData
{
    // For example: "HW_Class_8Б" or "Grades_StudentID_123"
    public string CacheKey { get; set; } = "";
    
    // The actual JSON response from MirageDiary
    public string JsonData { get; set; } = "";
    
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
