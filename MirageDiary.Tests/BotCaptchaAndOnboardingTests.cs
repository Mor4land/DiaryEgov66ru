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
using MirageDiary.Server.Data;
using MirageDiary.Server.Models;
using MirageDiary.Server.Services;
using Xunit;

namespace MirageDiary.Tests;

public class TestBotPlatformAdapter : IBotPlatformAdapter
{
    public string Platform { get; set; } = "telegram";
    public BotScreen? LastRenderedScreen { get; private set; }
    public string? LastAnswerAlertText { get; private set; }
    public bool? LastAnswerShowAlert { get; private set; }
    public string? LastTextMessage { get; private set; }

    public Task RenderScreenAsync(long chatId, string? messageId, BotScreen screen)
    {
        LastRenderedScreen = screen;
        return Task.CompletedTask;
    }

    public Task AnswerCallbackAsync(string callbackId, string? alertText = null, bool showAlert = false)
    {
        LastAnswerAlertText = alertText;
        LastAnswerShowAlert = showAlert;
        return Task.CompletedTask;
    }

    public Task SendTextMessageAsync(long chatId, string text)
    {
        LastTextMessage = text;
        return Task.CompletedTask;
    }

    public Task<bool> EnsureChannelSubscribedAsync(long chatId, string userId) => Task.FromResult(true);

    public bool WasChatHistoryDeleted { get; private set; }

    public Task DeleteChatHistoryAsync(long chatId)
    {
        WasChatHistoryDeleted = true;
        return Task.CompletedTask;
    }
}

public class BotCaptchaAndOnboardingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _sp;
    private readonly AppDbContext _db;
    private readonly DiaryBotWorkflowEngine _engine;
    private readonly BotCaptchaService _captchaService;
    private readonly IMemoryCache _cache;

    public BotCaptchaAndOnboardingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(opts => opts.UseSqlite(_connection));
        services.AddMemoryCache(options => { options.SizeLimit = 10000; });
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
        services.AddSingleton<BotCaptchaService>();
        services.AddScoped<DiaryInteractionService>();
        services.AddScoped<DiaryBotWorkflowEngine>();

        _sp = services.BuildServiceProvider();
        _db = _sp.GetRequiredService<AppDbContext>();
        _db.Database.EnsureCreated();

        _cache = _sp.GetRequiredService<IMemoryCache>();
        _captchaService = _sp.GetRequiredService<BotCaptchaService>();
        _engine = _sp.GetRequiredService<DiaryBotWorkflowEngine>();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        _sp.Dispose();
    }

    [Fact]
    public void BotCaptchaService_GeneratesValidScreenAndValidatesCorrectAnswer()
    {
        string userId = "test_captcha_user_1";
        var screen = _captchaService.GenerateCaptchaScreen(userId);

        Assert.NotNull(screen);
        Assert.Contains("Проверка безопасности", screen.Text);
        Assert.Equal(2, screen.Buttons.Count);
        Assert.Equal(2, screen.Buttons[0].Count);
        Assert.Equal(2, screen.Buttons[1].Count);

        // Find the correct answer from cache
        Assert.True(_cache.TryGetValue($"captcha:{userId}", out string? expectedAnswer));
        Assert.False(string.IsNullOrEmpty(expectedAnswer));

        // Wrong answer fails
        bool wrongValidation = _captchaService.ValidateCaptcha(userId, "completely_wrong_answer");
        Assert.False(wrongValidation);

        // Set expected answer explicitly and test validation success
        _captchaService.SetExpectedAnswer(userId, "valid_test_answer");
        bool validValidation = _captchaService.ValidateCaptcha(userId, "valid_test_answer");
        Assert.True(validValidation);

        // Cannot validate again (one-time protection)
        bool reuseValidation = _captchaService.ValidateCaptcha(userId, "valid_test_answer");
        Assert.False(reuseValidation);
    }

    [Fact]
    public async Task CompleteOnboardingWorkflow_FromCaptchaToMainMenu_Success()
    {
        var adapter = new TestBotPlatformAdapter();
        long chatId = 99887766;
        string userId = "new_student_42";

        // 1. Initial /start command -> must show Captcha
        await _engine.ProcessMessageOrCommandAsync(adapter, chatId, userId, "/start");
        Assert.NotNull(adapter.LastRenderedScreen);
        Assert.Contains("Проверка безопасности", adapter.LastRenderedScreen.Text);

        // 2. Submit incorrect captcha -> must alert error and show fresh captcha
        await _engine.ProcessCallbackAsync(adapter, chatId, "msg1", userId, "cq1", "cmd_captcha:bad_answer");
        Assert.Contains("Неверно", adapter.LastAnswerAlertText);
        Assert.Contains("Проверка безопасности", adapter.LastRenderedScreen.Text);

        // 3. Submit correct captcha
        _captchaService.SetExpectedAnswer(userId, "correct_cat");
        await _engine.ProcessCallbackAsync(adapter, chatId, "msg1", userId, "cq2", "cmd_captcha:correct_cat");
        Assert.Contains("успешно пройдена", adapter.LastAnswerAlertText);

        // Verify database updated
        var user = await _db.Users.FirstAsync(u => u.Id == userId);
        Assert.True(user.HasPassedCaptcha);
        Assert.False(user.HasAcceptedPrivacyPolicy);

        // Screen should now be Privacy Policy
        Assert.NotNull(adapter.LastRenderedScreen);
        Assert.Contains("Политика обработки персональных данных", adapter.LastRenderedScreen.Text);

        // 4. Accept Privacy Policy -> should advance to Step 1 of Onboarding (Shift selection)
        await _engine.ProcessCallbackAsync(adapter, chatId, "msg1", userId, "cq3", "cmd_accept_privacy");
        user = await _db.Users.FirstAsync(u => u.Id == userId);
        Assert.True(user.HasAcceptedPrivacyPolicy);
        Assert.False(user.HasCompletedOnboarding);

        Assert.NotNull(adapter.LastRenderedScreen);
        Assert.Contains("Смена обучения", adapter.LastRenderedScreen.Text);

        // 5. Select 2nd shift -> should advance to Step 2 (Alarm setup) with shift 2 defaults
        await _engine.ProcessCallbackAsync(adapter, chatId, "msg1", userId, "cq4", "cmd_onboard_shift:2");
        user = await _db.Users.FirstAsync(u => u.Id == userId);
        Assert.Equal(2, user.Shift);

        Assert.NotNull(adapter.LastRenderedScreen);
        Assert.Contains("Умный будильник", adapter.LastRenderedScreen.Text);
        Assert.Contains("12:00", adapter.LastRenderedScreen.Text); // Shift 2 schedule reminder

        // 6. Select "Включить всё" preset -> completes onboarding and shows Finish screen
        await _engine.ProcessCallbackAsync(adapter, chatId, "msg1", userId, "cq5", "cmd_onboard_alarm:all");
        user = await _db.Users.FirstAsync(u => u.Id == userId);
        Assert.True(user.HasCompletedOnboarding);
        Assert.True(user.IsAlarmEnabled);
        Assert.True(user.IsScheduleReminderEnabled);
        Assert.Equal("12:00", user.ScheduleReminderTime);
        Assert.Equal("19:00", user.HomeworkReminderTime);
        Assert.Equal("21:00", user.EquipmentReminderTime);

        Assert.NotNull(adapter.LastRenderedScreen);
        Assert.Contains("Настройка успешно завершена", adapter.LastRenderedScreen.Text);
        Assert.Contains("Войти через Госуслуги", adapter.LastRenderedScreen.Buttons[0][0].Text);

        // 7. Subsequent /start or /menu opens Main Menu directly
        await _engine.ProcessMessageOrCommandAsync(adapter, chatId, userId, "/start");
        Assert.NotNull(adapter.LastRenderedScreen);
        Assert.Contains("Mirage Diary", adapter.LastRenderedScreen.Text);
    }

    [Fact]
    public async Task LegacyActiveUser_BypassesCaptchaAndOnboarding()
    {
        var adapter = new TestBotPlatformAdapter();
        long chatId = 11223344;
        string userId = "legacy_user_1";

        // Setup user as already having accepted privacy policy
        var user = new UserEntity
        {
            Id = userId,
            HasAcceptedPrivacyPolicy = true,
            PrivacyPolicyAcceptedAtUtc = DateTime.UtcNow,
            AccessToken = "valid_cached_token"
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        // User types /start -> should immediately show main menu
        await _engine.ProcessMessageOrCommandAsync(adapter, chatId, userId, "/start");
        Assert.NotNull(adapter.LastRenderedScreen);
        Assert.Contains("Mirage Diary", adapter.LastRenderedScreen.Text);
        Assert.DoesNotContain("Капча", adapter.LastRenderedScreen.Text);
        Assert.DoesNotContain("Смена обучения", adapter.LastRenderedScreen.Text);
    }

    [Fact]
    public async Task AlarmMenu_SupportsShiftToggleAndWizardRestart()
    {
        var adapter = new TestBotPlatformAdapter();
        long chatId = 55443322;
        string userId = "alarm_test_user";

        var user = new UserEntity
        {
            Id = userId,
            HasPassedCaptcha = true,
            HasAcceptedPrivacyPolicy = true,
            HasCompletedOnboarding = true,
            Shift = 1
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        // Toggle shift via callback
        await _engine.ProcessCallbackAsync(adapter, chatId, "msg1", userId, "cq1", "cmd_alarm_toggle_shift");
        user = await _db.Users.FirstAsync(u => u.Id == userId);
        Assert.Equal(2, user.Shift);

        // Click restart onboarding -> opens Shift screen
        await _engine.ProcessCallbackAsync(adapter, chatId, "msg1", userId, "cq2", "cmd_onboard_restart");
        Assert.NotNull(adapter.LastRenderedScreen);
        Assert.Contains("Смена обучения", adapter.LastRenderedScreen.Text);
    }

    [Fact]
    public async Task StopCommand_WipesUserDataAndDeletesChatHistory()
    {
        var adapter = new TestBotPlatformAdapter();
        long chatId = 11223344;
        string userId = "user_to_stop_1";

        var user = new UserEntity
        {
            Id = userId,
            HasPassedCaptcha = true,
            HasAcceptedPrivacyPolicy = true,
            HasCompletedOnboarding = true,
            AccessToken = "valid_token_123",
            Username = "student_stop"
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        // Send /stop command
        await _engine.ProcessMessageOrCommandAsync(adapter, chatId, userId, "/stop");

        // Verify user is completely removed from database
        var deletedUser = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        Assert.Null(deletedUser);

        // Verify conversation history deletion was requested
        Assert.True(adapter.WasChatHistoryDeleted);

        // Verify farewell / notification text was sent
        Assert.NotNull(adapter.LastTextMessage);
        Assert.Contains("остановлена", adapter.LastTextMessage);
        Assert.Contains("удалены", adapter.LastTextMessage);
    }

    [Fact]
    public async Task HandleUserStopAsync_ForMaxUser_WipesDataAndDeletesChatHistory()
    {
        var adapter = new TestBotPlatformAdapter { Platform = "max" };
        long chatId = 99881122;
        string maxUserId = $"max_{chatId}";

        var user = new UserEntity
        {
            Id = maxUserId,
            Platform = "max",
            HasPassedCaptcha = true,
            HasAcceptedPrivacyPolicy = true,
            HasCompletedOnboarding = true,
            AccessToken = "max_token_abc"
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        // Trigger stop handler (e.g. from BotStoppedUpdate or DialogRemovedUpdate)
        await _engine.HandleUserStopAsync(adapter, chatId, maxUserId);

        var deletedUser = await _db.Users.FirstOrDefaultAsync(u => u.Id == maxUserId);
        Assert.Null(deletedUser);
        Assert.True(adapter.WasChatHistoryDeleted);
    }

    [Fact]
    public async Task MaxBot_OnboardingFlow_FromCaptchaToFinish_Success()
    {
        var adapter = new TestBotPlatformAdapter { Platform = "max" };
        long chatId = 44556677;
        string maxUserId = $"max_{chatId}";

        // 1. Initial /start in MAX -> Shows Captcha
        await _engine.ProcessMessageOrCommandAsync(adapter, chatId, maxUserId, "/start");
        Assert.NotNull(adapter.LastRenderedScreen);
        Assert.Contains("Проверка безопасности", adapter.LastRenderedScreen.Text);

        // 2. Submit correct captcha
        _captchaService.SetExpectedAnswer(maxUserId, "cat");
        await _engine.ProcessCallbackAsync(adapter, chatId, "msg1", maxUserId, "cq1", "cmd_captcha:cat");
        Assert.NotNull(adapter.LastRenderedScreen);
        Assert.Contains("Политика обработки персональных данных", adapter.LastRenderedScreen.Text);

        // 3. Accept Privacy Policy -> Step 1 (Shift)
        await _engine.ProcessCallbackAsync(adapter, chatId, "msg1", maxUserId, "cq2", "cmd_accept_privacy");
        Assert.NotNull(adapter.LastRenderedScreen);
        Assert.Contains("Смена обучения", adapter.LastRenderedScreen.Text);

        // 4. Select Shift 1 -> Step 2 (Alarm)
        await _engine.ProcessCallbackAsync(adapter, chatId, "msg1", maxUserId, "cq3", "cmd_onboard_shift:1");
        Assert.NotNull(adapter.LastRenderedScreen);
        Assert.Contains("Умный будильник", adapter.LastRenderedScreen.Text);

        // 5. Select All -> Finish screen with MAX App button
        await _engine.ProcessCallbackAsync(adapter, chatId, "msg1", maxUserId, "cq4", "cmd_onboard_alarm:all");
        Assert.NotNull(adapter.LastRenderedScreen);
        Assert.Contains("Настройка успешно завершена", adapter.LastRenderedScreen.Text);
        Assert.NotNull(adapter.LastRenderedScreen.Buttons[0][0].MaxWebApp);
        Assert.Contains("Войти через Госуслуги", adapter.LastRenderedScreen.Buttons[0][0].Text);

        // Verify DB entity flags
        var user = await _db.Users.FirstAsync(u => u.Id == maxUserId);
        Assert.True(user.HasPassedCaptcha);
        Assert.True(user.HasAcceptedPrivacyPolicy);
        Assert.True(user.HasCompletedOnboarding);
    }
}
