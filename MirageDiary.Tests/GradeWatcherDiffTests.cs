using System;
using System.Collections.Generic;
using System.Linq;
using MirageDiary.Core;
using MirageDiary.Core.Models;
using MirageDiary.Server.Services;
using Xunit;

namespace MirageDiary.Tests;

public class GradeWatcherDiffTests
{
    [Fact]
    public void GetMarkKey_UsesId_WhenAvailable()
    {
        var mark = new GosuslugiMarkItem
        {
            Id = "660708-512-10-0-0-2026-09-21",
            SubjectName = "География",
            Date = "2026-09-21",
            Value = "5",
            Weight = 1
        };

        var key = GradeWatcherWorker.GetMarkKey(mark);
        Assert.Equal("660708-512-10-0-0-2026-09-21", key);
    }

    [Fact]
    public void GetMarkKey_BuildsCompositeKey_WhenIdIsEmpty()
    {
        var mark = new GosuslugiMarkItem
        {
            Id = "",
            SubjectName = "Алгебра",
            Date = "2026-09-17",
            Value = "4",
            Weight = 2,
            WorkKind = "Контрольная"
        };

        var key = GradeWatcherWorker.GetMarkKey(mark);
        Assert.Equal("Алгебра_2026-09-17_4_2_Контрольная", key);
    }

    [Fact]
    public void ExactDiffing_DetectsOnlyRealNewMarks_PreventsFalseAlarmOnHashMismatch()
    {
        // Базовый набор оценок, как в SQLite кэше
        var cachedMarks = new List<GosuslugiMarkItem>
        {
            new() { Id = "id-1", SubjectName = "География", Date = "2026-09-21", Value = "5" },
            new() { Id = "id-2", SubjectName = "Алгебра", Date = "2026-09-17", Value = "4" },
            new() { Id = "id-3", SubjectName = "Англ. яз.", Date = "2026-09-16", Value = "3" }
        };

        // Тот же набор, но хэш мог измениться из-за деплоя/перезапуска/нового формата
        var currentMarks = new List<GosuslugiMarkItem>
        {
            new() { Id = "id-1", SubjectName = "География", Date = "2026-09-21", Value = "5" },
            new() { Id = "id-2", SubjectName = "Алгебра", Date = "2026-09-17", Value = "4" },
            new() { Id = "id-3", SubjectName = "Англ. яз.", Date = "2026-09-16", Value = "3" }
        };

        var prevKeys = new HashSet<string>(cachedMarks.Select(GradeWatcherWorker.GetMarkKey));
        var freshMarks = currentMarks.Where(m => !prevKeys.Contains(GradeWatcherWorker.GetMarkKey(m))).ToList();

        // Должно быть 0 новых оценок — никакого спама в чат!
        Assert.Empty(freshMarks);
    }

    [Fact]
    public void ExactDiffing_IsolatesOnlyTheGenuinelyNewMark_NotOldOnes()
    {
        var cachedMarks = new List<GosuslugiMarkItem>
        {
            new() { Id = "id-1", SubjectName = "География", Date = "2026-09-21", Value = "5" },
            new() { Id = "id-2", SubjectName = "Алгебра", Date = "2026-09-17", Value = "4" },
            new() { Id = "id-3", SubjectName = "Англ. яз.", Date = "2026-09-16", Value = "3" }
        };

        // Добавилась 1 новая оценка по Истории
        var currentMarks = new List<GosuslugiMarkItem>
        {
            new() { Id = "id-4", SubjectName = "История", Date = "2026-09-24", Value = "5" },
            new() { Id = "id-1", SubjectName = "География", Date = "2026-09-21", Value = "5" },
            new() { Id = "id-2", SubjectName = "Алгебра", Date = "2026-09-17", Value = "4" },
            new() { Id = "id-3", SubjectName = "Англ. яз.", Date = "2026-09-16", Value = "3" }
        };

        var prevKeys = new HashSet<string>(cachedMarks.Select(GradeWatcherWorker.GetMarkKey));
        var freshMarks = currentMarks.Where(m => !prevKeys.Contains(GradeWatcherWorker.GetMarkKey(m))).ToList();

        // Должна быть обнаружена ТОЛЬКО 1 новая оценка, а не 3 старых
        Assert.Single(freshMarks);
        Assert.Equal("История", freshMarks[0].SubjectName);
        Assert.Equal("5", freshMarks[0].Value);
        Assert.Equal("2026-09-24", freshMarks[0].Date);
    }

    [Fact]
    public void GetMarkKey_IncludesLessonId_WhenPresentAndIdEmpty()
    {
        var mark = new GosuslugiMarkItem
        {
            Id = "",
            SubjectName = "Биология",
            Date = "2026-09-18",
            Value = "3",
            Weight = 1,
            WorkKind = "Ответ на уроке",
            LessonId = "lesson-123"
        };

        var key = GradeWatcherWorker.GetMarkKey(mark);
        Assert.Equal("Биология_2026-09-18_3_1_Ответ на уроке_lesson-123", key);
    }

    [Fact]
    public void ComputeMarksHash_DifferentiatesDualGradesOnSameDay()
    {
        // Ситуация как на 2026-09-18: две оценки по биологии (3 и 5)
        var marks = new List<GosuslugiMarkItem>
        {
            new() { Id = "mark-3", LessonId = "l-1", SubjectName = "Биология", Date = "2026-09-18", Value = "3", Weight = 1 },
            new() { Id = "mark-5", LessonId = "l-2", SubjectName = "Биология", Date = "2026-09-18", Value = "5", Weight = 1 }
        };

        var hash = GradeWatcherWorker.ComputeMarksHash(marks);
        Assert.False(string.IsNullOrEmpty(hash));

        // Если одна из оценок удаляется или меняется — хэш строго меняется
        var marksOnly3 = new List<GosuslugiMarkItem>
        {
            new() { Id = "mark-3", LessonId = "l-1", SubjectName = "Биология", Date = "2026-09-18", Value = "3", Weight = 1 }
        };
        var hashOnly3 = GradeWatcherWorker.ComputeMarksHash(marksOnly3);
        Assert.NotEqual(hash, hashOnly3);
    }

    [Fact]
    public void GetRecentWeekIntervals_IncludesFutureWeeksAhead()
    {
        var intervals = DiaryClient.GetRecentWeekIntervals(count: 12, futureWeeks: 5);
        Assert.NotEmpty(intervals);

        var now = DateTime.UtcNow;
        var in4Weeks = now.AddDays(28);
        int expectedWeekNum = System.Globalization.ISOWeek.GetWeekOfYear(in4Weeks);
        string expectedInterval = $"{expectedWeekNum:D2}{in4Weeks.Year}";

        Assert.Contains(expectedInterval, intervals);
    }
}
