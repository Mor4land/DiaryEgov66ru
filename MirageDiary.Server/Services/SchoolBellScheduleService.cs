using System;
using System.Collections.Generic;
using System.Linq;

namespace MirageDiary.Server.Services;

public record LessonBell(int Shift, int LessonNumber, TimeSpan Start, TimeSpan End);

public class BellStatusInfo
{
    public bool IsInLesson { get; set; }
    public bool IsInBreak { get; set; }
    public bool IsSchoolDay { get; set; }
    public int? CurrentLessonNumber { get; set; }
    public int? NextLessonNumber { get; set; }
    public int? Shift { get; set; }
    public TimeSpan? TimeUntilNextBell { get; set; }
    public string StatusMessage { get; set; } = "";
}

/// <summary>
/// Расписание звонков МАОУ СОШ № 113 на 2026/2027 учебный год.
/// </summary>
public class SchoolBellScheduleService
{
    public static readonly IReadOnlyList<LessonBell> FirstShift = new List<LessonBell>
    {
        new(1, 1, new TimeSpan(8, 0, 0),  new TimeSpan(8, 40, 0)),
        new(1, 2, new TimeSpan(8, 55, 0), new TimeSpan(9, 35, 0)),
        new(1, 3, new TimeSpan(9, 50, 0), new TimeSpan(10, 30, 0)),
        new(1, 4, new TimeSpan(10, 45, 0), new TimeSpan(11, 25, 0)),
        new(1, 5, new TimeSpan(11, 40, 0), new TimeSpan(12, 20, 0)),
        new(1, 6, new TimeSpan(12, 25, 0), new TimeSpan(13, 5, 0)),
        new(1, 7, new TimeSpan(13, 10, 0), new TimeSpan(13, 50, 0)),
    };

    public static readonly IReadOnlyList<LessonBell> SecondShift = new List<LessonBell>
    {
        new(2, 1, new TimeSpan(13, 10, 0), new TimeSpan(13, 50, 0)),
        new(2, 2, new TimeSpan(14, 5, 0),  new TimeSpan(14, 45, 0)),
        new(2, 3, new TimeSpan(15, 0, 0),  new TimeSpan(15, 40, 0)),
        new(2, 4, new TimeSpan(15, 55, 0), new TimeSpan(16, 35, 0)),
        new(2, 5, new TimeSpan(16, 50, 0), new TimeSpan(17, 30, 0)),
        new(2, 6, new TimeSpan(17, 35, 0), new TimeSpan(18, 15, 0)),
        new(2, 7, new TimeSpan(18, 20, 0), new TimeSpan(19, 0, 0)),
    };

    private static readonly IReadOnlyList<LessonBell> AllLessons = FirstShift.Concat(SecondShift).ToList();

    /// <summary>
    /// Проверяет, является ли текущий день учебным (Пн - Сб).
    /// </summary>
    public bool IsSchoolDay(DateTime localTime)
    {
        return localTime.DayOfWeek != DayOfWeek.Sunday;
    }

    /// <summary>
    /// Возвращает true, если сейчас окно звонка (за 2 минуты до конца урока или в первые 6 минут перемены).
    /// В это время учителя выставляют оценки за прошедший урок.
    /// </summary>
    public bool IsLessonEndingOrBreakWindow(DateTime localTime)
    {
        if (!IsSchoolDay(localTime)) return false;

        var timeOfDay = localTime.TimeOfDay;

        foreach (var lesson in AllLessons)
        {
            // Окно: [End - 2 min, End + 6 min]
            var windowStart = lesson.End.Subtract(TimeSpan.FromMinutes(2));
            var windowEnd = lesson.End.Add(TimeSpan.FromMinutes(6));

            if (timeOfDay >= windowStart && timeOfDay <= windowEnd)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Возвращает детальный статус по текущему уроку или перемене (для команды /now).
    /// </summary>
    public BellStatusInfo GetCurrentStatus(DateTime localTime, int shift = 1)
    {
        var schedule = shift == 2 ? SecondShift : FirstShift;
        var info = new BellStatusInfo
        {
            IsSchoolDay = IsSchoolDay(localTime),
            Shift = shift
        };

        if (!info.IsSchoolDay)
        {
            info.StatusMessage = "Сегодня воскресенье — уроков нет, отдыхай! 🏖";
            return info;
        }

        var now = localTime.TimeOfDay;
        var firstLesson = schedule.First();
        var lastLesson = schedule.Last();

        if (now < firstLesson.Start)
        {
            var diff = firstLesson.Start - now;
            info.TimeUntilNextBell = diff;
            info.NextLessonNumber = 1;
            info.StatusMessage = $"🌅 Уроки еще не начались.\n1-й урок начнется в {firstLesson.Start:hh\\:mm} (через {(int)diff.TotalMinutes} мин).";
            return info;
        }

        if (now > lastLesson.End)
        {
            info.StatusMessage = "🎉 Учебный день подошел к концу! Все уроки завершены.";
            return info;
        }

        // Проверяем, идет ли урок прямо сейчас
        foreach (var lesson in schedule)
        {
            if (now >= lesson.Start && now <= lesson.End)
            {
                var remaining = lesson.End - now;
                info.IsInLesson = true;
                info.CurrentLessonNumber = lesson.LessonNumber;
                info.TimeUntilNextBell = remaining;

                int remMin = Math.Max(1, (int)remaining.TotalMinutes);
                info.StatusMessage = $"📚 Идет {lesson.LessonNumber}-й урок ({lesson.Start:hh\\:mm} – {lesson.End:hh\\:mm}).\n🔔 До звонка с урока: **{remMin} мин**.";
                return info;
            }
        }

        // Если не в уроке, значит перемена!
        for (int i = 0; i < schedule.Count - 1; i++)
        {
            var prev = schedule[i];
            var next = schedule[i + 1];

            if (now > prev.End && now < next.Start)
            {
                var remaining = next.Start - now;
                info.IsInBreak = true;
                info.CurrentLessonNumber = prev.LessonNumber;
                info.NextLessonNumber = next.LessonNumber;
                info.TimeUntilNextBell = remaining;

                int remMin = Math.Max(1, (int)remaining.TotalMinutes);
                info.StatusMessage = $"☕ Перемена между {prev.LessonNumber}-м и {next.LessonNumber}-м уроками.\n🔔 До звонка на {next.LessonNumber}-й урок: **{remMin} мин** (начнется в {next.Start:hh\\:mm}).";
                return info;
            }
        }

        info.StatusMessage = "Учебный день завершен.";
        return info;
    }
}
