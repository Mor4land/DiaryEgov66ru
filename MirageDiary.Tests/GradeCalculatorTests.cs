using System.Collections.Generic;
using MirageDiary.Server.Services;
using Xunit;

namespace MirageDiary.Tests;

public class GradeCalculatorTests
{
    [Fact]
    public void CalculateGpa_ReturnsZero_WhenEmpty()
    {
        var (sum, totalW, gpa) = GradeCalculator.CalculateGpa(new List<(int, int)>());
        Assert.Equal(0, sum);
        Assert.Equal(0, totalW);
        Assert.Equal(0.0, gpa);
    }

    [Fact]
    public void CalculateGpa_StandardWeights_CalculatesCorrectAverage()
    {
        // 5 (w=1), 4 (w=1), 5 (w=2), 3 (w=1)
        // Sum = 5*1 + 4*1 + 5*2 + 3*1 = 5 + 4 + 10 + 3 = 22
        // Weight = 1 + 1 + 2 + 1 = 5
        // GPA = 22 / 5 = 4.40
        var marks = new List<(int, int)>
        {
            (5, 1),
            (4, 1),
            (5, 2),
            (3, 1)
        };

        var (sum, totalW, gpa) = GradeCalculator.CalculateGpa(marks);
        Assert.Equal(22, sum);
        Assert.Equal(5, totalW);
        Assert.Equal(4.40, gpa);
    }

    [Fact]
    public void CalculateRequiredFives_WhenGpaAlreadyAboveThreshold_ReturnsZero()
    {
        // 5 (w=1), 5 (w=1) -> GPA = 5.0 >= 4.60
        int needed = GradeCalculator.CalculateRequiredFives(10, 2, targetGrade: 5);
        Assert.Equal(0, needed);
    }

    [Fact]
    public void CalculateRequiredFives_CalculatesExactFivesNeeded()
    {
        // Student has a 4 (w=1). Sum = 4, Weight = 1.
        // Threshold for 5 is 4.60.
        // (4 + 5*N) / (1 + N) >= 4.60
        // 4 + 5N >= 4.60 + 4.60N
        // 0.40N >= 0.60 => N >= 1.5 => 2 fives needed.
        int neededW1 = GradeCalculator.CalculateRequiredFives(4, 1, targetGrade: 5, weight: 1);
        Assert.Equal(2, neededW1);

        // Check math: (4 + 5*2) / (1 + 2) = 14 / 3 = 4.667 >= 4.60!
        var (s, w, gpa) = GradeCalculator.CalculateGpa(new List<(int, int)> { (4, 1), (5, 1), (5, 1) });
        Assert.True(gpa >= 4.60);
    }

    [Fact]
    public void CalculateRequiredFives_WithWeightTwo_NeedsFewerMarks()
    {
        // Same student: 4 (w=1). With exam/control work (w=2):
        // (4 + 10*N) / (1 + 2N) >= 4.60
        // 4 + 10N >= 4.60 + 9.20N
        // 0.80N >= 0.60 => N >= 0.75 => 1 five of weight 2 needed.
        int neededW2 = GradeCalculator.CalculateRequiredFives(4, 1, targetGrade: 5, weight: 2);
        Assert.Equal(1, neededW2);

        // Check math: (4 + 5*2) / (1 + 2) = 14 / 3 = 4.667 >= 4.60!
        var (s, w, gpa) = GradeCalculator.CalculateGpa(new List<(int, int)> { (4, 1), (5, 2) });
        Assert.True(gpa >= 4.60);
    }

    [Fact]
    public void CalculateRequiredFours_TargetGradeFour_CalculatesCorrectly()
    {
        // Student has 3 (w=1). Threshold for 4 is 3.60.
        // (3 + 4*N) / (1 + N) >= 3.60
        // 3 + 4N >= 3.60 + 3.60N
        // 0.40N >= 0.60 => N >= 1.5 => 2 fours needed.
        int neededFours = GradeCalculator.CalculateRequiredFours(3, 1, targetGrade: 4, weight: 1);
        Assert.Equal(2, neededFours);

        // Check math: (3 + 4*2) / (1 + 2) = 11 / 3 = 3.667 >= 3.60
        var (s, w, gpa) = GradeCalculator.CalculateGpa(new List<(int, int)> { (3, 1), (4, 1), (4, 1) });
        Assert.True(gpa >= 3.60);
    }

    [Theory]
    [InlineData("54", "5/4")]
    [InlineData("45", "4/5")]
    [InlineData("55", "5/5")]
    [InlineData("34", "3/4")]
    [InlineData("5 4", "5/4")]
    [InlineData("5, 4", "5/4")]
    [InlineData("5/4", "5/4")]
    [InlineData("544", "5/4/4")]
    [InlineData("5", "5")]
    [InlineData("Н", "Н")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NormalizeMarkValue_FormatsDoubleMarksCorrectly(string? input, string expected)
    {
        string actual = MirageDiary.Core.Models.GosuslugiMarkItem.NormalizeMarkValue(input);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void GosuslugiMarkItem_ValueProperty_NormalizesFiftyFourToFiveSlashFour()
    {
        var item1 = new MirageDiary.Core.Models.GosuslugiMarkItem { RawValue = "54" };
        Assert.Equal("5/4", item1.Value);

        var item2 = new MirageDiary.Core.Models.GosuslugiMarkItem { MarkValue1 = "54" };
        Assert.Equal("5/4", item2.Value);

        var item3 = new MirageDiary.Core.Models.GosuslugiMarkItem { MarkValue1 = "5", MarkValue2 = "4" };
        Assert.Equal("5/4", item3.Value);
    }

    [Fact]
    public void ParseMarkValues_DecomposesConcatenatedAndSeparatedMarks()
    {
        var marks54 = GradeCalculator.ParseMarkValues("54");
        Assert.Equal(new List<int> { 5, 4 }, marks54);

        var marks5Slash4 = GradeCalculator.ParseMarkValues("5/4");
        Assert.Equal(new List<int> { 5, 4 }, marks5Slash4);

        var marks5Space4 = GradeCalculator.ParseMarkValues("5 4");
        Assert.Equal(new List<int> { 5, 4 }, marks5Space4);

        var marksSingle = GradeCalculator.ParseMarkValues("5");
        Assert.Equal(new List<int> { 5 }, marksSingle);

        var marksNonNumeric = GradeCalculator.ParseMarkValues("Н");
        Assert.Empty(marksNonNumeric);
    }

    [Fact]
    public void CalculateMarksGpa_WithWeightsAndDoubleMarks_CalculatesAccurately()
    {
        var marks = new List<MirageDiary.Core.Models.GosuslugiMarkItem>
        {
            new() { Value = "5", Weight = 2, SubjectName = "Алгебра" },   // 5*2 = 10, w=2
            new() { Value = "5/4", Weight = 1, SubjectName = "Алгебра" }  // 5*1 + 4*1 = 9, w=2
        };

        var (sum, totalW, gpa) = GradeCalculator.CalculateMarksGpa(marks);
        Assert.Equal(19, sum);
        Assert.Equal(4, totalW);
        Assert.Equal(4.75, gpa);
    }

    [Fact]
    public void CalculateSubjectGpa_FiltersBySubjectNameCaseInsensitively()
    {
        var marks = new List<MirageDiary.Core.Models.GosuslugiMarkItem>
        {
            new() { Value = "5", Weight = 1, SubjectName = "Алгебра" },
            new() { Value = "3", Weight = 1, SubjectName = "Геометрия" },
            new() { Value = "4", Weight = 1, SubjectName = " алгебра " }
        };

        var (sum, totalW, gpa) = GradeCalculator.CalculateSubjectGpa(marks, "Алгебра");
        Assert.Equal(9, sum);
        Assert.Equal(2, totalW);
        Assert.Equal(4.50, gpa);
    }

    [Fact]
    public void ComputeSubjectDynamics_DetectsPositiveDeltaAndForecastChange()
    {
        var prevMarks = new List<MirageDiary.Core.Models.GosuslugiMarkItem>
        {
            new() { Value = "3", Weight = 1, SubjectName = "Алгебра" } // GPA = 3.00, forecast = 3
        };

        var currMarks = new List<MirageDiary.Core.Models.GosuslugiMarkItem>
        {
            new() { Value = "3", Weight = 1, SubjectName = "Алгебра" },
            new() { Value = "5", Weight = 2, SubjectName = "Алгебра" } // (3 + 10) / 3 = 4.33, forecast = 4
        };

        var dynamics = GradeCalculator.ComputeSubjectDynamics(prevMarks, currMarks, "Алгебра");
        Assert.Equal(3.00, dynamics.PreviousGpa);
        Assert.Equal(4.33, dynamics.NewGpa);
        Assert.Equal(1.33, dynamics.Delta);
        Assert.True(dynamics.IsPositive);
        Assert.Equal(3, dynamics.PreviousForecast);
        Assert.Equal(4, dynamics.NewForecast);

        string gpaLine = dynamics.FormatSubjectGpaLine();
        Assert.Contains("`3.00` ➡️ *4.33*", gpaLine);
        Assert.Contains("🟢 +1.33", gpaLine);

        string forecastLine = dynamics.FormatForecastLine();
        Assert.Contains("`3` ➡️ *4* 🎉", forecastLine);
    }

    [Fact]
    public void ComputeSubjectDynamics_FirstMark_ShowsFirstMarkNote()
    {
        var prevMarks = new List<MirageDiary.Core.Models.GosuslugiMarkItem>();
        var currMarks = new List<MirageDiary.Core.Models.GosuslugiMarkItem>
        {
            new() { Value = "5", Weight = 1, SubjectName = "Биология" }
        };

        var dynamics = GradeCalculator.ComputeSubjectDynamics(prevMarks, currMarks, "Биология");
        Assert.True(dynamics.IsFirstMark);
        Assert.Equal(5.00, dynamics.NewGpa);

        string gpaLine = dynamics.FormatSubjectGpaLine();
        Assert.Contains("`5.00` *(первая оценка)*", gpaLine);

        string forecastLine = dynamics.FormatForecastLine();
        Assert.Contains("*5*", forecastLine);
        Assert.DoesNotContain("➡️", forecastLine);
    }

    [Fact]
    public void ComputeSubjectDynamics_NegativeDelta_ShowsWarningAndDrop()
    {
        var prevMarks = new List<MirageDiary.Core.Models.GosuslugiMarkItem>
        {
            new() { Value = "5", Weight = 1, SubjectName = "Физика" } // GPA = 5.00, forecast = 5
        };
        var currMarks = new List<MirageDiary.Core.Models.GosuslugiMarkItem>
        {
            new() { Value = "5", Weight = 1, SubjectName = "Физика" },
            new() { Value = "2", Weight = 1, SubjectName = "Физика" } // GPA = 3.50, forecast = 4
        };

        var dynamics = GradeCalculator.ComputeSubjectDynamics(prevMarks, currMarks, "Физика");
        Assert.True(dynamics.IsNegative);
        Assert.Equal(-1.50, dynamics.Delta);

        string gpaLine = dynamics.FormatSubjectGpaLine();
        Assert.Contains("🔻 -1.50", gpaLine);

        string forecastLine = dynamics.FormatForecastLine();
        Assert.Contains("`5` ➡️ *4* ⚠️", forecastLine);
    }

    [Fact]
    public void ComputeOverallDynamics_CalculatesOverallAverageAndFormatsCorrectly()
    {
        var prevMarks = new List<MirageDiary.Core.Models.GosuslugiMarkItem>
        {
            new() { Value = "4", Weight = 1, SubjectName = "Русский язык" }
        };
        var currMarks = new List<MirageDiary.Core.Models.GosuslugiMarkItem>
        {
            new() { Value = "4", Weight = 1, SubjectName = "Русский язык" },
            new() { Value = "5", Weight = 1, SubjectName = "Литература" }
        };

        var overall = GradeCalculator.ComputeOverallDynamics(prevMarks, currMarks);
        Assert.Equal(4.00, overall.PreviousGpa);
        Assert.Equal(4.50, overall.NewGpa);
        Assert.Equal(0.50, overall.Delta);

        string overallLine = overall.FormatOverallGpaLine();
        Assert.Contains("📈 *Общий средний балл:* `4.00` ➡️ *4.50* (🟢 +0.50)", overallLine);
    }

    [Fact]
    public void SimulateUnpostedGrade_HypotheticalTwoControlWork_DropsForecastAndCalculatesRecovery()
    {
        // Student currently has Sum = 36, Weight = 10, GPA = 3.60 (Forecast = 4)
        var sim = GradeCalculator.SimulateUnpostedGrade(
            subjectIndex: 0,
            subjectName: "Алгебра",
            currentWeightedSum: 36,
            currentTotalWeight: 10,
            grade: 2,
            weight: 2,
            count: 1
        );

        Assert.Equal(3.60, sim.CurrentGpa);
        Assert.Equal(4, sim.CurrentForecast);
        Assert.Equal(40, sim.NewSum);
        Assert.Equal(12, sim.NewWeight);
        Assert.Equal(3.33, sim.NewGpa);
        Assert.Equal(-0.27, sim.Delta);
        Assert.Equal(3, sim.NewForecast);
        Assert.True(sim.ForecastDropped);

        // Target recovery is 4 (threshold >= 3.60)
        // diff = 3.60 * 12 - 40 = 3.20.
        // Fives W1 needed: ceil(3.20 / 1.40) = 3
        // Fives W2 needed: ceil(3.20 / 2.80) = 2
        Assert.Equal(3, sim.NeededFivesW1ToRecover);
        Assert.Equal(2, sim.NeededFivesW2ToRecover);
    }

    [Fact]
    public void SimulateUnpostedGrade_MultipleFives_BoostsAverage()
    {
        // Student has Sum = 14, Weight = 4, GPA = 3.50 (Forecast 4)
        var sim = GradeCalculator.SimulateUnpostedGrade(
            subjectIndex: 1,
            subjectName: "Русский язык",
            currentWeightedSum: 14,
            currentTotalWeight: 4,
            grade: 5,
            weight: 1,
            count: 2
        );

        // Added sum: 5 * 1 * 2 = 10. New sum = 24.
        // Added weight: 1 * 2 = 2. New weight = 6.
        // New GPA: 24 / 6 = 4.00.
        Assert.Equal(4.00, sim.NewGpa);
        Assert.Equal(0.50, sim.Delta);
        Assert.False(sim.ForecastDropped);
        Assert.Equal(0, sim.NeededFivesW1ToRecover);
    }
}
