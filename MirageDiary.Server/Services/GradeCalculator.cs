using System;
using System.Collections.Generic;
using System.Linq;
using MirageDiary.Core.Models;
using MirageDiary.Server.Models;

namespace MirageDiary.Server.Services;

/// <summary>
/// Вычислитель среднего балла (GPA) и прогнозирования требуемых оценок
/// для достижения целевой четвертной/годовой оценки по алгоритмам Госуслуг и ФГИС «Моя Школа».
/// </summary>
public static class GradeCalculator
{
    public static double GetTargetThreshold(int targetGrade) => targetGrade switch
    {
        5 => 4.60,
        4 => 3.60,
        3 => 2.60,
        _ => 4.60
    };

    /// <summary>
    /// Извлекает отдельные числовые оценки (1..5) из сырой строки Госуслуг.
    /// Поддерживает как разделенные оценки ("5/4", "5, 4", "5 4"), так и слитные ("54", "45", "55").
    /// </summary>
    public static List<int> ParseMarkValues(string? raw)
    {
        var result = new List<int>();
        if (string.IsNullOrWhiteSpace(raw)) return result;

        string normalized = GosuslugiMarkItem.NormalizeMarkValue(raw);
        var parts = normalized.Split(new[] { '/', '\\', ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts)
        {
            if (int.TryParse(p.Trim(), out int g) && g is >= 1 and <= 5)
            {
                result.Add(g);
            }
        }
        return result;
    }

    public static (int weightedSum, int totalWeight, double gpa) CalculateGpa(IEnumerable<(int mark, int weight)> marks)
    {
        int sum = 0;
        int totalW = 0;

        foreach (var (m, w) in marks)
        {
            if (m is >= 1 and <= 5 && w > 0)
            {
                sum += m * w;
                totalW += w;
            }
        }

        double gpa = totalW > 0 ? Math.Round((double)sum / totalW, 2) : 0.0;
        return (sum, totalW, gpa);
    }

    public static int CalculateRequiredGrades(int currentWeightedSum, int currentTotalWeight, int targetGrade, int gradeToEarn, int gradeWeight)
    {
        if (gradeToEarn <= 0 || gradeWeight <= 0) return 0;

        double threshold = GetTargetThreshold(targetGrade);
        if (currentTotalWeight > 0)
        {
            double currentGpa = (double)currentWeightedSum / currentTotalWeight;
            if (currentGpa >= threshold)
            {
                return 0; // Already achieved
            }
        }

        // Formula: (currentWeightedSum + N * gradeToEarn * gradeWeight) / (currentTotalWeight + N * gradeWeight) >= threshold
        // currentWeightedSum + N * gradeToEarn * gradeWeight >= threshold * currentTotalWeight + N * threshold * gradeWeight
        // N * gradeWeight * (gradeToEarn - threshold) >= threshold * currentTotalWeight - currentWeightedSum
        double denominator = gradeWeight * (gradeToEarn - threshold);
        if (denominator <= 0.0001)
        {
            return -1; // Mathematically impossible to reach threshold with this grade
        }

        double numerator = threshold * currentTotalWeight - currentWeightedSum;
        if (numerator <= 0) return 0;

        int needed = (int)Math.Ceiling(numerator / denominator);
        return Math.Max(1, needed);
    }

    public static int CalculateRequiredFives(int currentWeightedSum, int currentTotalWeight, int targetGrade, int weight = 1)
    {
        return CalculateRequiredGrades(currentWeightedSum, currentTotalWeight, targetGrade, 5, weight);
    }

    public static int CalculateRequiredFours(int currentWeightedSum, int currentTotalWeight, int targetGrade, int weight = 1)
    {
        return CalculateRequiredGrades(currentWeightedSum, currentTotalWeight, targetGrade, 4, weight);
    }
}
