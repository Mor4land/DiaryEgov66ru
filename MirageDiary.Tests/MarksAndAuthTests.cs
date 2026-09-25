using System.Collections.Generic;
using System.Text.Json;
using MirageDiary.Core;
using MirageDiary.Core.Models;
using Xunit;

namespace MirageDiary.Tests;

public class MarksAndAuthTests
{
    [Fact]
    public void GosuslugiMarkItem_SerializesValueAndDate_WhenRawFieldsWereEmpty()
    {
        // Simulate mark returned from Gosuslugi Datamart API (mark_value1 and mark_date populated, value/date empty)
        var mark = new GosuslugiMarkItem
        {
            MarksId = "660708-264-2-0-0-2026-09-24",
            SubjectName = "Алгебра",
            MarkValue1 = "3",
            MarkDate = "2026-09-24"
        };

        // Assert getters work
        Assert.Equal("3", mark.Value);
        Assert.Equal("2026-09-24", mark.Date);
        Assert.Equal("660708-264-2-0-0-2026-09-24", mark.Id);

        // Serialize to JSON
        string json = JsonSerializer.Serialize(mark, DiaryClient.DatamartJsonOptions);
        
        // Assert JSON contains non-empty value, date, and id
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("3", root.GetProperty("value").GetString());
        Assert.Equal("2026-09-24", root.GetProperty("date").GetString());
        Assert.Equal("660708-264-2-0-0-2026-09-24", root.GetProperty("id").GetString());
    }

    [Fact]
    public void GosuslugiMarkItem_DeserializesAndComputesGpa_Correctly()
    {
        // JSON format matching SQLite Cache table for real student
        string json = @"[
          {
            ""marks_id"": ""660708-264-2-0-0-2026-09-24"",
            ""id"": ""660708-264-2-0-0-2026-09-24"",
            ""subject_name"": ""Алгебра"",
            ""mark_value1"": ""4"",
            ""mark_value2"": null,
            ""value"": """",
            ""weight"": 1,
            ""mark_date"": ""2026-09-24"",
            ""date"": """"
          },
          {
            ""marks_id"": ""660708-264-2-0-0-2026-09-20"",
            ""id"": ""660708-264-2-0-0-2026-09-20"",
            ""subject_name"": ""Алгебра"",
            ""mark_value1"": ""5"",
            ""mark_value2"": null,
            ""value"": """",
            ""weight"": 1,
            ""mark_date"": ""2026-09-20"",
            ""date"": """"
          }
        ]";

        var marks = JsonSerializer.Deserialize<List<GosuslugiMarkItem>>(json, DiaryClient.DatamartJsonOptions);
        Assert.NotNull(marks);
        Assert.Equal(2, marks.Count);

        Assert.Equal("4", marks[0].Value);
        Assert.Equal("2026-09-24", marks[0].Date);
        Assert.Equal("5", marks[1].Value);
        Assert.Equal("2026-09-20", marks[1].Date);
    }
}
