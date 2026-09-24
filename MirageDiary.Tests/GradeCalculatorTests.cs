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
}
