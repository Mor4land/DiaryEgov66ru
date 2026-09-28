using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MirageDiary.Core;
using MirageDiary.Server.Data;
using MirageDiary.Server.Models;
using MirageDiary.Server.Services;
using Xunit;

namespace MirageDiary.Tests;

public class DiaryBotWorkflowEngineTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _sp;
    private readonly AppDbContext _db;
    private readonly DiaryBotWorkflowEngine _engine;

    public DiaryBotWorkflowEngineTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(opts => opts.UseSqlite(_connection));
        services.AddMemoryCache();
        services.AddLogging();

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
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        _sp.Dispose();
    }

    [Fact]
    public async Task BuildSettingsMenuScreenAsync_Telegram_ReturnsInlineLayout()
    {
        string userId = "tg_12345";
        _db.Users.Add(new UserEntity { Id = userId, IsAlarmEnabled = true });
        await _db.SaveChangesAsync();

        var screen = await _engine.BuildSettingsMenuScreenAsync(userId, "telegram");

        Assert.Equal("banner_settings.png", screen.BannerName);
        Assert.Contains("⚙️ *Настройки*", screen.Text);
        Assert.Contains("• ⏰ Будильник: `🟢 Включен`", screen.Text);
        Assert.Contains($"• 👤 ID: `{userId}`", screen.Text);

        Assert.Equal(4, screen.Buttons.Count);

        // Row 0: Alarm toggle button pointing to cmd_alarm_menu
        Assert.Single(screen.Buttons[0]);
        Assert.Equal("cmd_alarm_menu", screen.Buttons[0][0].CallbackData);
        Assert.Contains("🟢 Вкл", screen.Buttons[0][0].Text);

        // Row 1: WebApp for Gosuslugi + Link to MAX
        Assert.Equal(2, screen.Buttons[1].Count);
        Assert.StartsWith("https://t.me/MirageDiaryBot/app", screen.Buttons[1][0].WebAppUrl);
        Assert.Contains("userId=tg_12345", screen.Buttons[1][0].WebAppUrl);
        Assert.Equal("cmd_link_account", screen.Buttons[1][1].CallbackData);
        Assert.Contains("MAX", screen.Buttons[1][1].Text);

        // Row 2: Help + Delete account
        Assert.Equal(2, screen.Buttons[2].Count);
        Assert.Equal("cmd_help", screen.Buttons[2][0].CallbackData);
        Assert.Equal("cmd_delete_account", screen.Buttons[2][1].CallbackData);

        // Row 3: Back to main menu
        Assert.Single(screen.Buttons[3]);
        Assert.Equal("cmd_back_main", screen.Buttons[3][0].CallbackData);
    }

    [Fact]
    public async Task BuildSettingsMenuScreenAsync_Max_ReturnsMaxAppAndTelegramLink()
    {
        string userId = "max_999";
        _db.Users.Add(new UserEntity { Id = userId, IsAlarmEnabled = false });
        await _db.SaveChangesAsync();

        var screen = await _engine.BuildSettingsMenuScreenAsync(userId, "max");

        Assert.Contains("• ⏰ Будильник: `🔴 Выключен`", screen.Text);
        Assert.Equal("cmd_alarm_menu", screen.Buttons[0][0].CallbackData);
        Assert.Contains("🔴 Выкл", screen.Buttons[0][0].Text);

        // Row 1: OpenApp for MAX + Link to Telegram
        Assert.Equal("mirage_max_bot", screen.Buttons[1][0].MaxWebApp);
        Assert.Equal(userId, screen.Buttons[1][0].MaxPayload);
        Assert.Equal("cmd_link_account", screen.Buttons[1][1].CallbackData);
        Assert.Contains("Telegram", screen.Buttons[1][1].Text);
    }

    [Fact]
    public async Task BuildAlarmMenuScreenAsync_ReturnsAllSubmenuButtonsAndSettingsBack()
    {
        string userId = "user_alarm_test";
        _db.Users.Add(new UserEntity
        {
            Id = userId,
            IsAlarmEnabled = true,
            IsScheduleReminderEnabled = true,
            ScheduleReminderTime = "07:30",
            IsHomeworkReminderEnabled = true,
            HomeworkReminderTime = "17:00",
            IsEquipmentReminderEnabled = true,
            EquipmentReminderTime = "20:30"
        });
        await _db.SaveChangesAsync();

        var screen = await _engine.BuildAlarmMenuScreenAsync(userId);

        Assert.Equal("banner_alarm.png", screen.BannerName);
        Assert.Contains("⏰ *Умный будильник*", screen.Text);
        Assert.Contains("`07:30`", screen.Text);
        Assert.Contains("`17:00`", screen.Text);
        Assert.Contains("`20:30`", screen.Text);

        Assert.Equal(5, screen.Buttons.Count);

        // Row 0: Master toggle
        Assert.Equal("cmd_alarm_toggle_master", screen.Buttons[0][0].CallbackData);

        // Row 1: Grades & Schedule
        Assert.Equal("cmd_alarm_grades_toggle", screen.Buttons[1][0].CallbackData);
        Assert.Equal("cmd_alarm_sched", screen.Buttons[1][1].CallbackData);

        // Row 2: Homework & Equipment
        Assert.Equal("cmd_alarm_hw", screen.Buttons[2][0].CallbackData);
        Assert.Equal("cmd_alarm_equip", screen.Buttons[2][1].CallbackData);

        // Row 3: Check now
        Assert.Equal("cmd_alarm_check_now", screen.Buttons[3][0].CallbackData);

        // Row 4: Back to Settings & Main Menu
        Assert.Equal("cmd_settings", screen.Buttons[4][0].CallbackData);
        Assert.Equal("cmd_back_main", screen.Buttons[4][1].CallbackData);
    }

    [Fact]
    public async Task ToggleMasterAlarmAsync_FlipsAlarmStatusAndReminders()
    {
        string userId = "toggle_test_user";
        var user = new UserEntity
        {
            Id = userId,
            IsAlarmEnabled = true,
            IsScheduleReminderEnabled = true,
            IsHomeworkReminderEnabled = true,
            IsEquipmentReminderEnabled = true
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var result = await _engine.ToggleMasterAlarmAsync(userId);
        Assert.NotNull(result.Screen);

        var updated = await _db.Users.FirstAsync(u => u.Id == userId);
        Assert.False(updated.IsAlarmEnabled);
        Assert.False(updated.IsScheduleReminderEnabled);
        Assert.False(updated.IsHomeworkReminderEnabled);
        Assert.False(updated.IsEquipmentReminderEnabled);

        // Toggle back
        await _engine.ToggleMasterAlarmAsync(userId);
        var restored = await _db.Users.FirstAsync(u => u.Id == userId);
        Assert.True(restored.IsAlarmEnabled);
        Assert.True(restored.IsScheduleReminderEnabled);
    }

    [Fact]
    public async Task ReminderSubmenus_HaveCorrectCallbacksAndAlarmBackButtons()
    {
        string userId = "reminder_test_user";
        _db.Users.Add(new UserEntity { Id = userId });
        await _db.SaveChangesAsync();

        // 1. Schedule reminder menu
        var schedScreen = await _engine.BuildScheduleReminderMenuScreenAsync(userId);
        Assert.Contains(schedScreen.Buttons, row => row.Exists(b => b.CallbackData == "cmd_alarm_sched_toggle"));
        Assert.Contains(schedScreen.Buttons, row => row.Exists(b => b.CallbackData == "cmd_alarm_sched_time:07:30"));
        Assert.Contains(schedScreen.Buttons, row => row.Exists(b => b.CallbackData == "cmd_alarm_sched_custom"));
        Assert.Contains(schedScreen.Buttons, row => row.Exists(b => b.CallbackData == "cmd_alarm_menu"));

        // 2. Homework reminder menu
        var hwScreen = await _engine.BuildHomeworkReminderMenuScreenAsync(userId);
        Assert.Contains(hwScreen.Buttons, row => row.Exists(b => b.CallbackData == "cmd_alarm_hw_toggle"));
        Assert.Contains(hwScreen.Buttons, row => row.Exists(b => b.CallbackData == "cmd_alarm_hw_time:17:00"));
        Assert.Contains(hwScreen.Buttons, row => row.Exists(b => b.CallbackData == "cmd_alarm_hw_custom"));
        Assert.Contains(hwScreen.Buttons, row => row.Exists(b => b.CallbackData == "cmd_alarm_menu"));

        // 3. Equipment reminder menu
        var equipScreen = await _engine.BuildEquipmentReminderMenuScreenAsync(userId);
        Assert.Contains(equipScreen.Buttons, row => row.Exists(b => b.CallbackData == "cmd_alarm_equip_toggle"));
        Assert.Contains(equipScreen.Buttons, row => row.Exists(b => b.CallbackData == "cmd_alarm_equip_time:20:30"));
        Assert.Contains(equipScreen.Buttons, row => row.Exists(b => b.CallbackData == "cmd_alarm_equip_custom"));
        Assert.Contains(equipScreen.Buttons, row => row.Exists(b => b.CallbackData == "cmd_alarm_menu"));
    }

    [Fact]
    public void BuildDeleteAccountConfirmationScreen_HasProperButtonsAndLegalWarning()
    {
        var screen = _engine.BuildDeleteAccountConfirmationScreen();

        Assert.Contains("152-ФЗ РФ", screen.Text);
        Assert.Equal("banner_settings.png", screen.BannerName);

        Assert.Single(screen.Buttons);
        Assert.Equal(2, screen.Buttons[0].Count);
        Assert.Equal("cmd_delete_account_confirm", screen.Buttons[0][0].CallbackData);
        Assert.Equal("cmd_settings", screen.Buttons[0][1].CallbackData);
        Assert.Equal("❌ Отмена", screen.Buttons[0][1].Text);
    }

    [Fact]
    public void BuildHelpScreen_HasMainMenuBackButton()
    {
        var screen = _engine.BuildHelpScreen();

        Assert.Contains("ℹ️ *Справка по Mirage Diary*", screen.Text);
        Assert.Single(screen.Buttons);
        Assert.Equal("cmd_settings", screen.Buttons[0][0].CallbackData);
        Assert.Equal("cmd_back_main", screen.Buttons[0][1].CallbackData);
    }

    [Fact]
    public async Task BuildHomeworkScreenAsync_GeneratesDynamicDatesAndButtons()
    {
        string userId = "hw_nav_user";
        var localNow = DateTime.UtcNow.AddHours(5).Date;
        string todayStr = localNow.ToString("yyyy-MM-dd");
        string prevStr = localNow.AddDays(-1).ToString("yyyy-MM-dd");
        string nextStr = localNow.AddDays(1).ToString("yyyy-MM-dd");

        var screen = await _engine.BuildHomeworkScreenAsync(userId, null);

        Assert.Equal("banner_homework.png", screen.BannerName);
        Assert.Equal(2, screen.Buttons.Count);

        // Row 0: Yesterday, Today, Tomorrow
        Assert.Equal(3, screen.Buttons[0].Count);
        Assert.Equal("⬅️ Вчера", screen.Buttons[0][0].Text);
        Assert.Equal($"cmd_homework:{prevStr}", screen.Buttons[0][0].CallbackData);

        Assert.Equal("• Сегодня •", screen.Buttons[0][1].Text);
        Assert.Equal($"cmd_homework:{todayStr}", screen.Buttons[0][1].CallbackData);

        Assert.Equal("Завтра ➡️", screen.Buttons[0][2].Text);
        Assert.Equal($"cmd_homework:{nextStr}", screen.Buttons[0][2].CallbackData);

        // Row 1: Refresh, Main menu
        Assert.Equal(2, screen.Buttons[1].Count);
        Assert.StartsWith("cmd_homework_refresh:", screen.Buttons[1][0].CallbackData);
        Assert.Equal("cmd_back_main", screen.Buttons[1][1].CallbackData);
    }

    [Fact]
    public async Task BuildScheduleScreenAsync_GeneratesDynamicDatesAndButtons()
    {
        string userId = "sched_nav_user";
        var localNow = DateTime.UtcNow.AddHours(5).Date;
        string todayStr = localNow.ToString("yyyy-MM-dd");
        string prevStr = localNow.AddDays(-1).ToString("yyyy-MM-dd");
        string nextStr = localNow.AddDays(1).ToString("yyyy-MM-dd");

        var screen = await _engine.BuildScheduleScreenAsync(userId, null);

        Assert.Equal("banner_schedule.png", screen.BannerName);
        Assert.Equal(2, screen.Buttons.Count);

        // Row 0: Yesterday, Today, Tomorrow
        Assert.Equal(3, screen.Buttons[0].Count);
        Assert.Equal("⬅️ Вчера", screen.Buttons[0][0].Text);
        Assert.Equal($"cmd_schedule:{prevStr}", screen.Buttons[0][0].CallbackData);

        Assert.Equal("• Сегодня •", screen.Buttons[0][1].Text);
        Assert.Equal($"cmd_schedule:{todayStr}", screen.Buttons[0][1].CallbackData);

        Assert.Equal("Завтра ➡️", screen.Buttons[0][2].Text);
        Assert.Equal($"cmd_schedule:{nextStr}", screen.Buttons[0][2].CallbackData);

        // Row 1: Refresh, Main menu
        Assert.Equal(2, screen.Buttons[1].Count);
        Assert.StartsWith("cmd_schedule_refresh:", screen.Buttons[1][0].CallbackData);
        Assert.Equal("cmd_back_main", screen.Buttons[1][1].CallbackData);
    }

    [Fact]
    public async Task MainMenu_UnauthorizedUser_HasWebAppButton_NotExternalLink()
    {
        string userId = "unauth_test_user";
        var user = new UserEntity
        {
            Id = userId,
            HasAcceptedPrivacyPolicy = true,
            AccessToken = string.Empty // Unauthorized
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var screen = await _engine.BuildMainMenuScreenAsync(userId, "telegram");

        // First row must contain the WebApp authorization button, NOT a normal external link
        Assert.NotNull(screen.Buttons);
        Assert.NotEmpty(screen.Buttons);
        var authBtn = screen.Buttons[0][0];
        Assert.Contains("Войти через Госуслуги", authBtn.Text);
        Assert.NotNull(authBtn.WebAppUrl);
        Assert.Null(authBtn.Url); // Crucial: must NOT be external browser Link
    }

    [Fact]
    public async Task Calculator_UnauthorizedUser_PromptsAuthorization_WithWebApp()
    {
        string userId = "calc_unauth_user";
        var user = new UserEntity
        {
            Id = userId,
            HasAcceptedPrivacyPolicy = true,
            AccessToken = string.Empty // Unauthorized
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var screen = await _engine.BuildGradeCalculatorSubjectsScreenAsync(userId, false, "telegram");

        Assert.Contains("необходимо войти в аккаунт Госуслуг", screen.Text);
        Assert.NotNull(screen.Buttons);
        Assert.NotEmpty(screen.Buttons);
        var authBtn = screen.Buttons[0][0];
        Assert.Contains("Войти через Госуслуги", authBtn.Text);
        Assert.NotNull(authBtn.WebAppUrl);
        Assert.Null(authBtn.Url);
    }

    [Theory]
    [InlineData("2026-09-15", 1)]
    [InlineData("2026-10-30", 1)]
    [InlineData("2026-11-05", 2)]
    [InlineData("2026-12-25", 2)]
    [InlineData("2026-01-15", 3)]
    [InlineData("2026-03-20", 3)]
    [InlineData("2026-04-10", 4)]
    [InlineData("2026-05-25", 4)]
    [InlineData("1727170000", 1)] // Sep 24, 2026 Unix timestamp
    [InlineData("2026-09-24T14:30:00Z", 1)]
    public void GetMarkQuarter_HandlesMultipleFormats_Correctly(string dateStr, int expectedQ)
    {
        int q = DiaryInteractionService.GetMarkQuarter(dateStr);
        Assert.Equal(expectedQ, q);
    }

    [Fact]
    public async Task DualPlatform_BannerAndSettings_WorkCorrectly()
    {
        string tgId = "tg_user_100";
        string maxId = "max_user_200";

        var tgUser = new UserEntity
        {
            Id = tgId,
            Platform = "telegram",
            LinkedUserId = maxId,
            HasAcceptedPrivacyPolicy = true,
            HasCompletedOnboarding = true,
            AccessToken = "valid_token"
        };
        var maxUser = new UserEntity
        {
            Id = maxId,
            Platform = "max",
            LinkedUserId = tgId,
            HasAcceptedPrivacyPolicy = true,
            HasCompletedOnboarding = true,
            AccessToken = "valid_token"
        };

        _db.Users.AddRange(tgUser, maxUser);
        await _db.SaveChangesAsync();

        // 1. Dual registration check
        var (hasBoth, peer) = await _engine.CheckDualPlatformRegistrationAsync(tgUser);
        Assert.True(hasBoth);
        Assert.NotNull(peer);
        Assert.Equal(maxId, peer.Id);

        // 2. Main menu when channel is unset - must show banner & selection buttons
        var menuScreen = await _engine.BuildMainMenuScreenAsync(tgId, "telegram");
        Assert.Contains("Куда присылать уведомления?", menuScreen.Text);
        Assert.Contains(menuScreen.Buttons, row => row.Any(b => b.CallbackData == "cmd_set_notify_channel:telegram"));
        Assert.Contains(menuScreen.Buttons, row => row.Any(b => b.CallbackData == "cmd_set_notify_channel:max"));
        Assert.Contains(menuScreen.Buttons, row => row.Any(b => b.CallbackData == "cmd_set_notify_channel:all"));

        // 3. User chooses Telegram
        var prefRes = await _engine.SetNotificationChannelPreferenceAsync(tgId, "telegram", "telegram");
        Assert.Contains("Telegram", prefRes.AlertText ?? "");

        var updatedTg = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == tgId);
        var updatedMax = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == maxId);
        Assert.Equal("telegram", updatedTg!.NotificationChannel);
        Assert.Equal("telegram", updatedMax!.NotificationChannel);

        // 4. Main menu after setting channel - banner is gone
        var menuAfter = await _engine.BuildMainMenuScreenAsync(tgId, "telegram");
        Assert.DoesNotContain("Куда присылать уведомления?", menuAfter.Text);

        // 5. Settings screen shows preferred channel and button to change
        var settingsScreen = await _engine.BuildSettingsMenuScreenAsync(tgId, "telegram");
        Assert.Contains("• 📨 Доставка уведомлений: `✈️ Только Telegram`", settingsScreen.Text);
        Assert.Contains(settingsScreen.Buttons, row => row.Any(b => b.CallbackData == "cmd_settings_notify_channel"));

        // 6. Dedicated channel selection screen
        var channelScreen = await _engine.BuildNotificationChannelSelectScreenAsync(tgId, "telegram");
        Assert.Contains("Выбор мессенджера для уведомлений", channelScreen.Text);
        Assert.Contains("✅ ✈️ Только в Telegram", channelScreen.Buttons[0][0].Text);
    }
}
