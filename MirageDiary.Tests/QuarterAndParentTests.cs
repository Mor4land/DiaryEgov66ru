using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MirageDiary.Core;
using MirageDiary.Core.Models;
using MirageDiary.Server.Data;
using MirageDiary.Server.Models;
using MirageDiary.Server.Services;
using Xunit;

namespace MirageDiary.Tests;

public class QuarterAndParentTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _sp;
    private readonly AppDbContext _db;
    private readonly DiaryBotWorkflowEngine _engine;
    private readonly ITokenStore _tokenStore;

    public QuarterAndParentTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(opts => opts.UseSqlite(_connection));
        services.AddMemoryCache();
        services.AddLogging();
        services.AddHttpClient();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MiniAppUrl"] = "https://t.me/MirageDiaryBot/app",
                ["MaxBot:Username"] = "mirage_max_bot"
            })
            .Build();

        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<ITokenEncryptionService, TokenEncryptionService>();
        services.AddScoped<ITokenStore, DbTokenStore>();
        services.AddScoped<SharedClassroomCacheService>();
        services.AddScoped<RoleService>();
        services.AddScoped<UserSyncService>();
        services.AddScoped<SchoolBellScheduleService>();
        services.AddSingleton<MaintenanceModeService>();
        services.AddScoped<DiaryInteractionService>();
        services.AddScoped<DiaryBotWorkflowEngine>();

        _sp = services.BuildServiceProvider();
        _db = _sp.GetRequiredService<AppDbContext>();
        _db.Database.EnsureCreated();

        _engine = _sp.GetRequiredService<DiaryBotWorkflowEngine>();
        _tokenStore = _sp.GetRequiredService<ITokenStore>();
    }

    public void Dispose()
    {
        _connection.Close();
        _sp.Dispose();
    }

    [Theory]
    [InlineData("2026-09-15", 1)] // September -> Q1
    [InlineData("2026-10-25", 1)] // October -> Q1
    [InlineData("2026-11-10", 2)] // November -> Q2
    [InlineData("2026-12-28", 2)] // December -> Q2
    [InlineData("2027-01-15", 3)] // January -> Q3
    [InlineData("2027-03-20", 3)] // March -> Q3
    [InlineData("2027-04-10", 4)] // April -> Q4
    [InlineData("2027-05-25", 4)] // May -> Q4
    public void GetCurrentQuarter_CalculatesCorrectQuarter_ForGivenDates(string dateStr, int expectedQ)
    {
        var dt = DateTime.Parse(dateStr);
        int quarter = DiaryInteractionService.GetCurrentQuarter(dt);
        Assert.Equal(expectedQ, quarter);
    }

    [Theory]
    [InlineData("2026-09-20T08:00:00", 1)]
    [InlineData("2026-11-15", 2)]
    [InlineData("2027-02-10", 3)]
    [InlineData("2027-04-05", 4)]
    [InlineData(null, 0)]
    public void GetMarkQuarter_ParsesDateStringsCorrectly(string? dateStr, int expectedQ)
    {
        int q = DiaryInteractionService.GetMarkQuarter(dateStr);
        Assert.Equal(expectedQ, q);
    }

    [Fact]
    public async Task DbTokenStore_ReturnsSelectedStudentId_ForParent()
    {
        var user = new UserEntity
        {
            Id = "parent_user_1",
            AccessToken = "parent_token",
            RefreshToken = "parent_refresh",
            StudentId = "primary_student_id",
            SelectedStudentId = "child_2_student_id",
            Role = "Parent",
            HasAcceptedPrivacyPolicy = true
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var tokens = await _tokenStore.GetTokensAsync("parent_user_1");
        Assert.NotNull(tokens);
        Assert.Equal("child_2_student_id", tokens.StudentId);
    }

    [Fact]
    public async Task BuildMainMenuScreenAsync_ShowsParentHeaderAndChildrenButton_ForParent()
    {
        var user = new UserEntity
        {
            Id = "parent_menu_1",
            AccessToken = "valid_tok",
            Role = "Parent",
            ClassName = "9А",
            OrgName = "Лицей №1",
            HasAcceptedPrivacyPolicy = true
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var screen = await _engine.BuildMainMenuScreenAsync("parent_menu_1");
        Assert.NotNull(screen);
        Assert.Contains("Родитель", screen.Text);

        // Verify "👨‍👧 Дети" button exists
        var allButtons = screen.Buttons.SelectMany(r => r).ToList();
        var childrenButton = allButtons.FirstOrDefault(b => b.Text.Contains("Дети"));
        Assert.NotNull(childrenButton);
        Assert.Equal("cmd_children_menu", childrenButton.CallbackData);
    }

    [Fact]
    public async Task BuildMarksScreenAsync_RendersQuarterTogglesAndHighlightsActive()
    {
        var user = new UserEntity
        {
            Id = "student_marks_user",
            AccessToken = "marks_tok",
            Role = "Student",
            HasAcceptedPrivacyPolicy = true
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        // Screen for Q1
        var screenQ1 = await _engine.BuildMarksScreenAsync("student_marks_user", quarter: 1);
        Assert.NotNull(screenQ1);

        var allButtonsQ1 = screenQ1.Buttons.SelectMany(r => r).ToList();
        var q1Btn = allButtonsQ1.FirstOrDefault(b => b.CallbackData == "cmd_marks_q:1");
        var q2Btn = allButtonsQ1.FirstOrDefault(b => b.CallbackData == "cmd_marks_q:2");
        var allBtn = allButtonsQ1.FirstOrDefault(b => b.CallbackData == "cmd_marks_q:0");

        Assert.NotNull(q1Btn);
        Assert.NotNull(q2Btn);
        Assert.NotNull(allBtn);

        // Q1 button should have active indicator "•"
        Assert.Contains("•", q1Btn.Text);
        Assert.DoesNotContain("•", q2Btn.Text);

        // Screen for All quarters (Q0)
        var screenQ0 = await _engine.BuildMarksScreenAsync("student_marks_user", quarter: 0);
        var allButtonsQ0 = screenQ0.Buttons.SelectMany(r => r).ToList();
        var allBtnActive = allButtonsQ0.FirstOrDefault(b => b.CallbackData == "cmd_marks_q:0");
        Assert.NotNull(allBtnActive);
        Assert.Contains("•", allBtnActive.Text);
    }

    [Fact]
    public async Task CacheTable_PersistsAndRetainsMarksAcrossSessions()
    {
        string cacheKey = "marks_db:student_test_123";
        var mark1 = new GosuslugiMarkItem
        {
            Id = "mark_1",
            SubjectName = "Алгебра",
            Value = "5",
            Date = "2026-09-10",
            Weight = 1
        };

        var cached = new CachedData
        {
            CacheKey = cacheKey,
            JsonData = System.Text.Json.JsonSerializer.Serialize(new List<GosuslugiMarkItem> { mark1 }, DiaryClient.DatamartJsonOptions),
            UpdatedAt = DateTime.UtcNow
        };
        _db.Cache.Add(cached);
        await _db.SaveChangesAsync();

        // Retrieve from DB Cache
        var dbEntry = await _db.Cache.FirstOrDefaultAsync(c => c.CacheKey == cacheKey);
        Assert.NotNull(dbEntry);
        Assert.Contains("Алгебра", dbEntry.JsonData);
        Assert.Contains("mark_1", dbEntry.JsonData);

        var deserialized = System.Text.Json.JsonSerializer.Deserialize<List<GosuslugiMarkItem>>(dbEntry.JsonData, DiaryClient.DatamartJsonOptions);
        Assert.NotNull(deserialized);
        Assert.Single(deserialized);
        Assert.Equal("Алгебра", deserialized[0].SubjectName);
    }

    [Fact]
    public async Task ToggleGradeWatcher_OperatesIndependentlyFromMasterAlarm()
    {
        string userId = "user_grade_toggle_test";
        _db.Users.Add(new UserEntity
        {
            Id = userId,
            IsAlarmEnabled = true,
            IsGradeWatcherEnabled = true
        });
        await _db.SaveChangesAsync();

        // 1. Toggle grade watcher OFF
        var res1 = await _engine.ToggleGradeWatcherReminderAsync(userId);
        Assert.Contains("выключены", res1.AlertText ?? "");

        var userAfterGrToggle = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        Assert.NotNull(userAfterGrToggle);
        Assert.False(userAfterGrToggle.IsGradeWatcherEnabled);
        Assert.True(userAfterGrToggle.IsAlarmEnabled); // Master alarm remains ON

        // 2. Toggle master alarm OFF - must NOT overwrite IsGradeWatcherEnabled
        var res2 = await _engine.ToggleMasterAlarmAsync(userId);
        Assert.Contains("выключены", res2.AlertText ?? "");

        var userAfterMasterToggle = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        Assert.NotNull(userAfterMasterToggle);
        Assert.False(userAfterMasterToggle.IsAlarmEnabled);
        Assert.False(userAfterMasterToggle.IsGradeWatcherEnabled);

        // 3. Toggle master alarm ON - must NOT force grade watcher ON if user turned it off
        var res3 = await _engine.ToggleMasterAlarmAsync(userId);
        Assert.Contains("включены", res3.AlertText ?? "");

        var userAfterMasterOn = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        Assert.NotNull(userAfterMasterOn);
        Assert.True(userAfterMasterOn.IsAlarmEnabled);
        Assert.False(userAfterMasterOn.IsGradeWatcherEnabled); // Still OFF!
    }

    [Fact]
    public async Task SelectChildAsync_InstantlyUpdatesSelectedStudentAndHash()
    {
        string userId = "parent_test_user";
        _db.Users.Add(new UserEntity
        {
            Id = userId,
            Role = "Parent",
            StudentId = "child_1",
            SelectedStudentId = "child_1",
            LastMarksHash = "child_1:hash123"
        });
        await _db.SaveChangesAsync();

        var diaryService = _sp.GetRequiredService<DiaryInteractionService>();
        bool ok = await diaryService.SelectChildAsync(userId, "child_2");
        Assert.True(ok);

        var updated = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        Assert.NotNull(updated);
        Assert.Equal("child_2", updated.SelectedStudentId);
        Assert.Equal("child_2", updated.StudentId);
        Assert.Equal("child_2:initial", updated.LastMarksHash);
    }

    [Theory]
    [InlineData("660708-2026/2027-1-101-I четверть", "2026-11-02", 1)]
    [InlineData("660708-2026/2027-2-101-II четверть", "2026-10-30", 2)]
    [InlineData("660708-2026/2027-3-101-III четверть", "2026-12-20", 3)]
    [InlineData("660708-2026/2027-4-101-IV четверть", "2027-03-20", 4)]
    [InlineData(null, "2026-09-15", 1)]
    [InlineData(null, "2026-11-15", 2)]
    public void GetMarkQuarter_WithPeriodId_PrefersPeriodIdOverDate(string? periodId, string dateStr, int expectedQ)
    {
        var mark = new GosuslugiMarkItem
        {
            PeriodId = periodId ?? "",
            Date = dateStr,
            Value = "5"
        };
        int q = DiaryInteractionService.GetMarkQuarter(mark);
        Assert.Equal(expectedQ, q);
    }

    [Fact]
    public async Task GetSubjectGradesSummaryAsync_TargetQuarterEmpty_DoesNotDumpQuarter1MarksIntoQuarter2()
    {
        string userId = "test_quarter_filter_user";
        string studentId = "student_q_filter_1";
        var enc = _sp.GetRequiredService<ITokenEncryptionService>();
        var user = new UserEntity
        {
            Id = userId,
            StudentId = studentId,
            AccessToken = enc.Encrypt("mock_access_token_123"),
            Role = "Student",
            HasAcceptedPrivacyPolicy = true
        };
        _db.Users.Add(user);

        // Prepopulate Cache with only Q1 marks (September)
        var q1Marks = new List<GosuslugiMarkItem>
        {
            new() { Id = "m1", SubjectName = "Алгебра", Date = "2026-09-15", Value = "5", Weight = 1, PeriodId = "I четверть" },
            new() { Id = "m2", SubjectName = "Геометрия", Date = "2026-09-20", Value = "4", Weight = 1, PeriodId = "I четверть" }
        };
        _db.Cache.Add(new CachedData
        {
            CacheKey = $"marks_db:{userId}_{studentId}",
            JsonData = JsonSerializer.Serialize(q1Marks, DiaryClient.DatamartJsonOptions),
            UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        var diaryService = _sp.GetRequiredService<DiaryInteractionService>();

        // 1. Query Quarter 1 -> Should return 2 subjects
        var q1Summary = await diaryService.GetSubjectGradesSummaryAsync(userId, quarter: 1, forceRefresh: false);
        Assert.Equal(2, q1Summary.Count);
        Assert.Contains(q1Summary, s => s.SubjectName == "Алгебра" && s.AverageGpa == 5.0);

        // 2. Query Quarter 2 -> Must be empty! NOT contain Q1 marks
        var q2Summary = await diaryService.GetSubjectGradesSummaryAsync(userId, quarter: 2, forceRefresh: false);
        Assert.Empty(q2Summary);

        // 3. Render marks string for Q2 -> must report no marks
        var q2String = await diaryService.GetMarksStringAsync(userId, quarter: 2, forceRefresh: false);
        Assert.Contains("2-ю четверть пока нет", q2String);
        Assert.DoesNotContain("Алгебра", q2String);
    }

    [Fact]
    public async Task BuildHypotheticalGradeScreenAsync_RendersSimulatedMarksAndRecoveryPlan()
    {
        string userId = "test_student_sim";
        var user = new UserEntity
        {
            Id = userId,
            StudentId = "student_sim_1",
            AccessToken = "valid_tok",
            Role = "Student",
            HasAcceptedPrivacyPolicy = true
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var memCache = _sp.GetRequiredService<IMemoryCache>();
        int currentQ = DiaryInteractionService.GetCurrentQuarter();
        string memKey = $"user_marks:{userId}_{user.StudentId}:q{currentQ}";

        var mockSubjects = new List<DiaryInteractionService.SubjectGradeSummary>
        {
            new()
            {
                SubjectName = "Алгебра",
                AverageGpa = 3.60,
                FinalForecast = 4,
                WeightedSum = 36,
                TotalWeight = 10,
                StatusEmoji = "🟡"
            }
        };
        memCache.Set(memKey, mockSubjects);

        // 1. Check subject target options screen has simulation buttons
        var optionsScreen = await _engine.BuildSubjectTargetOptionsScreenAsync(userId, 0, "telegram");
        Assert.NotNull(optionsScreen);
        Assert.Contains("Алгебра", optionsScreen.Text);
        Assert.Contains(optionsScreen.Buttons, row => row.Any(b => b.CallbackData == "cmd_calc_sim:0:2:1:1"));

        // 2. Simulate getting a 2 with weight 2 (control work)
        var simScreen = await _engine.BuildHypotheticalGradeScreenAsync(userId, 0, grade: 2, weight: 2, count: 1, "telegram");
        Assert.NotNull(simScreen);
        Assert.Contains("Симулятор невыставленной оценки", simScreen.Text);
        Assert.Contains("Алгебра", simScreen.Text);
        Assert.Contains("🔴 *2*", simScreen.Text);
        Assert.Contains("вес *2*", simScreen.Text);
        Assert.Contains("`4` ➡️ *3* ⚠️", simScreen.Text);
        Assert.Contains("итоговая оценка за четверть упадёт", simScreen.Text);
        Assert.Contains("Как удержать оценку «4»", simScreen.Text);
        Assert.Contains("3* пятерок за ответы на уроках", simScreen.Text);

        // Verify simulation buttons (grades, weights, count, navigation)
        Assert.Contains(simScreen.Buttons, row => row.Any(b => b.CallbackData == "cmd_calc_sim:0:2:2:1"));
        Assert.Contains(simScreen.Buttons, row => row.Any(b => b.CallbackData == "cmd_calc_sim:0:5:2:1"));
        Assert.Contains(simScreen.Buttons, row => row.Any(b => b.CallbackData == "cmd_calc_subj:0"));
        Assert.Contains(simScreen.Buttons, row => row.Any(b => b.CallbackData == "cmd_back_main"));
    }
}
