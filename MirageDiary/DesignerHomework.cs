using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace MirageDiary
{
    public class HomeworkResponse
    {
        [JsonPropertyName("date")]
        public string Date { get; set; }

        [JsonPropertyName("homeworks")]
        public List<HomeworkItem> Homeworks { get; set; }
    }

    public class HomeworkItem
    {
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
        
    }
}
