using System;
using System.Collections.Generic;
using System.Globalization;
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

    /// <summary>
    /// Вычисляет взвешенную сумму, суммарный вес и средний балл (GPA) для переданного набора оценок.
    /// Автоматически разбирает составные (5/4) и слитные (54) оценки, а также учитывает веса.
    /// </summary>
    public static (int weightedSum, int totalWeight, double gpa) CalculateMarksGpa(IEnumerable<GosuslugiMarkItem>? marks)
    {
        if (marks == null) return (0, 0, 0.0);

        int wSum = 0;
        int totalW = 0;

        foreach (var m in marks)
        {
            if (string.IsNullOrWhiteSpace(m?.Value)) continue;

            var parts = m.Value.Split(new[] { '/', '\\', ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (int.TryParse(part.Trim(), out int gradeVal))
                {
                    if (gradeVal is >= 1 and <= 5)
                    {
                        int w = (m.Weight.HasValue && m.Weight.Value > 0) ? m.Weight.Value : 1;
                        wSum += gradeVal * w;
                        totalW += w;
                    }
                    else if (gradeVal is >= 11 and <= 55)
                    {
                        int d1 = gradeVal / 10;
                        int d2 = gradeVal % 10;
                        if (d1 is >= 1 and <= 5 && d2 is >= 1 and <= 5)
                        {
                            int w = (m.Weight.HasValue && m.Weight.Value > 0) ? m.Weight.Value : 1;
                            wSum += (d1 + d2) * w;
                            totalW += w * 2;
                        }
                    }
                }
            }
        }

        double gpa = totalW > 0 ? Math.Round((double)wSum / totalW, 2) : 0.0;
        return (wSum, totalW, gpa);
    }

    /// <summary>
    /// Вычисляет средний балл по конкретному предмету.
    /// </summary>
    public static (int weightedSum, int totalWeight, double gpa) CalculateSubjectGpa(IEnumerable<GosuslugiMarkItem>? marks, string? subjectName)
    {
        if (marks == null || string.IsNullOrWhiteSpace(subjectName)) return (0, 0, 0.0);
        string target = subjectName.Trim();
        var subjectMarks = marks.Where(m => !string.IsNullOrEmpty(m.SubjectName) && 
            m.SubjectName.Trim().Equals(target, StringComparison.OrdinalIgnoreCase));
        return CalculateMarksGpa(subjectMarks);
    }

    public record GpaDynamics(
        double PreviousGpa, 
        int PreviousWeight, 
        double NewGpa, 
        int NewWeight, 
        double Delta, 
        int PreviousForecast, 
        int NewForecast)
    {
        public bool IsFirstMark => PreviousWeight == 0 && NewWeight > 0;
        public bool HasChange => PreviousWeight > 0 && Math.Abs(Delta) >= 0.005;
        public bool IsPositive => Delta > 0.001;
        public bool IsNegative => Delta < -0.001;

        public string FormatSubjectGpaLine(string? quarterLabel = null)
        {
            var ci = CultureInfo.InvariantCulture;
            string prefix = !string.IsNullOrEmpty(quarterLabel) ? $"📊 *Средний балл ({quarterLabel}):*" : "📊 *Средний балл:*";
            if (IsFirstMark)
            {
                return $"{prefix} `{NewGpa.ToString("F2", ci)}` *(первая оценка)*";
            }
            if (PreviousWeight > 0 && NewWeight > 0)
            {
                if (IsPositive)
                    return $"{prefix} `{PreviousGpa.ToString("F2", ci)}` ➡️ *{NewGpa.ToString("F2", ci)}* (🟢 +{Delta.ToString("F2", ci)})";
                if (IsNegative)
                    return $"{prefix} `{PreviousGpa.ToString("F2", ci)}` ➡️ *{NewGpa.ToString("F2", ci)}* (🔻 {Delta.ToString("F2", ci)})";
                return $"{prefix} *{NewGpa.ToString("F2", ci)}* *(без изм.)*";
            }
            return NewWeight > 0 ? $"{prefix} `{NewGpa.ToString("F2", ci)}`" : $"{prefix} —";
        }

        public string FormatForecastLine(string? quarterLabel = null)
        {
            if (NewForecast <= 0) return string.Empty;
            string prefix = !string.IsNullOrEmpty(quarterLabel) ? $"🎯 *Прогноз ({quarterLabel}):*" : "🎯 *Прогноз за четверть:*";
            if (PreviousWeight > 0 && PreviousForecast > 0 && PreviousForecast != NewForecast)
            {
                string icon = NewForecast > PreviousForecast ? "🎉" : "⚠️";
                return $"{prefix} `{PreviousForecast}` ➡️ *{NewForecast}* {icon}";
            }
            return $"{prefix} *{NewForecast}*";
        }

        public string FormatOverallGpaLine()
        {
            var ci = CultureInfo.InvariantCulture;
            if (IsFirstMark)
            {
                return $"📈 *Общий средний балл:* `{NewGpa.ToString("F2", ci)}`";
            }
            if (PreviousWeight > 0 && NewWeight > 0)
            {
                if (IsPositive)
                    return $"📈 *Общий средний балл:* `{PreviousGpa.ToString("F2", ci)}` ➡️ *{NewGpa.ToString("F2", ci)}* (🟢 +{Delta.ToString("F2", ci)})";
                if (IsNegative)
                    return $"📈 *Общий средний балл:* `{PreviousGpa.ToString("F2", ci)}` ➡️ *{NewGpa.ToString("F2", ci)}* (🔻 {Delta.ToString("F2", ci)})";
                return $"📈 *Общий средний балл:* *{NewGpa.ToString("F2", ci)}* *(без изм.)*";
            }
            return NewWeight > 0 ? $"📈 *Общий средний балл:* `{NewGpa.ToString("F2", ci)}`" : string.Empty;
        }
    }

    public static GpaDynamics ComputeSubjectDynamics(
        IEnumerable<GosuslugiMarkItem>? previousMarks,
        IEnumerable<GosuslugiMarkItem>? currentMarks,
        string? subjectName)
    {
        var prev = CalculateSubjectGpa(previousMarks, subjectName);
        var curr = CalculateSubjectGpa(currentMarks, subjectName);
        double delta = Math.Round(curr.gpa - prev.gpa, 2);
        int prevForecast = prev.totalWeight > 0 ? (int)Math.Round(prev.gpa, MidpointRounding.AwayFromZero) : 0;
        int newForecast = curr.totalWeight > 0 ? (int)Math.Round(curr.gpa, MidpointRounding.AwayFromZero) : 0;

        return new GpaDynamics(prev.gpa, prev.totalWeight, curr.gpa, curr.totalWeight, delta, prevForecast, newForecast);
    }

    public static GpaDynamics ComputeOverallDynamics(
        IEnumerable<GosuslugiMarkItem>? previousMarks,
        IEnumerable<GosuslugiMarkItem>? currentMarks)
    {
        var prev = CalculateMarksGpa(previousMarks);
        var curr = CalculateMarksGpa(currentMarks);
        double delta = Math.Round(curr.gpa - prev.gpa, 2);
        int prevForecast = prev.totalWeight > 0 ? (int)Math.Round(prev.gpa, MidpointRounding.AwayFromZero) : 0;
        int newForecast = curr.totalWeight > 0 ? (int)Math.Round(curr.gpa, MidpointRounding.AwayFromZero) : 0;

        return new GpaDynamics(prev.gpa, prev.totalWeight, curr.gpa, curr.totalWeight, delta, prevForecast, newForecast);
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

    public record HypotheticalGradeSimulation(
        int SubjectIndex,
        string SubjectName,
        int CurrentSum,
        int CurrentWeight,
        double CurrentGpa,
        int CurrentForecast,
        int Grade,
        int Weight,
        int GradeCount,
        int NewSum,
        int NewWeight,
        double NewGpa,
        double Delta,
        int NewForecast,
        bool ForecastDropped,
        int NeededFivesW1ToRecover,
        int NeededFivesW2ToRecover
    );

    public static HypotheticalGradeSimulation SimulateUnpostedGrade(
        int subjectIndex,
        string subjectName,
        int currentWeightedSum,
        int currentTotalWeight,
        int grade,
        int weight = 1,
        int count = 1)
    {
        grade = Math.Clamp(grade, 1, 5);
        weight = Math.Clamp(weight, 1, 3);
        count = Math.Max(1, count);

        double curGpa = currentTotalWeight > 0 ? Math.Round((double)currentWeightedSum / currentTotalWeight, 2) : 0.0;
        int curForecast = currentTotalWeight > 0 ? (int)Math.Round(curGpa, MidpointRounding.AwayFromZero) : 0;

        int addedSum = grade * weight * count;
        int addedWeight = weight * count;

        int newSum = currentWeightedSum + addedSum;
        int newWeight = currentTotalWeight + addedWeight;
        double newGpa = newWeight > 0 ? Math.Round((double)newSum / newWeight, 2) : 0.0;
        double delta = Math.Round(newGpa - curGpa, 2);
        int newForecast = newWeight > 0 ? (int)Math.Round(newGpa, MidpointRounding.AwayFromZero) : 0;
        bool dropped = curForecast > 0 && newForecast < curForecast;

        int targetRecoveryGrade = curForecast > 0 ? curForecast : (grade >= 4 ? grade : 4);
        int neededFivesW1 = CalculateRequiredFives(newSum, newWeight, targetRecoveryGrade, 1);
        int neededFivesW2 = CalculateRequiredFives(newSum, newWeight, targetRecoveryGrade, 2);

        return new HypotheticalGradeSimulation(
            subjectIndex,
            subjectName,
            currentWeightedSum,
            currentTotalWeight,
            curGpa,
            curForecast,
            grade,
            weight,
            count,
            newSum,
            newWeight,
            newGpa,
            delta,
            newForecast,
            dropped,
            neededFivesW1,
            neededFivesW2
        );
    }
}
