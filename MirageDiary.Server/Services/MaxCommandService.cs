using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MirageDiary.Core;
using MirageDiary.Server.Data;
using MirageDiary.Server.Models;
using MAX.Bot.Interfaces;
using MAX.Bot.Interfaces.Models;
using MAX.Bot.Interfaces.Models.Request;
using MAX.Bot.Interfaces.Models.Request.Message;
using MAX.Bot.Interfaces.Models.Request.Message.Attachment;
using MAX.Bot.Interfaces.Models.Request.Message.Attachment.Payloads;

namespace MirageDiary.Server.Services;

public class MaxCommandService
{
    private static readonly ConcurrentDictionary<long, DateTime> _userCooldowns = new();
    private static readonly ConcurrentDictionary<string, DateTime> _profileRefreshCooldown = new();

    private readonly IMaxBotClient _botClient;
    private readonly DiaryInteractionService _diaryService;
    private readonly IConfiguration _config;
    private readonly RoleService _roleService;
    private readonly BotStateService _stateService;
    private readonly MaintenanceModeService _maintenanceService;
    private readonly AppDbContext _db;
    private readonly ILogger<MaxCommandService> _logger;
    private readonly SchoolBellScheduleService _bellSchedule;
    private readonly UserSyncService _userSyncService;
    private readonly ITokenStore _tokenStore;
    private readonly DiaryBotWorkflowEngine _engine;

    private static readonly string[] TriggerWords =
    {
        "циркуль", "линейка", "краски", "альбом", "форма",
        "транспортир", "контурные карты", "кисти", "карандаши", "тетрадь"
    };

    public MaxCommandService(
        IMaxBotClient botClient,
        DiaryInteractionService diaryService,
        IConfiguration config,
        RoleService roleService,
        BotStateService stateService,
        MaintenanceModeService maintenanceService,
        AppDbContext db,
        ILogger<MaxCommandService> logger,
        SchoolBellScheduleService bellSchedule,
        UserSyncService userSyncService,
        ITokenStore tokenStore,
        DiaryBotWorkflowEngine engine)
    {
        _botClient = botClient;
        _diaryService = diaryService;
        _config = config;
        _roleService = roleService;
        _stateService = stateService;
        _maintenanceService = maintenanceService;
        _db = db;
        _logger = logger;
        _bellSchedule = bellSchedule;
        _userSyncService = userSyncService;
        _tokenStore = tokenStore;
        _engine = engine;
    }

    // =========================================================================
    // RATE LIMITING & COOLDOWN
    // =========================================================================

    private bool CheckUserCooldown(long chatId)
    {
        var now = DateTime.UtcNow;
        if (_userCooldowns.TryGetValue(chatId, out var lastTime))
        {
            if ((now - lastTime).TotalSeconds < 1.0)
            {
                return false;
            }
        }
        _userCooldowns[chatId] = now;
        return true;
    }

    // =========================================================================
    // ENTRY POINT
    // =========================================================================

    public async Task HandleUpdateAsync(Update update)
    {
        if (update == null) return;

        try
        {
            if (update is MessageCreatedUpdate msgCreated)
            {
                await HandleMessageCreatedAsync(msgCreated.Message);
            }
            else if (update is MessageCallbackUpdate msgCallback)
            {
                await HandleCallbackAsync(msgCallback);
            }
            else if (update is BotStartedUpdate botStarted)
            {
                long userId = botStarted.User?.Id ?? botStarted.ChatId;
                if (userId != 0)
                {
                    _stateService.ClearState(userId);
                    await ShowMainMenuAsync(userId, $"max_{userId}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing MAX update");
        }
    }

    // =========================================================================
    // MESSAGE HANDLER
    // =========================================================================

    private async Task HandleMessageCreatedAsync(Message msg)
    {
        if (msg == null) return;

        long chatId = msg.Sender?.Id ?? 0;
        if (chatId == 0) return;

        string maxUserId = $"max_{chatId}";
        string? username = msg.Sender?.Username;
        string? firstName = msg.Sender?.FirstName;
        string? lastName = msg.Sender?.LastName;

        var currentUser = await EnsureUserExistsAsync(maxUserId, username, firstName, lastName);
        string text = msg.Body?.Text?.Trim() ?? "";

        if (!CheckUserCooldown(chatId))
        {
            return;
        }

        // /start, /menu, Cancel — reset state and show main menu
        if (text.StartsWith("/start") || text.StartsWith("/menu") || IsCancelCommand(text))
        {
            _stateService.ClearState(chatId);
            await ShowMainMenuAsync(chatId, maxUserId);
            return;
        }

        // Direct /help or / command
        if (text == "/" || text.StartsWith("/help") || text.StartsWith("/помощь") || text.StartsWith("/команды"))
        {
            _stateService.ClearState(chatId);
            await ShowHelpAsync(chatId, maxUserId);
            return;
        }

        // Direct /login or /auth command
        if (text.StartsWith("/login") || text.StartsWith("/auth") || text.StartsWith("/войти"))
        {
            _stateService.ClearState(chatId);
            await ShowLoginPromptAsync(chatId, maxUserId);
            return;
        }

        // Direct /logout command
        if (text.StartsWith("/logout") || text.StartsWith("/выйти"))
        {
            _stateService.ClearState(chatId);
            await ShowLogoutConfirmationAsync(chatId, maxUserId);
            return;
        }



        // File uploads (schedule from HeadTeacher / Admin)
        if (msg.Body?.Attachments != null && msg.Body.Attachments.Any(a => a is ImageAttachment || a is FileAttachment))
        {
            await HandleFileUploadAsync(chatId, maxUserId, msg);
            return;
        }

        // State machine input
        var session = _stateService.GetSession(chatId);
        if (session.State != UserState.Normal)
        {
            await HandleStateInputAsync(chatId, maxUserId, text, session);
            return;
        }

        // Text command shortcuts
        if (text.StartsWith("/homework") || text.StartsWith("/домашка") || text.StartsWith("/дз"))
        {
            var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            string dateArg = parts.Length > 1 ? parts[1].Trim() : "";
            await ExecuteHomeworkAsync(chatId, maxUserId, dateArg);
        }
        else if (text.StartsWith("/schedule") || text.StartsWith("/расписание"))
        {
            var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            string dateArg = parts.Length > 1 ? parts[1].Trim() : "";
            await ExecuteScheduleAsync(chatId, maxUserId, dateArg);
        }
        else if (text.StartsWith("/marks") || text.StartsWith("/grades") || text.StartsWith("/оценки"))
        {
            await ExecuteMarksAsync(chatId, maxUserId);
        }
        else if (text.StartsWith("/calc") || text.StartsWith("/calculator") || text.StartsWith("/калькулятор"))
        {
            await ShowGradeCalculatorSubjectsAsync(chatId, maxUserId);
        }
        else if (text.StartsWith("/profile") || text.StartsWith("/профиль") || text.StartsWith("/me"))
        {
            await ExecuteProfileAsync(chatId, maxUserId);
        }
        else if (text.StartsWith("/now") || text.StartsWith("/bell") || text.StartsWith("/звонок"))
        {
            await ExecuteNowAsync(chatId, maxUserId);
        }
        else if (text.StartsWith("/settings") || text.StartsWith("/настройки"))
        {
            await ShowSettingsMenuAsync(chatId, maxUserId);
        }
        else if (text.StartsWith("/alarm") || text.StartsWith("/будильник"))
        {
            await ExecuteAlarmCheckAsync(chatId, maxUserId);
        }
        else if (text.StartsWith("/subscription") || text.StartsWith("/premium"))
        {
            await EditOrSendMessageAsync(chatId, null,
                "⭐ *MirageDiary*\n\n" +
                "Раздел платной подписки закрыт — все функции бота (расписание, оценки, калькулятор, домашка и уведомления) полностью бесплатны! 🚀");
        }
        else if (text.StartsWith("/link") || text.StartsWith("/sync") || text.StartsWith("/связать"))
        {
            await HandleLinkCommandAsync(chatId, maxUserId, text);
        }
        else if (text.StartsWith("/delete_account"))
        {
            await ShowDeleteAccountConfirmationAsync(chatId, maxUserId, null);
        }
        else if (text.StartsWith("/privacy"))
        {
            await ShowPrivacyPolicyOnboardingAsync(chatId);
        }
        else if (text.StartsWith("/admin"))
        {
            var role = await _roleService.GetUserRoleAsync(maxUserId);
            if (role == "Admin")
                await ShowAdminPanelAsync(chatId, maxUserId);
        }
        else
        {
            // Default response: show menu
            await ShowMainMenuAsync(chatId, maxUserId);
        }
    }

    // ─── File upload (schedule) ───────────────────────────────────────────────
    private async Task HandleFileUploadAsync(long chatId, string maxUserId, Message msg)
    {
        var role = await _roleService.GetUserRoleAsync(maxUserId);
        if (role != "HeadTeacher" && role != "Admin")
        {
            await SendUserMessageAsync(chatId, new SendMessageRequest
            {
                Text = "❌ Только завучи и администраторы могут загружать расписание."
            });
            return;
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == maxUserId);
        if (user == null || string.IsNullOrEmpty(user.OrgName))
        {
            await SendUserMessageAsync(chatId, new SendMessageRequest
            {
                Text = "❌ Вы не привязаны ни к какой школе."
            });
            return;
        }

        var attachment = msg.Body?.Attachments?.FirstOrDefault(a => a is ImageAttachment || a is FileAttachment);
        if (attachment == null) return;

        string fileId = msg.Body?.Mid ?? Guid.NewGuid().ToString();
        string fileType = attachment is ImageAttachment ? "photo" : "document";

        var schedule = await _db.Schedules.FirstOrDefaultAsync(s => s.OrgName == user.OrgName);
        if (schedule == null)
        {
            schedule = new ScheduleEntity { OrgName = user.OrgName };
            _db.Schedules.Add(schedule);
        }
        schedule.FileId = fileId;
        schedule.FileType = fileType;
        schedule.UploadedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await SendUserMessageAsync(chatId, new SendMessageRequest
        {
            Text = $"✅ Расписание для школы *{user.OrgName}* успешно сохранено!\n\n" +
                   $"Теперь ученики смогут получить его через кнопку «📅 Расписание».",
            Format = MessageFormat.Markdown
        });
    }

    // ─── State machine input ──────────────────────────────────────────────────
    private async Task HandleStateInputAsync(long chatId, string maxUserId, string text, UserSession session)
    {
        switch (session.State)
        {
            case UserState.WaitingForHeadTeacherId:
                if (IsCancelCommand(text))
                {
                    _stateService.ClearState(chatId);
                    await SendUserMessageAsync(chatId, new SendMessageRequest
                    {
                        Text = "❌ Назначение завуча отменено.",
                        Format = null
                    });
                    return;
                }
                _stateService.UpdateState(chatId, UserState.WaitingForHeadTeacherOrg, text);
                await SendUserMessageAsync(chatId, new SendMessageRequest
                {
                    Text = "🏫 Теперь введите название школы, к которой привязать этого завуча:\n_(или напишите «Отмена»)_",
                    Format = MessageFormat.Markdown
                });
                break;

            case UserState.WaitingForHeadTeacherOrg:
                if (IsCancelCommand(text))
                {
                    _stateService.ClearState(chatId);
                    await SendUserMessageAsync(chatId, new SendMessageRequest
                    {
                        Text = "❌ Назначение завуча отменено.",
                        Format = null
                    });
                    return;
                }
                await SaveHeadTeacherAsync(chatId, session.TempData ?? "", text);
                _stateService.ClearState(chatId);
                break;

            case UserState.WaitingForBroadcastText:
                if (IsCancelCommand(text))
                {
                    _stateService.ClearState(chatId);
                    await SendUserMessageAsync(chatId, new SendMessageRequest
                    {
                        Text = "❌ Рассылка отменена.",
                        Format = null
                    });
                    return;
                }
                await DoBroadcastAsync(chatId, text);
                _stateService.ClearState(chatId);
                break;

            case UserState.WaitingForScheduleReminderTime:
                if (IsCancelCommand(text))
                {
                    _stateService.ClearState(chatId);
                    await SendUserMessageAsync(chatId, new SendMessageRequest
                    {
                        Text = "❌ Ввод времени отменён.",
                        Format = null
                    });
                    return;
                }
                if (TryParseTime(text, out string schedTime))
                {
                    await SetScheduleReminderTimeAsync(chatId, null, maxUserId, schedTime);
                    _stateService.ClearState(chatId);
                }
                else
                {
                    await SendUserMessageAsync(chatId, new SendMessageRequest
                    {
                        Text = "❌ Неверный формат времени. Введите время в формате ЧЧ:ММ (например, `07:15`):\n_(или напишите «Отмена»)_",
                        Format = MessageFormat.Markdown
                    });
                }
                break;

            case UserState.WaitingForHomeworkReminderTime:
                if (IsCancelCommand(text))
                {
                    _stateService.ClearState(chatId);
                    await SendUserMessageAsync(chatId, new SendMessageRequest
                    {
                        Text = "❌ Ввод времени отменён.",
                        Format = null
                    });
                    return;
                }
                if (TryParseTime(text, out string hwTime))
                {
                    await SetHomeworkReminderTimeAsync(chatId, null, maxUserId, hwTime);
                    _stateService.ClearState(chatId);
                }
                else
                {
                    await SendUserMessageAsync(chatId, new SendMessageRequest
                    {
                        Text = "❌ Неверный формат времени. Введите время в формате ЧЧ:ММ (например, `17:30`):\n_(или напишите «Отмена»)_",
                        Format = MessageFormat.Markdown
                    });
                }
                break;

            case UserState.WaitingForEquipmentReminderTime:
                if (IsCancelCommand(text))
                {
                    _stateService.ClearState(chatId);
                    await SendUserMessageAsync(chatId, new SendMessageRequest
                    {
                        Text = "❌ Ввод времени отменён.",
                        Format = null
                    });
                    return;
                }
                if (TryParseTime(text, out string equipTime))
                {
                    await SetEquipmentReminderTimeAsync(chatId, null, maxUserId, equipTime);
                    _stateService.ClearState(chatId);
                }
                else
                {
                    await SendUserMessageAsync(chatId, new SendMessageRequest
                    {
                        Text = "❌ Неверный формат времени. Введите время в формате ЧЧ:ММ (например, `20:45`):\n_(или напишите «Отмена»)_",
                        Format = MessageFormat.Markdown
                    });
                }
                break;
        }
    }

    // =========================================================================
    // CALLBACK QUERY HANDLER
    // =========================================================================

    private async Task HandleCallbackAsync(MessageCallbackUpdate callbackUpdate)
    {
        var cb = callbackUpdate.Callback;
        if (cb == null) return;

        long chatId = cb.User?.Id ?? callbackUpdate.Message?.Sender?.Id ?? 0;
        if (chatId == 0) return;

        string messageId = callbackUpdate.Message?.Body?.Mid ?? "";
        string maxUserId = $"max_{chatId}";
        string data = cb.Payload ?? "";

        if (!string.IsNullOrEmpty(cb.CallbackId))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _botClient.AnswerCallbackAsync(new AnswerCallbackRequest
                    {
                        CallbackId = cb.CallbackId,
                        Notification = "👌"
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to answer callback query {CallbackId}", cb.CallbackId);
                }
            });
        }

        if (!data.StartsWith("cmd_admin_"))
        {
            _stateService.ClearState(chatId);
        }

        switch (data)
        {
            case "cmd_back_main":
                await ShowMainMenuAsync(chatId, maxUserId, messageId);
                break;

            case "cmd_accept_privacy":
                var privUser = await EnsureUserExistsAsync(maxUserId);
                privUser.HasAcceptedPrivacyPolicy = true;
                privUser.PrivacyPolicyAcceptedAtUtc = DateTime.UtcNow;
                privUser.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                await _userSyncService.SyncUserAcrossPlatformsAsync(privUser);
                await ShowMainMenuAsync(chatId, maxUserId, messageId);
                break;

            // ─── Homework ───
            case { } s when s.StartsWith("cmd_homework_refresh:"):
                var hwRefDate = s["cmd_homework_refresh:".Length..];
                await ExecuteHomeworkAsync(chatId, maxUserId, hwRefDate, messageId, forceRefresh: true);
                break;

            case { } s when s.StartsWith("cmd_homework:"):
                var dateParam = s["cmd_homework:".Length..];
                await ExecuteHomeworkAsync(chatId, maxUserId, dateParam, messageId);
                break;

            case "cmd_homework":
                await ExecuteHomeworkAsync(chatId, maxUserId, null, messageId);
                break;

            case "cmd_homework_refresh":
                await ExecuteHomeworkAsync(chatId, maxUserId, null, messageId, forceRefresh: true);
                break;

            // ─── Schedule ───
            case { } s when s.StartsWith("cmd_schedule_refresh:"):
                var schedRefDate = s["cmd_schedule_refresh:".Length..];
                await ExecuteScheduleAsync(chatId, maxUserId, schedRefDate, messageId, forceRefresh: true);
                break;

            case { } s when s.StartsWith("cmd_schedule:"):
                var schedDate = s["cmd_schedule:".Length..];
                await ExecuteScheduleAsync(chatId, maxUserId, schedDate, messageId);
                break;

            case "cmd_schedule":
                await ExecuteScheduleAsync(chatId, maxUserId, null, messageId);
                break;

            case "cmd_schedule_refresh":
                await ExecuteScheduleAsync(chatId, maxUserId, null, messageId, forceRefresh: true);
                break;

            // ─── Marks ───
            case "cmd_marks":
                await ExecuteMarksAsync(chatId, maxUserId, messageId);
                break;

            case "cmd_marks_refresh":
                await ExecuteMarksAsync(chatId, maxUserId, messageId, forceRefresh: true);
                break;

            // ─── Grade Calculator ───
            case "cmd_calc":
                await ShowGradeCalculatorSubjectsAsync(chatId, maxUserId, messageId);
                break;

            case "cmd_calc_refresh":
                await ShowGradeCalculatorSubjectsAsync(chatId, maxUserId, messageId, forceRefresh: true);
                break;

            case { } s when s.StartsWith("cmd_calc_subj:"):
                if (int.TryParse(s["cmd_calc_subj:".Length..], out int subjIndex))
                    await ShowSubjectTargetOptionsAsync(chatId, maxUserId, messageId, subjIndex);
                break;

            case { } s when s.StartsWith("cmd_calc_target:"):
                var parts = s["cmd_calc_target:".Length..].Split(':');
                if (parts.Length == 2 && int.TryParse(parts[0], out int sIdx) && int.TryParse(parts[1], out int tGrade))
                    await ShowTargetCalculationResultAsync(chatId, maxUserId, messageId, sIdx, tGrade);
                break;

            // ─── Profile ───
            case "cmd_profile":
                await ExecuteProfileAsync(chatId, maxUserId, messageId);
                break;

            case "cmd_profile_refresh":
                await ExecuteProfileAsync(chatId, maxUserId, messageId, forceRefresh: true);
                break;

            case "cmd_children":
                await ShowChildrenMenuAsync(chatId, maxUserId, messageId);
                break;

            case { } s when s.StartsWith("cmd_select_child:"):
                var selectedStudentId = s["cmd_select_child:".Length..];
                await SelectChildAsync(chatId, maxUserId, messageId, selectedStudentId);
                break;

            // ─── Auth / Logout ───
            case "cmd_logout":
                await ShowLogoutConfirmationAsync(chatId, maxUserId, messageId);
                break;

            case "cmd_confirm_logout":
                await PerformLogoutAsync(chatId, maxUserId, messageId);
                break;

            // ─── Bell schedule / Now ───
            case "cmd_now":
                await ExecuteNowAsync(chatId, maxUserId, messageId);
                break;

            case { } s when s.StartsWith("cmd_set_shift:"):
                if (int.TryParse(s["cmd_set_shift:".Length..], out int newShift))
                {
                    var u = await EnsureUserExistsAsync(maxUserId);
                    u.Shift = newShift;
                    await _db.SaveChangesAsync();
                    await ExecuteNowAsync(chatId, maxUserId, messageId);
                }
                break;

            // ─── Settings ───
            case "cmd_help":
                await ShowHelpAsync(chatId, maxUserId, messageId);
                break;

            case "cmd_settings":
                await ShowSettingsMenuAsync(chatId, maxUserId, messageId);
                break;

            case "cmd_alarm":
            case "cmd_alarm_menu":
                await ShowAlarmMenuAsync(chatId, maxUserId, messageId);
                break;

            case "cmd_alarm_check_now":
                await ExecuteAlarmCheckAsync(chatId, maxUserId);
                break;

            case "cmd_toggle_alarm":
                await ToggleAlarmAsync(chatId, maxUserId, messageId);
                break;

            case "cmd_alarm_sched":
                await ShowScheduleReminderMenuAsync(chatId, messageId, maxUserId);
                break;

            case "cmd_alarm_sched_toggle":
                await ToggleScheduleReminderAsync(chatId, messageId, maxUserId);
                break;

            case { } s when s.StartsWith("cmd_alarm_sched_time:"):
                var st = s["cmd_alarm_sched_time:".Length..];
                await SetScheduleReminderTimeAsync(chatId, messageId, maxUserId, st);
                break;

            case "cmd_alarm_sched_custom":
                _stateService.UpdateState(chatId, UserState.WaitingForScheduleReminderTime);
                await EditOrSendMessageAsync(chatId, messageId,
                    "⏰ *Ввод времени для расписания*\n\nВведите время в формате `ЧЧ:ММ` (например, `07:15`):\n_(или напишите «Отмена»)_",
                    CreateSingleButtonKeyboard("❌ Отмена", "cmd_alarm_sched"));
                break;

            case "cmd_alarm_hw":
                await ShowHomeworkReminderMenuAsync(chatId, messageId, maxUserId);
                break;

            case "cmd_alarm_hw_toggle":
                await ToggleHomeworkReminderAsync(chatId, messageId, maxUserId);
                break;

            case { } s when s.StartsWith("cmd_alarm_hw_time:"):
                var ht = s["cmd_alarm_hw_time:".Length..];
                await SetHomeworkReminderTimeAsync(chatId, messageId, maxUserId, ht);
                break;

            case "cmd_alarm_hw_custom":
                _stateService.UpdateState(chatId, UserState.WaitingForHomeworkReminderTime);
                await EditOrSendMessageAsync(chatId, messageId,
                    "⏰ *Ввод времени для домашки*\n\nВведите время в формате `ЧЧ:ММ` (например, `17:30`):\n_(или напишите «Отмена»)_",
                    CreateSingleButtonKeyboard("❌ Отмена", "cmd_alarm_hw"));
                break;

            case "cmd_alarm_equip":
                await ShowEquipmentReminderMenuAsync(chatId, messageId, maxUserId);
                break;

            case "cmd_alarm_equip_toggle":
                await ToggleEquipmentReminderAsync(chatId, messageId, maxUserId);
                break;

            case { } s when s.StartsWith("cmd_alarm_equip_time:"):
                var et = s["cmd_alarm_equip_time:".Length..];
                await SetEquipmentReminderTimeAsync(chatId, messageId, maxUserId, et);
                break;

            case "cmd_alarm_equip_custom":
                _stateService.UpdateState(chatId, UserState.WaitingForEquipmentReminderTime);
                await EditOrSendMessageAsync(chatId, messageId,
                    "⏰ *Ввод времени для сбора рюкзака*\n\nВведите время в формате `ЧЧ:ММ` (например, `20:45`):\n_(или напишите «Отмена»)_",
                    CreateSingleButtonKeyboard("❌ Отмена", "cmd_alarm_equip"));
                break;

            // ─── Account Deletion ───
            case "cmd_delete_account":
                await ShowDeleteAccountConfirmationAsync(chatId, maxUserId, messageId);
                break;

            case "cmd_delete_account_confirm":
                await ExecuteDeleteAccountAsync(chatId, maxUserId, messageId);
                break;

            case "cmd_link_account":
                await ShowLinkAccountAsync(chatId, maxUserId, messageId);
                break;

            // ─── Subscription / Premium ───
            case "cmd_subscription":
                await ShowMainMenuAsync(chatId, maxUserId, messageId);
                break;

            // ─── Admin Panel ───
            case "cmd_admin_panel":
                var roleCheck = await _roleService.GetUserRoleAsync(maxUserId);
                if (roleCheck == "Admin")
                    await ShowAdminPanelAsync(chatId, maxUserId, messageId);
                break;

            case "cmd_admin_headteachers":
                await ShowHeadTeachersMenuAsync(chatId, maxUserId, messageId);
                break;

            case "cmd_admin_add_ht":
                _stateService.UpdateState(chatId, UserState.WaitingForHeadTeacherId);
                await EditOrSendMessageAsync(chatId, messageId,
                    "👤 *Добавление завуча*\n\nВведите MAX/Telegram ID, @username или имя пользователя:\n_(или напишите «Отмена»)_",
                    CreateSingleButtonKeyboard("❌ Отмена", "cmd_admin_headteachers"));
                break;

            case { } s when s.StartsWith("cmd_admin_remove_ht:"):
                var htId = s["cmd_admin_remove_ht:".Length..];
                await RemoveHeadTeacherAsync(chatId, maxUserId, messageId, htId);
                break;

            case "cmd_admin_maintenance":
                await ShowMaintenanceMenuAsync(chatId, maxUserId, messageId);
                break;

            case "cmd_admin_maintenance_on":
                await ToggleMaintenanceAsync(chatId, maxUserId, messageId, true);
                break;

            case "cmd_admin_maintenance_off":
                await ToggleMaintenanceAsync(chatId, maxUserId, messageId, false);
                break;

            case "cmd_admin_broadcast":
                var roleBc = await _roleService.GetUserRoleAsync(maxUserId);
                if (roleBc == "Admin")
                {
                    _stateService.UpdateState(chatId, UserState.WaitingForBroadcastText);
                    await EditOrSendMessageAsync(chatId, messageId,
                        "📢 *Рассылка*\n\nВведите текст сообщения для всех пользователей MAX бота:\n_(или напишите «Отмена»)_",
                        CreateSingleButtonKeyboard("❌ Отмена", "cmd_admin_panel"));
                }
                break;

            case "cmd_admin_stats":
                await ShowStatsAsync(chatId, maxUserId, messageId);
                break;

            case "cmd_back_admin":
                await ShowAdminPanelAsync(chatId, maxUserId, messageId);
                break;
        }
    }

    // =========================================================================
    // SCREENS — MAIN MENU, PRIVACY, HELP
    // =========================================================================

    private async Task ShowMainMenuAsync(long chatId, string maxUserId, string? messageId = null)
    {
        var screen = await _engine.BuildMainMenuScreenAsync(maxUserId, "max");
        await RenderScreenAsync(chatId, messageId, screen);
    }

    private async Task ShowPrivacyPolicyOnboardingAsync(long chatId, string? messageId = null)
    {
        var screen = _engine.BuildPrivacyPolicyOnboardingScreen();
        await RenderScreenAsync(chatId, messageId, screen);
    }

    private async Task ShowHelpAsync(long chatId, string maxUserId, string? messageId = null)
    {
        var screen = _engine.BuildHelpScreen();
        await RenderScreenAsync(chatId, messageId, screen);
    }

    private async Task ShowLoginPromptAsync(long chatId, string maxUserId, string? messageId = null)
    {
        var maxBotApp = _config["MaxBot:Username"] ?? _config["MaxBotUsername"] ?? "se13960436_bot";

        var text = "🔐 *Авторизация в Mirage Diary*\n\n" +
                   "Для доступа к расписанию и оценкам необходимо связать аккаунт Госуслуг (Моя Школа / Дневник.ру).\n\n" +
                   "Нажмите кнопку ниже, чтобы открыть безопасный портал авторизации:";

        var keyboard = new List<List<Button>>
        {
            new List<Button>
            {
                new OpenAppButton
                {
                    Text = "🚀 Войти через Госуслуги",
                    WebApp = maxBotApp,
                    Payload = maxUserId
                }
            },
            new List<Button>
            {
                new CallbackButton { Text = "🏠 Главное меню", Payload = "cmd_back_main" }
            }
        };

        await EditOrSendMessageAsync(chatId, messageId, text, keyboard);
    }

    private async Task ShowLogoutConfirmationAsync(long chatId, string maxUserId, string? messageId = null)
    {
        var screen = _engine.BuildLogoutConfirmationScreen();
        await RenderScreenAsync(chatId, messageId, screen);
    }

    private async Task PerformLogoutAsync(long chatId, string maxUserId, string? messageId = null)
    {
        var screen = await _engine.PerformLogoutAsync(maxUserId);
        await RenderScreenAsync(chatId, messageId, screen);
    }

    // =========================================================================
    // HOMEWORK, SCHEDULE, MARKS
    // =========================================================================

    private async Task ExecuteHomeworkAsync(long chatId, string maxUserId, string? dateArg = null, string? messageId = null, bool forceRefresh = false)
    {
        var screen = await _engine.BuildHomeworkScreenAsync(maxUserId, dateArg, forceRefresh);
        await RenderScreenAsync(chatId, messageId, screen);
    }

    private async Task ExecuteScheduleAsync(long chatId, string maxUserId, string? dateArg = null, string? messageId = null, bool forceRefresh = false)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == maxUserId);
        if ((user == null || string.IsNullOrEmpty(user.AccessToken)) && !string.IsNullOrEmpty(user?.OrgName))
        {
            var schedule = await _db.Schedules.FirstOrDefaultAsync(s => s.OrgName == user.OrgName);
            if (schedule != null && !string.IsNullOrEmpty(schedule.FileId))
            {
                var uploadedKeyboard = new List<List<Button>>
                {
                    new List<Button>
                    {
                        new CallbackButton { Text = "🔄 Обновить", Payload = "cmd_schedule_refresh" },
                        new CallbackButton { Text = "🏠 Главное меню", Payload = "cmd_back_main" }
                    }
                };
                var uploadedText = $"📅 *Расписание — {user.OrgName}*\n\nЗагружено администрацией школы (ID: `{schedule.FileId}`)";
                await SendOrEditBannerScreenAsync(chatId, messageId, "banner_schedule.png", uploadedText, uploadedKeyboard);
                return;
            }
        }

        var screen = await _engine.BuildScheduleScreenAsync(maxUserId, dateArg, forceRefresh);
        await RenderScreenAsync(chatId, messageId, screen);
    }

    private async Task ExecuteMarksAsync(long chatId, string maxUserId, string? messageId = null, bool forceRefresh = false)
    {
        var screen = await _engine.BuildMarksScreenAsync(maxUserId, forceRefresh);
        await RenderScreenAsync(chatId, messageId, screen);
    }

    // =========================================================================
    // GRADE GOAL CALCULATOR
    // =========================================================================

    private async Task ShowGradeCalculatorSubjectsAsync(long chatId, string maxUserId, string? messageId = null, bool forceRefresh = false)
    {
        var screen = await _engine.BuildGradeCalculatorSubjectsScreenAsync(maxUserId, forceRefresh);
        await RenderScreenAsync(chatId, messageId, screen);
    }

    private async Task ShowSubjectTargetOptionsAsync(long chatId, string maxUserId, string? messageId, int subjectIndex)
    {
        var screen = await _engine.BuildSubjectTargetOptionsScreenAsync(maxUserId, subjectIndex);
        await RenderScreenAsync(chatId, messageId, screen);
    }

    private async Task ShowTargetCalculationResultAsync(long chatId, string maxUserId, string? messageId, int subjectIndex, int targetGrade)
    {
        var screen = await _engine.BuildTargetCalculationResultScreenAsync(maxUserId, subjectIndex, targetGrade);
        await RenderScreenAsync(chatId, messageId, screen);
    }

    // =========================================================================
    // PROFILE & CHILDREN SWITCHER
    // =========================================================================

    private async Task ExecuteProfileAsync(long chatId, string maxUserId, string? messageId = null, bool forceRefresh = false)
    {
        var screen = await _engine.BuildProfileScreenAsync(maxUserId, forceRefresh);
        await RenderScreenAsync(chatId, messageId, screen);
    }

    private async Task ShowChildrenMenuAsync(long chatId, string maxUserId, string? messageId = null)
    {
        var screen = await _engine.BuildChildrenMenuScreenAsync(maxUserId);
        await RenderScreenAsync(chatId, messageId, screen);
    }

    private async Task SelectChildAsync(long chatId, string maxUserId, string? messageId, string studentId)
    {
        await _engine.SelectChildAsync(maxUserId, studentId);
        await ExecuteProfileAsync(chatId, maxUserId, messageId, forceRefresh: true);
    }

    // =========================================================================
    // BELL SCHEDULE / NOW SCREEN
    // =========================================================================

    private async Task ExecuteNowAsync(long chatId, string maxUserId, string? messageId = null)
    {
        var screen = await _engine.BuildNowScreenAsync(maxUserId);
        await RenderScreenAsync(chatId, messageId, screen);
    }

    // =========================================================================
    // SETTINGS & SMART REMINDERS
    // =========================================================================

    private async Task ShowSettingsMenuAsync(long chatId, string maxUserId, string? messageId = null)
    {
        var screen = await _engine.BuildSettingsMenuScreenAsync(maxUserId);
        await RenderScreenAsync(chatId, messageId, screen);
    }

    private async Task ShowAlarmMenuAsync(long chatId, string maxUserId, string? messageId = null)
    {
        var screen = await _engine.BuildAlarmMenuScreenAsync(maxUserId);
        await RenderScreenAsync(chatId, messageId, screen);
    }

    private async Task ShowDeleteAccountConfirmationAsync(long chatId, string maxUserId, string? messageId)
    {
        var screen = _engine.BuildDeleteAccountConfirmationScreen();
        await RenderScreenAsync(chatId, messageId, screen);
    }

    private async Task ExecuteDeleteAccountAsync(long chatId, string maxUserId, string? messageId)
    {
        var screen = await _engine.ExecuteDeleteAccountAsync(maxUserId);
        _stateService.ClearState(chatId);
        await EditOrSendMessageAsync(chatId, messageId, screen.Text);
    }

    private async Task ShowLinkAccountAsync(long chatId, string maxUserId, string? messageId = null)
    {
        var screen = await _engine.BuildLinkAccountScreenAsync(maxUserId);
        await RenderScreenAsync(chatId, messageId, screen);
    }

    private async Task HandleLinkCommandAsync(long chatId, string maxUserId, string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            await ShowLinkAccountAsync(chatId, maxUserId, null);
            return;
        }

        string code = parts[1].Trim();
        var (success, message, peer) = await _userSyncService.TryLinkAccountsByCodeAsync(maxUserId, code);
        if (success)
        {
            string peerInfo = peer != null && !string.IsNullOrEmpty(peer.FirstName) ? $" ({peer.FirstName})" : "";
            await SendUserMessageAsync(chatId, new SendMessageRequest
            {
                Text = $"✅ *Аккаунты успешно связаны!*{peerInfo}\n\n" +
                       $"Все токены, класс, расписание и настройки синхронизированы с вашим аккаунтом на другой платформе.",
                Format = MessageFormat.Markdown
            });
        }
        else
        {
            await SendUserMessageAsync(chatId, new SendMessageRequest
            {
                Text = $"❌ *Ошибка связки:* {message}",
                Format = MessageFormat.Markdown
            });
        }
    }

    private async Task ToggleAlarmAsync(long chatId, string maxUserId, string? messageId = null)
    {
        var res = await _engine.ToggleMasterAlarmAsync(maxUserId);
        if (res.NextScreen != null)
            await RenderScreenAsync(chatId, messageId, res.NextScreen);
        else
            await ShowAlarmMenuAsync(chatId, maxUserId, messageId);
    }

    // ─── Schedule Reminder Menu ───
    private async Task ShowScheduleReminderMenuAsync(long chatId, string? messageId, string maxUserId)
    {
        var screen = await _engine.BuildScheduleReminderMenuScreenAsync(maxUserId);
        await RenderScreenAsync(chatId, messageId, screen);
    }

    private async Task ToggleScheduleReminderAsync(long chatId, string? messageId, string maxUserId)
    {
        var res = await _engine.ToggleScheduleReminderAsync(maxUserId);
        if (res.NextScreen != null)
            await RenderScreenAsync(chatId, messageId, res.NextScreen);
        else
            await ShowScheduleReminderMenuAsync(chatId, messageId, maxUserId);
    }

    private async Task SetScheduleReminderTimeAsync(long chatId, string? messageId, string maxUserId, string time)
    {
        var res = await _engine.SetScheduleReminderTimeAsync(maxUserId, time);
        if (res.NextScreen != null)
            await RenderScreenAsync(chatId, messageId, res.NextScreen);
        else
            await ShowScheduleReminderMenuAsync(chatId, messageId, maxUserId);
    }

    // ─── Homework Reminder Menu ───
    private async Task ShowHomeworkReminderMenuAsync(long chatId, string? messageId, string maxUserId)
    {
        var screen = await _engine.BuildHomeworkReminderMenuScreenAsync(maxUserId);
        await RenderScreenAsync(chatId, messageId, screen);
    }

    private async Task ToggleHomeworkReminderAsync(long chatId, string? messageId, string maxUserId)
    {
        var res = await _engine.ToggleHomeworkReminderAsync(maxUserId);
        if (res.NextScreen != null)
            await RenderScreenAsync(chatId, messageId, res.NextScreen);
        else
            await ShowHomeworkReminderMenuAsync(chatId, messageId, maxUserId);
    }

    private async Task SetHomeworkReminderTimeAsync(long chatId, string? messageId, string maxUserId, string time)
    {
        var res = await _engine.SetHomeworkReminderTimeAsync(maxUserId, time);
        if (res.NextScreen != null)
            await RenderScreenAsync(chatId, messageId, res.NextScreen);
        else
            await ShowHomeworkReminderMenuAsync(chatId, messageId, maxUserId);
    }

    // ─── Equipment Reminder Menu ───
    private async Task ShowEquipmentReminderMenuAsync(long chatId, string? messageId, string maxUserId)
    {
        var screen = await _engine.BuildEquipmentReminderMenuScreenAsync(maxUserId);
        await RenderScreenAsync(chatId, messageId, screen);
    }

    private async Task ToggleEquipmentReminderAsync(long chatId, string? messageId, string maxUserId)
    {
        var res = await _engine.ToggleEquipmentReminderAsync(maxUserId);
        if (res.NextScreen != null)
            await RenderScreenAsync(chatId, messageId, res.NextScreen);
        else
            await ShowEquipmentReminderMenuAsync(chatId, messageId, maxUserId);
    }

    private async Task SetEquipmentReminderTimeAsync(long chatId, string? messageId, string maxUserId, string time)
    {
        var res = await _engine.SetEquipmentReminderTimeAsync(maxUserId, time);
        if (res.NextScreen != null)
            await RenderScreenAsync(chatId, messageId, res.NextScreen);
        else
            await ShowEquipmentReminderMenuAsync(chatId, messageId, maxUserId);
    }

    // =========================================================================
    // SMART ALARM (EQUIPMENT CHECK)
    // =========================================================================

    private async Task ExecuteAlarmCheckAsync(long chatId, string maxUserId)
    {
        var screen = await _engine.BuildAlarmCheckScreenAsync(maxUserId);
        await RenderScreenAsync(chatId, null, screen);
    }

    // =========================================================================
    // SUBSCRIPTION & PREMIUM
    // =========================================================================

    private async Task ShowSubscriptionAsync(long chatId, string maxUserId, string? messageId = null)
    {
        var screen = await _engine.BuildSubscriptionScreenAsync(maxUserId);
        await RenderScreenAsync(chatId, messageId, screen);
    }

    private async Task ActivateBetaSubscriptionAsync(long chatId, string maxUserId, string? messageId = null)
    {
        var res = await _engine.ActivateBetaSubscriptionAsync(maxUserId);
        if (res.NextScreen != null)
            await RenderScreenAsync(chatId, messageId, res.NextScreen);
        else
            await ShowSubscriptionAsync(chatId, maxUserId, messageId);
    }

    // =========================================================================
    // ADMIN PANEL
    // =========================================================================

    private async Task ShowAdminPanelAsync(long chatId, string maxUserId, string? messageId = null)
    {
        var role = await _roleService.GetUserRoleAsync(maxUserId);
        if (role != "Admin") return;

        var isMaintenance = _maintenanceService.IsMaintenanceModeActive();
        var userCount = await _db.Users.CountAsync();
        var htCount = await _db.Users.CountAsync(u => u.Role == "HeadTeacher");

        var adminKey = Environment.GetEnvironmentVariable("ADMIN_KEY") ?? _config["Security:AdminKey"] ?? "";
        var baseUrl = (_config["MiniAppUrl"] ?? "https://miragediary.space").TrimEnd('/');
        var adminWebUrl = $"{baseUrl}/admin?key={Uri.EscapeDataString(adminKey)}";

        var text = $"👑 *Админ-панель (MAX)*\n\n" +
                   $"👥 Пользователей в базе: *{userCount}*\n" +
                   $"🏫 Завучей: *{htCount}*\n" +
                   $"🔧 Тех. работы: *{(isMaintenance ? "Включены" : "Выключены")}*\n\n" +
                   $"🔑 *Ключ администратора:*\n`{adminKey}`\n\n" +
                   $"_Нажмите кнопку ниже для мгновенного входа в веб-консоль:_";

        var keyboard = new List<List<Button>>
        {
            new List<Button> { new LinkButton { Text = "🌐 Войти в админку на сайте", Url = adminWebUrl } },
            new List<Button> { new CallbackButton { Text = "🏫 Управление завучами", Payload = "cmd_admin_headteachers" } },
            new List<Button> { new CallbackButton { Text = isMaintenance ? "🟢 Выключить тех. работы" : "🔧 Включить тех. работы", Payload = "cmd_admin_maintenance" } },
            new List<Button>
            {
                new CallbackButton { Text = "📢 Рассылка", Payload = "cmd_admin_broadcast" },
                new CallbackButton { Text = "📊 Статистика", Payload = "cmd_admin_stats" }
            },
            new List<Button> { new CallbackButton { Text = "🏠 Главное меню", Payload = "cmd_back_main" } }
        };

        await SendOrEditBannerScreenAsync(chatId, messageId, "banner_admin.png", text, keyboard);
    }

    private async Task ShowHeadTeachersMenuAsync(long chatId, string maxUserId, string? messageId = null)
    {
        var role = await _roleService.GetUserRoleAsync(maxUserId);
        if (role != "Admin") return;

        var headTeachers = await _db.Users.Where(u => u.Role == "HeadTeacher").ToListAsync();

        var text = "🏫 *Управление завучами (MAX)*\n\n";
        if (headTeachers.Count == 0)
            text += "Завучей пока нет.";
        else
            text += string.Join("\n", headTeachers.Select(ht =>
                $"• `{ht.Id}` — {(string.IsNullOrEmpty(ht.OrgName) ? "_без школы_" : ht.OrgName)}"));

        var keyboard = new List<List<Button>>
        {
            new List<Button> { new CallbackButton { Text = "➕ Добавить завуча", Payload = "cmd_admin_add_ht" } }
        };

        foreach (var ht in headTeachers)
        {
            var shortId = ht.Id.Length > 8 ? ht.Id[..8] + "…" : ht.Id;
            keyboard.Add(new List<Button>
            {
                new CallbackButton
                {
                    Text = $"🗑 Удалить {shortId} ({ht.OrgName})",
                    Payload = $"cmd_admin_remove_ht:{ht.Id}"
                }
            });
        }

        keyboard.Add(new List<Button> { new CallbackButton { Text = "⬅️ Назад", Payload = "cmd_back_admin" } });

        await EditOrSendMessageAsync(chatId, messageId, text, keyboard);
    }

    private async Task SaveHeadTeacherAsync(long adminChatId, string targetInput, string orgName)
    {
        var user = await _userSyncService.FindUserByIdOrUsernameAsync(targetInput);
        string effectiveId = user?.Id ?? targetInput.Trim();

        if (user == null)
        {
            user = new UserEntity
            {
                Id = effectiveId,
                Role = "HeadTeacher",
                OrgName = orgName,
                AccessToken = "",
                RefreshToken = "",
                StudentId = "",
                ClassName = ""
            };
            if (targetInput.StartsWith("@"))
                user.Username = targetInput.TrimStart('@');

            _db.Users.Add(user);
        }
        else
        {
            user.Role = "HeadTeacher";
            user.OrgName = orgName;
        }

        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await _userSyncService.SyncUserAcrossPlatformsAsync(user);

        string displayTarget = !string.IsNullOrEmpty(user.Username) ? $"@{user.Username} (`{user.Id}`)" : $"`{user.Id}`";
        await SendUserMessageAsync(adminChatId, new SendMessageRequest
        {
            Text = $"✅ Пользователь {displayTarget} назначен завучем школы *{orgName}*.\n\nВозвращайтесь в меню: /menu",
            Format = MessageFormat.Markdown
        });
    }

    private async Task RemoveHeadTeacherAsync(long chatId, string maxUserId, string? messageId, string htId)
    {
        var role = await _roleService.GetUserRoleAsync(maxUserId);
        if (role != "Admin") return;

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == htId);
        if (user != null)
        {
            user.Role = "Student";
            user.OrgName = "";
            await _db.SaveChangesAsync();
        }

        await ShowHeadTeachersMenuAsync(chatId, maxUserId, messageId);
    }

    private async Task ShowMaintenanceMenuAsync(long chatId, string maxUserId, string? messageId = null)
    {
        var role = await _roleService.GetUserRoleAsync(maxUserId);
        if (role != "Admin") return;

        var isMaintenance = _maintenanceService.IsMaintenanceModeActive();
        var text = $"🔧 *Технические работы (MAX)*\n\n" +
                   $"Текущий статус: *{(isMaintenance ? "Включены 🔴" : "Выключены 🟢")}*\n\n" +
                   "При включении пользователи будут получать сообщение о тех. работах вместо ответа бота.";

        var keyboard = new List<List<Button>>
        {
            new List<Button>
            {
                new CallbackButton { Text = "🟢 Выключить", Payload = "cmd_admin_maintenance_off" },
                new CallbackButton { Text = "🔴 Включить (30 мин)", Payload = "cmd_admin_maintenance_on" }
            },
            new List<Button> { new CallbackButton { Text = "⬅️ Назад", Payload = "cmd_back_admin" } }
        };

        await EditOrSendMessageAsync(chatId, messageId, text, keyboard);
    }

    private async Task ToggleMaintenanceAsync(long chatId, string maxUserId, string? messageId, bool enable)
    {
        var role = await _roleService.GetUserRoleAsync(maxUserId);
        if (role != "Admin") return;

        if (enable) _maintenanceService.ActivateMaintenanceMode(30);
        else _maintenanceService.DeactivateMaintenanceMode();

        await ShowAdminPanelAsync(chatId, maxUserId, messageId);
    }

    private async Task ShowStatsAsync(long chatId, string maxUserId, string? messageId = null)
    {
        var role = await _roleService.GetUserRoleAsync(maxUserId);
        if (role != "Admin") return;

        var total = await _db.Users.CountAsync();
        var auth = await _db.Users.CountAsync(u => u.AccessToken != "");
        var hts = await _db.Users.CountAsync(u => u.Role == "HeadTeacher");
        var alarmOn = await _db.Users.CountAsync(u => u.IsAlarmEnabled);
        var scheds = await _db.Schedules.CountAsync();

        var text = $"📊 *Статистика*\n\n" +
                   $"👥 Всего пользователей: *{total}*\n" +
                   $"✅ Авторизованных: *{auth}*\n" +
                   $"🏫 Завучей: *{hts}*\n" +
                   $"🔔 С будильником: *{alarmOn}*\n" +
                   $"📅 Расписаний загружено: *{scheds}*";

        var keyboard = CreateSingleButtonKeyboard("⬅️ Назад", "cmd_back_admin");
        await EditOrSendMessageAsync(chatId, messageId, text, keyboard);
    }

    private async Task DoBroadcastAsync(long adminChatId, string broadcastText)
    {
        var maxUsers = await _db.Users.Where(u => u.Id.StartsWith("max_") && u.AccessToken != "").ToListAsync();
        int sent = 0, failed = 0;

        await SendUserMessageAsync(adminChatId, new SendMessageRequest
        {
            Text = $"📢 Начинаю рассылку для *{maxUsers.Count}* пользователей MAX...",
            Format = MessageFormat.Markdown
        });

        foreach (var user in maxUsers)
        {
            if (!long.TryParse(user.Id.Replace("max_", ""), out long targetChatId)) continue;
            try
            {
                await SendUserMessageAsync(targetChatId, new SendMessageRequest
                {
                    Text = $"📢 *Сообщение от администратора:*\n\n{broadcastText}",
                    Format = MessageFormat.Markdown
                });
                sent++;
                await Task.Delay(50);
            }
            catch { failed++; }
        }

        await SendUserMessageAsync(adminChatId, new SendMessageRequest
        {
            Text = $"✅ Рассылка завершена.\n✔️ Доставлено: *{sent}*\n❌ Ошибок: *{failed}*\n\nВозвращайтесь: /menu",
            Format = MessageFormat.Markdown
        });
    }

    // =========================================================================
    // HELPERS FOR MESSAGES, ATTACHMENTS & BANNERS
    // =========================================================================

    private static List<List<Button>> ConvertToMaxKeyboard(List<List<BotButton>>? buttons)
    {
        var keyboard = new List<List<Button>>();
        if (buttons == null) return keyboard;

        foreach (var row in buttons)
        {
            var rowButtons = new List<Button>();
            foreach (var btn in row)
            {
                if (!string.IsNullOrEmpty(btn.MaxWebApp))
                {
                    rowButtons.Add(new OpenAppButton
                    {
                        Text = btn.Text,
                        WebApp = btn.MaxWebApp,
                        Payload = btn.MaxPayload ?? ""
                    });
                }
                else if (!string.IsNullOrEmpty(btn.Url))
                {
                    rowButtons.Add(new LinkButton { Text = btn.Text, Url = btn.Url });
                }
                else if (!string.IsNullOrEmpty(btn.WebAppUrl))
                {
                    rowButtons.Add(new LinkButton { Text = btn.Text, Url = btn.WebAppUrl });
                }
                else
                {
                    rowButtons.Add(new CallbackButton { Text = btn.Text, Payload = btn.CallbackData ?? "noop" });
                }
            }
            keyboard.Add(rowButtons);
        }
        return keyboard;
    }

    private async Task RenderScreenAsync(long chatId, string? messageId, BotScreen screen)
    {
        var keyboard = ConvertToMaxKeyboard(screen.Buttons);
        if (!string.IsNullOrEmpty(screen.BannerFileName))
        {
            await SendOrEditBannerScreenAsync(chatId, messageId, screen.BannerFileName, screen.Text, keyboard);
        }
        else
        {
            await EditOrSendMessageAsync(chatId, messageId, screen.Text, keyboard);
        }
    }

    private async Task SendOrEditBannerScreenAsync(
        long chatId,
        string? messageId,
        string bannerFileName,
        string text,
        List<List<Button>> keyboard)
    {
        var baseUrl = _config["MiniAppUrl"]?.TrimEnd('/') ?? "https://miragediary.space";
        var attachments = new List<Attachment>();

        if (!string.IsNullOrEmpty(bannerFileName))
        {
            attachments.Add(new ImageAttachment
            {
                Payload = new ImagePayload
                {
                    Url = $"{baseUrl}/images/banners/{bannerFileName}"
                }
            });
        }

        if (keyboard != null && keyboard.Count > 0)
        {
            attachments.Add(new InlineKeyboardAttachment
            {
                Payload = new InlineKeyboardPayload
                {
                    Buttons = keyboard
                }
            });
        }

        if (!string.IsNullOrEmpty(messageId))
        {
            try
            {
                await _botClient.EditMessageByIdAsync(messageId, new SendMessageRequest
                {
                    Text = text,
                    Format = MessageFormat.Markdown,
                    Attachments = attachments
                });
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to edit MAX message {MessageId}, sending fresh.", messageId);
            }
        }

        await SendUserMessageAsync(chatId, new SendMessageRequest
        {
            Text = text,
            Format = MessageFormat.Markdown,
            Attachments = attachments
        });
    }

    private async Task EditOrSendMessageAsync(long chatId, string? messageId, string text, List<List<Button>>? keyboard = null)
    {
        var attachments = keyboard != null ? CreateKeyboardAttachment(keyboard) : null;

        if (!string.IsNullOrEmpty(messageId))
        {
            try
            {
                await _botClient.EditMessageByIdAsync(messageId, new SendMessageRequest
                {
                    Text = text,
                    Format = MessageFormat.Markdown,
                    Attachments = attachments
                });
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to edit MAX message {MessageId} with Markdown, attempting PlainText fallback.", messageId);
                try
                {
                    await _botClient.EditMessageByIdAsync(messageId, new SendMessageRequest
                    {
                        Text = text,
                        Format = null,
                        Attachments = attachments
                    });
                    return;
                }
                catch { }
            }
        }

        await SendUserMessageAsync(chatId, new SendMessageRequest
        {
            Text = text,
            Format = MessageFormat.Markdown,
            Attachments = attachments
        });
    }

    private async Task SendUserMessageAsync(long userId, SendMessageRequest request)
    {
        try
        {
            request.UserId = userId;
            request.ChatId = null;
            await _botClient.SendMessageAsync(request);
        }
        catch (Exception ex) when (request.Format == MessageFormat.Markdown)
        {
            _logger.LogWarning(ex, "Failed to send MAX message with Markdown to user {UserId}. Retrying with PlainText fallback.", userId);
            try
            {
                request.Format = null;
                await _botClient.SendMessageAsync(request);
            }
            catch (Exception retryEx)
            {
                _logger.LogError(retryEx, "Failed to send MAX message in PlainText fallback to user {UserId}", userId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send MAX message to user {UserId}", userId);
        }
    }

    private List<List<Button>> CreateSingleButtonKeyboard(string text, string payload)
    {
        return new List<List<Button>>
        {
            new List<Button>
            {
                new CallbackButton { Text = text, Payload = payload }
            }
        };
    }

    private List<Attachment> CreateKeyboardAttachment(List<List<Button>> keyboard)
    {
        return new List<Attachment>
        {
            new InlineKeyboardAttachment
            {
                Payload = new InlineKeyboardPayload
                {
                    Buttons = keyboard
                }
            }
        };
    }

    private async Task<UserEntity> EnsureUserExistsAsync(string maxUserId, string? username = null, string? firstName = null, string? lastName = null)
    {
        return await _engine.EnsureUserExistsAsync(maxUserId, username, firstName, lastName, "max");
    }

    private static bool TryParseTime(string input, out string formattedTime)
    {
        formattedTime = "";
        if (string.IsNullOrWhiteSpace(input)) return false;
        var clean = input.Trim();
        var match = Regex.Match(clean, @"^([0-1]?[0-9]|2[0-3])[:\s\.-]([0-5][0-9])$");
        if (match.Success)
        {
            int h = int.Parse(match.Groups[1].Value);
            int m = int.Parse(match.Groups[2].Value);
            formattedTime = $"{h:D2}:{m:D2}";
            return true;
        }
        return false;
    }

    private static bool IsCancelCommand(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();
        return trimmed.Equals("отмена", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("cancel", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("/cancel", StringComparison.OrdinalIgnoreCase);
    }
}
