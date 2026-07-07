using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MirageDiary.Core.Models;

public class HomeworkResponse
{
    [JsonPropertyName("date")]
    public string Date { get; set; }

    [JsonPropertyName("homeworks")]
    public List<HomeworkItem> Homeworks { get; set; }
}

public class HomeworkItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("date")]
    public string Date { get; set; }

    [JsonPropertyName("lessonName")]
    public string LessonName { get; set; }

    [JsonPropertyName("lessonNumber")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public int? LessonNumber { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; }

    [JsonPropertyName("isDone")]
    public bool IsDone { get; set; }

    [JsonPropertyName("homeWorkFiles")]
    public object HomeWorkFiles { get; set; }
    
    [JsonPropertyName("startTime")]
    public string StartTime { get; set; }
    
    [JsonPropertyName("endTime")]
    public string EndTime { get; set; }
    
    [JsonPropertyName("lessonId")]
    public string LessonId { get; set; }
}
