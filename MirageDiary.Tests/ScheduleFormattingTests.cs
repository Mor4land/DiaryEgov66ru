using System.Collections.Generic;
using System.Text.Json;
using MirageDiary.Core.Models;
using MirageDiary.Server.Services;
using Xunit;

namespace MirageDiary.Tests;

public class ScheduleFormattingTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void LessonItem_ThemeFallbacks_ParsesCorrectly()
    {
        // 1. Standard "theme"
        var json1 = "{\"subject_name\":\"Алгебра\",\"theme\":\"Квадратные уравнения\"}";
        var item1 = JsonSerializer.Deserialize<GosuslugiLessonItem>(json1, JsonOpts);
        Assert.NotNull(item1);
        Assert.Equal("Квадратные уравнения", item1.Theme);

        // 2. Fallback "lesson_theme"
        var json2 = "{\"subject_name\":\"Геометрия\",\"lesson_theme\":\"Теорема Пифагора\"}";
        var item2 = JsonSerializer.Deserialize<GosuslugiLessonItem>(json2, JsonOpts);
        Assert.NotNull(item2);
        Assert.Equal("Теорема Пифагора", item2.Theme);

        // 3. Fallback "topic"
        var json3 = "{\"subject_name\":\"Физика\",\"topic\":\"Законы Ньютона\"}";
        var item3 = JsonSerializer.Deserialize<GosuslugiLessonItem>(json3, JsonOpts);
        Assert.NotNull(item3);
        Assert.Equal("Законы Ньютона", item3.Theme);

        // 4. Fallback "subject_theme"
        var json4 = "{\"subject_name\":\"Химия\",\"subject_theme\":\"Окислительно-восстановительные реакции\"}";
        var item4 = JsonSerializer.Deserialize<GosuslugiLessonItem>(json4, JsonOpts);
        Assert.NotNull(item4);
        Assert.Equal("Окислительно-восстановительные реакции", item4.Theme);

        // 5. Fallback "plan_theme"
        var json5 = "{\"subject_name\":\"История\",\"plan_theme\":\"Реформы Петра I\"}";
        var item5 = JsonSerializer.Deserialize<GosuslugiLessonItem>(json5, JsonOpts);
        Assert.NotNull(item5);
        Assert.Equal("Реформы Петра I", item5.Theme);

        // 6. Fallback "theme_name"
        var json6 = "{\"subject_name\":\"Литература\",\"theme_name\":\"Евгений Онегин. Анализ главы 4\"}";
        var item6 = JsonSerializer.Deserialize<GosuslugiLessonItem>(json6, JsonOpts);
        Assert.NotNull(item6);
        Assert.Equal("Евгений Онегин. Анализ главы 4", item6.Theme);
    }

    [Fact]
    public void LessonItem_LessonTypeAndStatus_ParsesCorrectly()
    {
        // Cancelled lesson with type
        var jsonCancelled = "{\"subject_name\":\"Биология\",\"lesson_type\":\"Контрольная работа\",\"active\":false}";
        var itemCancelled = JsonSerializer.Deserialize<GosuslugiLessonItem>(jsonCancelled, JsonOpts);
        Assert.NotNull(itemCancelled);
        Assert.Equal("Контрольная работа", itemCancelled.LessonType);
        Assert.True(itemCancelled.IsCancelled);
        Assert.False(itemCancelled.IsReplacement);

        // Replacement lesson with type_value
        var jsonReplaced = "{\"subject_name\":\"Информатика\",\"type_value\":\"Лабораторная работа\",\"old_data\":{\"subject_name\":\"Труд\"}}";
        var itemReplaced = JsonSerializer.Deserialize<GosuslugiLessonItem>(jsonReplaced, JsonOpts);
        Assert.NotNull(itemReplaced);
        Assert.Equal("Лабораторная работа", itemReplaced.LessonType);
        Assert.False(itemReplaced.IsCancelled);
        Assert.True(itemReplaced.IsReplacement);
    }

    [Fact]
    public void FormatSchedule_IncludesThemeAndType_ProperlyEscaped()
    {
        var lessons = new List<GosuslugiLessonItem>
        {
            new()
            {
                SubjectName = "Алгебра",
                RawStartTime = "08:30",
                RawEndTime = "09:15",
                Room = "305",
                RawTheme = "Квадратные уравнения: a*x^2 + b*x + c = 0",
                RawLessonType = "Самостоятельная работа"
            },
            new()
            {
                SubjectName = "Физкультура",
                RawStartTime = "09:25",
                RawEndTime = "10:10",
                Room = "Спортзал",
                Active = false
            }
        };

        string formatted = DiaryInteractionService.FormatSchedule(lessons, "24.09.2026");

        Assert.Contains("📅 *Расписание на 24.09.2026:*", formatted);
        Assert.Contains("1. *Алгебра* `[08:30–09:15]` • каб. 305", formatted);
        Assert.Contains("🏷️ *Вид:* Самостоятельная работа", formatted);
        Assert.Contains("📖 *Тема:*", formatted);
        Assert.Contains("Квадратные уравнения", formatted);
        Assert.Contains("2. *Физкультура* `[09:25–10:10]` • каб. Спортзал ❌ *(Отменен)*", formatted);
    }
}
