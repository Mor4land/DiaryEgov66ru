using System;
using System.Collections.Generic;
using System.Linq;
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
}
