using System;
using System.Linq;
using MirageDiary.Server.Services;
using Xunit;

namespace MirageDiary.Tests;

public class AlarmTriggerWordsTests
{
    [Fact]
    public void TriggerWords_ContainsExactTenSchoolEquipmentItems()
    {
        var expected = new[]
        {
            "циркуль", "линейка", "краски", "альбом", "форма",
            "транспортир", "контурные карты", "кисти", "карандаши", "тетрадь"
        };

        Assert.Equal(expected.Length, DiaryBotWorkflowEngine.TriggerWords.Length);
        foreach (var item in expected)
        {
            Assert.Contains(item, DiaryBotWorkflowEngine.TriggerWords);
        }
    }

    [Theory]
    [InlineData("Принести циркуль и линейка на урок геометрии", "циркуль", "линейка")]
    [InlineData("Взять с собой краски, альбом и кисти для ИЗО", "краски", "альбом", "кисти")]
    [InlineData("Спортивная форма на физкультуру в зал", "форма")]
    [InlineData("Подготовить контурные карты по географии", "контурные карты")]
    [InlineData("Чистая тетрадь в клетку и цветные карандаши", "тетрадь", "карандаши")]
    public void TriggerWords_MatchesInHomeworkText_WithoutRegex(string text, params string[] expectedMatches)
    {
        var normalized = text.ToLowerInvariant();
        var found = DiaryBotWorkflowEngine.TriggerWords
            .Where(w => normalized.Contains(w))
            .ToList();

        foreach (var exp in expectedMatches)
        {
            Assert.Contains(exp, found);
        }
    }

    [Fact]
    public void TriggerWords_NoMatchOnStandardSubjectsWithoutEquipment()
    {
        string text = "Русский язык: упр 42, литература: читать стр 15-20, история: параграф 5";
        var normalized = text.ToLowerInvariant();
        var found = DiaryBotWorkflowEngine.TriggerWords
            .Where(w => normalized.Contains(w))
            .ToList();

        Assert.Empty(found);
    }
}
