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
        ITokenStore tokenStore)
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
        if (text.StartsWith("/start") || text.StartsWith("/menu") || text.Equals("Отмена", StringComparison.OrdinalIgnoreCase))
        {
            _stateService.ClearState(chatId);
            await ShowMainMenuAsync(chatId, maxUserId);
            return;
        }

        // Direct /help command
        if (text.StartsWith("/help"))
        {
            _stateService.ClearState(chatId);
            await ShowHelpAsync(chatId, maxUserId);
            return;
        }

        // Direct /login or /auth command
        if (text.StartsWith("/login") || text.StartsWith("/auth"))
        {
            _stateService.ClearState(chatId);
            await ShowLoginPromptAsync(chatId, maxUserId);
            return;
        }

        // Direct /logout command
        if (text.StartsWith("/logout"))
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
        if (text.StartsWith("/homework"))
        {
            string dateArg = text.Length > 9 ? text[9..].Trim() : "";
            await ExecuteHomeworkAsync(chatId, maxUserId, dateArg);
        }
        else if (text.StartsWith("/schedule"))
        {
            string dateArg = text.Length > 9 ? text[9..].Trim() : "";
            await ExecuteScheduleAsync(chatId, maxUserId, dateArg);
        }
        else if (text.StartsWith("/marks") || text.StartsWith("/grades"))
        {
            await ExecuteMarksAsync(chatId, maxUserId);
        }
        else if (text.StartsWith("/calc"))
        {
            await ShowGradeCalculatorSubjectsAsync(chatId, maxUserId);
        }
        else if (text.StartsWith("/profile"))
        {
            await ExecuteProfileAsync(chatId, maxUserId);
        }
        else if (text.StartsWith("/now") || text.StartsWith("/bell"))
        {
            await ExecuteNowAsync(chatId, maxUserId);
        }
        else if (text.StartsWith("/settings"))
        {
            await ShowSettingsMenuAsync(chatId, maxUserId);
        }
        else if (text.StartsWith("/alarm"))
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
                _stateService.UpdateState(chatId, UserState.WaitingForHeadTeacherOrg, text);
                await SendUserMessageAsync(chatId, new SendMessageRequest
                {
                    Text = "🏫 Теперь введите название школы, к которой привязать этого завуча:\n_(или напишите «Отмена»)_",
                    Format = MessageFormat.Markdown
                });
                break;

            case UserState.WaitingForHeadTeacherOrg:
                await SaveHeadTeacherAsync(chatId, session.TempData ?? "", text);
                _stateService.ClearState(chatId);
                break;

            case UserState.WaitingForBroadcastText:
                await DoBroadcastAsync(chatId, text);
                _stateService.ClearState(chatId);
                break;

            case UserState.WaitingForScheduleReminderTime:
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
            case "cmd_settings":
                await ShowSettingsMenuAsync(chatId, maxUserId, messageId);
                break;

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
        var user = await EnsureUserExistsAsync(maxUserId);

        // 1. Check Privacy Policy Onboarding (152-FZ)
        if (!user.HasAcceptedPrivacyPolicy)
        {
            await ShowPrivacyPolicyOnboardingAsync(chatId, messageId);
            return;
        }

        var role = await _roleService.GetUserRoleAsync(maxUserId);
        if (role != user.Role)
        {
            user.Role = role;
            await _db.SaveChangesAsync();
        }
        var isAuthorized = !string.IsNullOrEmpty(user.AccessToken);
        var isMaintenance = _maintenanceService.IsMaintenanceModeActive();
        var maxBotApp = _config["MaxBot:Username"] ?? _config["MaxBotUsername"] ?? "se13960436_bot";

        var keyboard = new List<List<Button>>();

        if (!isAuthorized)
        {
            keyboard.Add(new List<Button>
            {
                new OpenAppButton
                {
                    Text = "🔐 Войти через Госуслуги",
                    WebApp = maxBotApp,
                    Payload = maxUserId
                }
            });
        }
        else
        {
            keyboard.Add(new List<Button>
            {
                new OpenAppButton
                {
                    Text = "📱 Открыть Дневник",
                    WebApp = maxBotApp,
                    Payload = maxUserId
                }
            });
        }

        keyboard.Add(new List<Button>
        {
            new CallbackButton { Text = "📅 Расписание", Payload = "cmd_schedule" },
            new CallbackButton { Text = "📚 Домашка", Payload = "cmd_homework" }
        });
        keyboard.Add(new List<Button>
        {
            new CallbackButton { Text = "📊 Оценки", Payload = "cmd_marks" },
            new CallbackButton { Text = "🎯 Калькулятор", Payload = "cmd_calc" }
        });
        keyboard.Add(new List<Button>
        {
            new CallbackButton { Text = "👤 Профиль", Payload = "cmd_profile" },
            new CallbackButton { Text = "⚙️ Настройки", Payload = "cmd_settings" }
        });

        if (role == "Admin")
        {
            keyboard.Add(new List<Button>
            {
                new CallbackButton { Text = "🔔 До звонка", Payload = "cmd_now" },
                new CallbackButton { Text = "👑 Админ-панель", Payload = "cmd_admin_panel" }
            });
        }
        else
        {
            keyboard.Add(new List<Button>
            {
                new CallbackButton { Text = "🔔 До звонка", Payload = "cmd_now" }
            });
        }

        var maintenanceBanner = isMaintenance ? "\n🔧 _Идут технические работы_" : "";

        string header;
        if (role == "Admin")
        {
            header = "👑 *Администратор* • Mirage Diary (MAX)";
        }
        else if (role == "HeadTeacher")
        {
            header = $"🏫 *Завуч* • {(!string.IsNullOrEmpty(user?.OrgName) ? user.OrgName : "Mirage Diary (MAX)")}";
        }
        else if (isAuthorized)
        {
            if (!string.IsNullOrEmpty(user.ClassName) && !string.IsNullOrEmpty(user.OrgName))
                header = $"🎓 *{user.ClassName}* • {user.OrgName}";
            else if (!string.IsNullOrEmpty(user.ClassName))
                header = $"🎓 *{user.ClassName}* • Mirage Diary";
            else
                header = "🎓 *Ученик* • Mirage Diary";
        }
        else
        {
            header = "👋 *Mirage Diary (MAX)* — школьный дневник";
        }

        var text = header + maintenanceBanner;
        if (role == "HeadTeacher")
        {
            text += "\n\n💡 _Отправьте фото или PDF с расписанием — я сохраню его для учеников вашей школы._";
        }

        await SendOrEditBannerScreenAsync(chatId, messageId, "banner_menu.png", text, keyboard);
    }

    private async Task ShowPrivacyPolicyOnboardingAsync(long chatId, string? messageId = null)
    {
        string text = "🛡 *Политика конфиденциальности и защита данных (152-ФЗ)*\n\n" +
                      "Добро пожаловать в *Mirage Diary*!\n" +
                      "Перед началом использования, пожалуйста, ознакомьтесь с условиями обработки данных:\n\n" +
                      "1. 🔒 *Конвертное шифрование:* Токены сессии защищены Envelope Encryption (AES-256-GCM с уникальным случайным 256-битным ключом на каждую запись).\n" +
                      "2. 🚫 *Без паролей:* Пароли от Госуслуг никогда не передаются и не сохраняются на сервере — вход идет через ЕСИА.\n" +
                      "3. 👨‍👩‍👧 *Для несовершеннолетних:* Сервис используется с согласия родителей (законных представителей) согласно ч. 6 ст. 9 152-ФЗ.\n" +
                      "4. 🇷🇺 *Хранение в РФ:* База данных расположена в Российской Федерации в соответствии с законом.\n" +
                      "5. 🗑 *Удаление данных:* Полный отзыв согласия и стирание профиля доступны в любой момент по команде /delete_account в настройках.\n\n" +
                      "Нажмите *«✅ Согласен с политикой (152-ФЗ)»*, чтобы продолжить.";

        var keyboard = new List<List<Button>>
        {
            new List<Button>
            {
                new LinkButton { Text = "📖 Читать Политику (152-ФЗ)", Url = "https://miragediary.space/privacy" }
            },
            new List<Button>
            {
                new CallbackButton { Text = "✅ Согласен с политикой (152-ФЗ)", Payload = "cmd_accept_privacy" }
            }
        };

        await EditOrSendMessageAsync(chatId, messageId, text, keyboard);
    }

    private async Task ShowHelpAsync(long chatId, string maxUserId)
    {
        var text = "ℹ️ *Справка по командам Mirage Diary (MAX)*\n\n" +
                   "• /menu или /start — Главное интерактивное меню\n" +
                   "• /homework [дата] — Домашнее задание\n" +
                   "• /schedule [дата] — Расписание уроков\n" +
                   "• /marks — Оценки и средний балл за четверть\n" +
                   "• /calc — Калькулятор целей (сколько оценок нужно до 5, 4, 3)\n" +
                   "• /now — Какой урок идёт прямо сейчас и время до звонка\n" +
                   "• /profile — Данные ученика, класс, школа и смена\n" +
                   "• /settings — Настройки будильника и уведомлений\n" +
                   "• /alarm — Проверка вещей в рюкзак на завтра\n" +
                   "• /login — Авторизация через Госуслуги в Mini App\n" +
                   "• /logout — Выход из текущего профиля\n" +
                   "• /subscription — Возможности Mirage Premium";

        var keyboard = new List<List<Button>>
        {
            new List<Button> { new CallbackButton { Text = "🏠 Главное меню", Payload = "cmd_back_main" } }
        };

        await SendOrEditBannerScreenAsync(chatId, null, "banner_menu.png", text, keyboard);
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
        var text = "🚪 *Выход из аккаунта*\n\n" +
                   "Вы действительно хотите выйти из своего аккаунта Госуслуг?\n" +
                   "Все сохранённые токены доступа будут удалены с сервера.";

        var keyboard = new List<List<Button>>
        {
            new List<Button>
            {
                new CallbackButton { Text = "🚪 Да, выйти", Payload = "cmd_confirm_logout" },
                new CallbackButton { Text = "❌ Отмена", Payload = "cmd_profile" }
            }
        };

        await EditOrSendMessageAsync(chatId, messageId, text, keyboard);
    }

    private async Task PerformLogoutAsync(long chatId, string maxUserId, string? messageId = null)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == maxUserId);
        if (user != null)
        {
            user.AccessToken = "";
            user.RefreshToken = "";
            user.StudentId = "";
            user.SelectedStudentId = "";
            await _db.SaveChangesAsync();
        }

        var text = "✅ *Вы успешно вышли из аккаунта.*\n\n" +
                   "Чтобы продолжить пользоваться ботом, авторизуйтесь снова через меню /login.";

        var keyboard = new List<List<Button>>
        {
            new List<Button> { new CallbackButton { Text = "🏠 Главное меню", Payload = "cmd_back_main" } }
        };

        await EditOrSendMessageAsync(chatId, messageId, text, keyboard);
    }

    // =========================================================================
    // HOMEWORK, SCHEDULE, MARKS
    // =========================================================================

    private async Task ExecuteHomeworkAsync(long chatId, string maxUserId, string? dateArg = null, string? messageId = null, bool forceRefresh = false)
    {
        try
        {
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == maxUserId);
            var maxBotApp = _config["MaxBot:Username"] ?? _config["MaxBotUsername"] ?? "se13960436_bot";

            if (user == null || string.IsNullOrEmpty(user.AccessToken))
            {
                await ShowLoginPromptAsync(chatId, maxUserId, messageId);
                return;
            }

            var hw = await _diaryService.GetHomeworkStringAsync(maxUserId, dateArg, forceRefresh);

            if (hw.StartsWith("🔒") || hw.Contains("Сессия истекла") || hw.Contains("недействительна") || hw.Contains("403 Forbidden"))
            {
                var reloginKeyboard = new List<List<Button>>
                {
                    new List<Button> { new OpenAppButton { Text = "🔐 Войти через Госуслуги", WebApp = maxBotApp, Payload = maxUserId } },
                    new List<Button> { new CallbackButton { Text = "🏠 Главное меню", Payload = "cmd_back_main" } }
                };
                await EditOrSendMessageAsync(chatId, messageId, hw, reloginKeyboard);
                return;
            }

            var localNow = DateTime.UtcNow.AddHours(5).Date;
            DateTime currentBaseDate = localNow;
            if (!string.IsNullOrWhiteSpace(dateArg))
            {
                string clean = dateArg.Trim().ToLowerInvariant();
                if (clean == "завтра" || clean == "tomorrow") currentBaseDate = localNow.AddDays(1);
                else if (clean == "вчера" || clean == "yesterday") currentBaseDate = localNow.AddDays(-1);
                else if (DateTime.TryParseExact(clean, new[] { "yyyy-MM-dd", "dd.MM.yyyy", "dd.MM", "d.M.yyyy", "d.M" }, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed))
                {
                    if (clean.Length <= 5) parsed = new DateTime(localNow.Year, parsed.Month, parsed.Day);
                    currentBaseDate = parsed;
                }
            }

            string prevDate = currentBaseDate.AddDays(-1).ToString("yyyy-MM-dd");
            string nextDate = currentBaseDate.AddDays(1).ToString("yyyy-MM-dd");
            string todayStr = localNow.ToString("yyyy-MM-dd");

            var keyboard = new List<List<Button>>
            {
                new List<Button>
                {
                    new CallbackButton { Text = "⬅️ Вчера", Payload = $"cmd_homework:{prevDate}" },
                    new CallbackButton { Text = "Сегодня", Payload = $"cmd_homework:{todayStr}" },
                    new CallbackButton { Text = "Завтра ➡️", Payload = $"cmd_homework:{nextDate}" }
                },
                new List<Button>
                {
                    new CallbackButton { Text = "🔄 Обновить", Payload = "cmd_homework_refresh" },
                    new CallbackButton { Text = "🏠 Главное меню", Payload = "cmd_back_main" }
                }
            };

            await SendOrEditBannerScreenAsync(chatId, messageId, "banner_homework.png", hw, keyboard);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute homework for MAX user {MaxUserId}", maxUserId);
            await SendUserMessageAsync(chatId, new SendMessageRequest
            {
                Text = $"❌ Произошла ошибка при получении домашнего задания: {ex.Message}"
            });
        }
    }

    private async Task ExecuteScheduleAsync(long chatId, string maxUserId, string? dateArg = null, string? messageId = null, bool forceRefresh = false)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == maxUserId);
        var maxBotApp = _config["MaxBot:Username"] ?? _config["MaxBotUsername"] ?? "se13960436_bot";

        // 1. Prioritize HeadTeacher uploaded schedule file if available for user's school
        if (user != null && !string.IsNullOrEmpty(user.OrgName))
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

        if (user == null || string.IsNullOrEmpty(user.AccessToken))
        {
            await ShowLoginPromptAsync(chatId, maxUserId, messageId);
            return;
        }

        var scheduleText = await _diaryService.GetScheduleStringAsync(maxUserId, dateArg, forceRefresh);

        if (scheduleText.StartsWith("🔒") || scheduleText.Contains("Сессия истекла") || scheduleText.Contains("недействительна") || scheduleText.Contains("403 Forbidden"))
        {
            var reloginKeyboard = new List<List<Button>>
            {
                new List<Button> { new OpenAppButton { Text = "🔐 Войти через Госуслуги", WebApp = maxBotApp, Payload = maxUserId } },
                new List<Button> { new CallbackButton { Text = "🏠 Главное меню", Payload = "cmd_back_main" } }
            };
            await EditOrSendMessageAsync(chatId, messageId, scheduleText, reloginKeyboard);
            return;
        }

        var keyboard = new List<List<Button>>
        {
            new List<Button>
            {
                new CallbackButton { Text = "🔄 Обновить", Payload = "cmd_schedule_refresh" },
                new CallbackButton { Text = "🏠 Главное меню", Payload = "cmd_back_main" }
            }
        };

        if (string.IsNullOrWhiteSpace(scheduleText))
        {
            scheduleText = "📅 *Расписание уроков*\n\nНа выбранную дату расписание отсутствует или уроки не запланированы.";
        }

        await SendOrEditBannerScreenAsync(chatId, messageId, "banner_schedule.png", scheduleText, keyboard);
    }

    private async Task ExecuteMarksAsync(long chatId, string maxUserId, string? messageId = null, bool forceRefresh = false)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == maxUserId);
        var maxBotApp = _config["MaxBot:Username"] ?? _config["MaxBotUsername"] ?? "se13960436_bot";

        if (user == null || string.IsNullOrEmpty(user.AccessToken))
        {
            await ShowLoginPromptAsync(chatId, maxUserId, messageId);
            return;
        }

        var marksText = await _diaryService.GetMarksStringAsync(maxUserId, forceRefresh);

        if (marksText.StartsWith("🔒") || marksText.Contains("Сессия истекла") || marksText.Contains("недействительна") || marksText.Contains("403 Forbidden"))
        {
            var reloginKeyboard = new List<List<Button>>
            {
                new List<Button> { new OpenAppButton { Text = "🔐 Войти через Госуслуги", WebApp = maxBotApp, Payload = maxUserId } },
                new List<Button> { new CallbackButton { Text = "🏠 Главное меню", Payload = "cmd_back_main" } }
            };
            await EditOrSendMessageAsync(chatId, messageId, marksText, reloginKeyboard);
            return;
        }

        var keyboard = new List<List<Button>>
        {
            new List<Button>
            {
                new CallbackButton { Text = "🎯 Калькулятор целей", Payload = "cmd_calc" },
                new CallbackButton { Text = "🔄 Обновить", Payload = "cmd_marks_refresh" }
            },
            new List<Button>
            {
                new CallbackButton { Text = "🏠 Главное меню", Payload = "cmd_back_main" }
            }
        };

        await SendOrEditBannerScreenAsync(chatId, messageId, "banner_marks.png", marksText, keyboard);
    }

    // =========================================================================
    // GRADE GOAL CALCULATOR
    // =========================================================================

    private async Task ShowGradeCalculatorSubjectsAsync(long chatId, string maxUserId, string? messageId = null, bool forceRefresh = false)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == maxUserId);
        if (user == null || string.IsNullOrEmpty(user.AccessToken))
        {
            await ShowLoginPromptAsync(chatId, maxUserId, messageId);
            return;
        }

        var subjects = await _diaryService.GetSubjectGradesSummaryAsync(maxUserId, forceRefresh);
        if (subjects == null || subjects.Count == 0)
        {
            var noMarks = "📊 В дневнике пока нет выставленных оценок для расчета цели.";
            var backKeyboard = CreateSingleButtonKeyboard("🏠 Главное меню", "cmd_back_main");
            await EditOrSendMessageAsync(chatId, messageId, noMarks, backKeyboard);
            return;
        }

        var keyboard = new List<List<Button>>();
        var row = new List<Button>();

        for (int i = 0; i < subjects.Count; i++)
        {
            var s = subjects[i];
            string gpaDisplay = s.TotalWeight > 0 ? $"{s.AverageGpa:F2}" : "—";
            string btnText = $"{s.StatusEmoji} {s.SubjectName} ({gpaDisplay})";
            row.Add(new CallbackButton { Text = btnText, Payload = $"cmd_calc_subj:{i}" });

            if (row.Count == 2)
            {
                keyboard.Add(row);
                row = new List<Button>();
            }
        }
        if (row.Count > 0) keyboard.Add(row);

        keyboard.Add(new List<Button>
        {
            new CallbackButton { Text = "🔄 Обновить", Payload = "cmd_calc_refresh" },
            new CallbackButton { Text = "🏠 Главное меню", Payload = "cmd_back_main" }
        });

        string text = "🎯 *Калькулятор целей и среднего балла*\n\n" +
                      "Выберите предмет, чтобы рассчитать, сколько оценок нужно получить до желаемого балла в четверти:";

        await SendOrEditBannerScreenAsync(chatId, messageId, "banner_marks.png", text, keyboard);
    }

    private async Task ShowSubjectTargetOptionsAsync(long chatId, string maxUserId, string? messageId, int subjectIndex)
    {
        var subjects = await _diaryService.GetSubjectGradesSummaryAsync(maxUserId);
        if (subjects == null || subjectIndex < 0 || subjectIndex >= subjects.Count)
        {
            await ShowGradeCalculatorSubjectsAsync(chatId, maxUserId, messageId);
            return;
        }

        var s = subjects[subjectIndex];
        var markStrings = s.Marks.Select(m =>
        {
            int w = (m.Weight.HasValue && m.Weight.Value > 1) ? m.Weight.Value : 1;
            string wSuffix = w > 1 ? $" (вес ×{w})" : "";
            return $"{m.Value}{wSuffix}";
        });

        string marksDisplay = s.Marks.Count > 0 ? string.Join(", ", markStrings) : "нет оценок";
        string gpaDisplay = s.TotalWeight > 0 ? $"{s.AverageGpa:F2}" : "—";
        string forecastDisplay = s.FinalForecast > 0 ? $"*{s.FinalForecast}* {s.StatusEmoji}" : "не определен";

        string text = $"📚 *Предмет:* {s.SubjectName}\n\n" +
                      $"• *Текущие оценки*: {marksDisplay}\n" +
                      $"• *Текущий средний балл*: `{gpaDisplay}`\n" +
                      $"• *Прогноз в четверти*: {forecastDisplay}\n\n" +
                      $"🎯 *Какую оценку вы хотите получить за четверть?*";

        var keyboard = new List<List<Button>>
        {
            new List<Button>
            {
                new CallbackButton { Text = "⭐ Хочу 5 (≥ 4.60)", Payload = $"cmd_calc_target:{subjectIndex}:5" },
                new CallbackButton { Text = "👍 Хочу 4 (≥ 3.60)", Payload = $"cmd_calc_target:{subjectIndex}:4" }
            },
            new List<Button>
            {
                new CallbackButton { Text = "👌 Хочу 3 (≥ 2.60)", Payload = $"cmd_calc_target:{subjectIndex}:3" },
                new CallbackButton { Text = "⬅️ К предметам", Payload = "cmd_calc" }
            },
            new List<Button>
            {
                new CallbackButton { Text = "🏠 Главное меню", Payload = "cmd_back_main" }
            }
        };

        await EditOrSendMessageAsync(chatId, messageId, text, keyboard);
    }

    private async Task ShowTargetCalculationResultAsync(long chatId, string maxUserId, string? messageId, int subjectIndex, int targetGrade)
    {
        var subjects = await _diaryService.GetSubjectGradesSummaryAsync(maxUserId);
        if (subjects == null || subjectIndex < 0 || subjectIndex >= subjects.Count)
        {
            await ShowGradeCalculatorSubjectsAsync(chatId, maxUserId, messageId);
            return;
        }

        var s = subjects[subjectIndex];
        double targetThreshold = targetGrade switch
        {
            5 => 4.60,
            4 => 3.60,
            3 => 2.60,
            _ => 4.60
        };

        string text;
        int S = s.WeightedSum;
        int W = s.TotalWeight;
        double currentGpa = s.AverageGpa;

        if (W > 0 && currentGpa >= targetThreshold)
        {
            text = $"🎉 *Отличные новости по предмету {s.SubjectName}!* 🚀\n\n" +
                   $"Ваш текущий средний балл: `{currentGpa:F2}`\n" +
                   $"Он уже выше или равен порогу для оценки *{targetGrade}* (`{targetThreshold:F2}`)!\n\n" +
                   $"✨ *Совет:* продолжайте в том же духе и не получайте случайных двоек, чтобы удержать итоговую оценку!";
        }
        else
        {
            double diff = targetThreshold * W - S;
            int neededFivesW1 = (int)Math.Ceiling(diff / (5.0 - targetThreshold));
            if (neededFivesW1 < 1) neededFivesW1 = 1;

            int neededFivesW2 = (int)Math.Ceiling(diff / (10.0 - 2.0 * targetThreshold));
            if (neededFivesW2 < 1) neededFivesW2 = 1;

            string foursOption = "";
            if (targetGrade <= 4 && (4.0 - targetThreshold) > 0.01)
            {
                int neededFoursW1 = (int)Math.Ceiling(diff / (4.0 - targetThreshold));
                if (neededFoursW1 > 0)
                {
                    foursOption = $"\n• 🟡 *{neededFoursW1}* четвёрок подряд (вес 1)";
                }
            }

            text = $"🎯 *План для получения оценки «{targetGrade}»*\n" +
                   $"📚 *Предмет:* {s.SubjectName}\n\n" +
                   $"• *Текущий балл:* `{currentGpa:F2}`\n" +
                   $"• *Требуемый порог:* `≥ {targetThreshold:F2}`\n\n" +
                   $"Чтобы гарантированно получить *{targetGrade}* в четверти, вам нужно заработать:\n" +
                   $"• 🟢 *{neededFivesW1}* пятёрок за ответы на уроках (вес 1)\n" +
                   $"• 🟢 *{neededFivesW2}* пятёрки за контрольные работы (вес 2){foursOption}\n\n" +
                   $"💡 _Каждая хорошая оценка за самостоятельную или контрольную работу быстрее всего поднимет ваш средний балл!_";
        }

        var keyboard = new List<List<Button>>
        {
            new List<Button>
            {
                new CallbackButton { Text = "🎯 Другая цель", Payload = $"cmd_calc_subj:{subjectIndex}" },
                new CallbackButton { Text = "⬅️ К предметам", Payload = "cmd_calc" }
            },
            new List<Button>
            {
                new CallbackButton { Text = "🏠 Главное меню", Payload = "cmd_back_main" }
            }
        };

        await EditOrSendMessageAsync(chatId, messageId, text, keyboard);
    }

    // =========================================================================
    // PROFILE & CHILDREN SWITCHER
    // =========================================================================

    private async Task ExecuteProfileAsync(long chatId, string maxUserId, string? messageId = null, bool forceRefresh = false)
    {
        if (forceRefresh)
        {
            var now = DateTime.UtcNow;
            if (_profileRefreshCooldown.TryGetValue(maxUserId, out var lastTime))
            {
                if ((now - lastTime).TotalSeconds < 15.0)
                {
                    await SendUserMessageAsync(chatId, new SendMessageRequest
                    {
                        Text = "⏳ Пожалуйста, подождите немного перед повторным обновлением профиля."
                    });
                    return;
                }
            }
            _profileRefreshCooldown[maxUserId] = now;
        }

        var text = await _diaryService.GetProfileStringAsync(maxUserId, forceRefresh);
        var children = await _diaryService.GetChildrenListAsync(maxUserId, forceRefresh);

        var keyboard = new List<List<Button>>
        {
            new List<Button>
            {
                new CallbackButton { Text = "🔄 Обновить", Payload = "cmd_profile_refresh" },
                new CallbackButton { Text = "⚙️ Настройки", Payload = "cmd_settings" }
            }
        };

        if (children != null && children.Count > 1)
        {
            keyboard.Add(new List<Button>
            {
                new CallbackButton { Text = "👨‍👩‍👧‍👦 Выбор ребенка", Payload = "cmd_children" }
            });
        }

        keyboard.Add(new List<Button>
        {
            new CallbackButton { Text = "🚪 Выйти", Payload = "cmd_logout" },
            new CallbackButton { Text = "🏠 Главное меню", Payload = "cmd_back_main" }
        });

        await SendOrEditBannerScreenAsync(chatId, messageId, "banner_profile.png", text, keyboard);
    }

    private async Task ShowChildrenMenuAsync(long chatId, string maxUserId, string? messageId = null)
    {
        var children = await _diaryService.GetChildrenListAsync(maxUserId);
        if (children == null || children.Count == 0)
        {
            var msg = "❌ Не удалось получить список детей из аккаунта Госуслуг. Убедитесь, что вы авторизованы.";
            await EditOrSendMessageAsync(chatId, messageId, msg, CreateSingleButtonKeyboard("🏠 Главное меню", "cmd_back_main"));
            return;
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == maxUserId);
        string currentActiveId = user?.SelectedStudentId ?? (children.Count > 0 ? children[0].StudentId : "");

        string text = "👨‍👩‍👧‍👦 *Выбор ребенка (Родительский аккаунт)*\n\nВыберите активного ребенка для просмотра дневника и оценок:\n\n";

        var keyboard = new List<List<Button>>();

        foreach (var child in children)
        {
            string name = $"{child.StudentFirstName}".Trim();
            if (string.IsNullOrEmpty(name)) name = "Ученик";

            bool isActive = (child.StudentId == currentActiveId || child.MdmId == currentActiveId);
            string activeMark = isActive ? " ✅" : "";

            keyboard.Add(new List<Button>
            {
                new CallbackButton { Text = $"👶 {name}{activeMark}", Payload = $"cmd_select_child:{child.StudentId}" }
            });

            text += $"• *{name}*{activeMark}\n";
        }

        keyboard.Add(new List<Button> { new CallbackButton { Text = "🏠 Главное меню", Payload = "cmd_back_main" } });

        await EditOrSendMessageAsync(chatId, messageId, text, keyboard);
    }

    private async Task SelectChildAsync(long chatId, string maxUserId, string? messageId, string studentId)
    {
        var user = await EnsureUserExistsAsync(maxUserId);
        user.SelectedStudentId = studentId;
        user.StudentId = studentId;
        await _db.SaveChangesAsync();

        await ExecuteProfileAsync(chatId, maxUserId, messageId, forceRefresh: true);
    }

    // =========================================================================
    // BELL SCHEDULE / NOW SCREEN
    // =========================================================================

    private async Task ExecuteNowAsync(long chatId, string maxUserId, string? messageId = null)
    {
        var user = await EnsureUserExistsAsync(maxUserId);
        int shift = user.Shift;
        var localTime = DateTime.UtcNow.AddHours(user.UtcOffsetHours);

        var status = _bellSchedule.GetCurrentStatus(localTime, shift);

        string text = $"🔔 *Расписание звонков (МАОУ СОШ № 113)*\n\n" +
                      $"{status.StatusMessage}\n\n" +
                      $"🕐 *Точное время:* `{localTime:HH:mm:ss}` (Екатеринбург)\n" +
                      $"🎓 *Смена:* {shift}-я смена";

        var keyboard = new List<List<Button>>
        {
            new List<Button>
            {
                new CallbackButton { Text = "🔄 Обновить", Payload = "cmd_now" },
                new CallbackButton
                {
                    Text = shift == 1 ? "Сменить на 2 смену" : "Сменить на 1 смену",
                    Payload = shift == 1 ? "cmd_set_shift:2" : "cmd_set_shift:1"
                }
            },
            new List<Button> { new CallbackButton { Text = "🏠 Главное меню", Payload = "cmd_back_main" } }
        };

        await SendOrEditBannerScreenAsync(chatId, messageId, "banner_now.png", text, keyboard);
    }

    // =========================================================================
    // SETTINGS & SMART REMINDERS
    // =========================================================================

    private async Task ShowSettingsMenuAsync(long chatId, string maxUserId, string? messageId = null)
    {
        var user = await EnsureUserExistsAsync(maxUserId);
        var alarmOn = user.IsAlarmEnabled;
        var maxBotApp = _config["MaxBot:Username"] ?? _config["MaxBotUsername"] ?? "se13960436_bot";

        var keyboard = new List<List<Button>>
        {
            new List<Button>
            {
                new CallbackButton
                {
                    Text = $"⏰ Будильник: {(alarmOn ? "🟢 Вкл" : "🔴 Выкл")}",
                    Payload = "cmd_alarm_menu"
                }
            },
            new List<Button>
            {
                new OpenAppButton
                {
                    Text = "🔄 Госуслуги",
                    WebApp = maxBotApp,
                    Payload = maxUserId
                },
                new CallbackButton { Text = "🔗 Связать с Telegram", Payload = "cmd_link_account" }
            },
            new List<Button>
            {
                new CallbackButton { Text = "🗑 Удалить аккаунт", Payload = "cmd_delete_account" },
                new CallbackButton { Text = "🏠 Главное меню", Payload = "cmd_back_main" }
            }
        };

        string text = "⚙️ *Настройки*\n\n" +
                      $"• ⏰ Будильник: {(alarmOn ? "`🟢 Включен`" : "`🔴 Выключен`")}\n" +
                      $"• 👤 ID: `{maxUserId}`";

        await SendOrEditBannerScreenAsync(chatId, messageId, "banner_settings.png", text, keyboard);
    }

    private async Task ShowAlarmMenuAsync(long chatId, string maxUserId, string? messageId = null)
    {
        var user = await EnsureUserExistsAsync(maxUserId);
        var alarmOn = user.IsAlarmEnabled;
        var schedOn = user.IsScheduleReminderEnabled;
        var schedTime = user.ScheduleReminderTime ?? "07:30";
        var hwOn = user.IsHomeworkReminderEnabled;
        var hwTime = user.HomeworkReminderTime ?? "17:00";
        var equipOn = user.IsEquipmentReminderEnabled;
        var equipTime = user.EquipmentReminderTime ?? "20:30";

        var keyboard = new List<List<Button>>
        {
            new List<Button>
            {
                new CallbackButton
                {
                    Text = alarmOn ? "🔔 Будильник: 🟢 ВКЛЮЧЁН" : "🔕 Будильник: 🔴 ВЫКЛЮЧЕН",
                    Payload = "cmd_toggle_alarm"
                }
            },
            new List<Button>
            {
                new CallbackButton
                {
                    Text = $"📅 Расписание: {(schedOn ? schedTime : "ВЫКЛ")}",
                    Payload = "cmd_alarm_sched"
                },
                new CallbackButton
                {
                    Text = $"📚 Домашка: {(hwOn ? hwTime : "ВЫКЛ")}",
                    Payload = "cmd_alarm_hw"
                }
            },
            new List<Button>
            {
                new CallbackButton
                {
                    Text = $"🎒 Вещи: {(equipOn ? equipTime : "ВЫКЛ")}",
                    Payload = "cmd_alarm_equip"
                },
                new CallbackButton
                {
                    Text = "⚡ Проверить вещи",
                    Payload = "cmd_alarm_check_now"
                }
            },
            new List<Button>
            {
                new CallbackButton { Text = "⬅️ Настройки", Payload = "cmd_settings" },
                new CallbackButton { Text = "🏠 Главное меню", Payload = "cmd_back_main" }
            }
        };

        string text = "⏰ *Умный будильник*\n\n" +
                      $"• 🔔 Статус: {(alarmOn ? "`🟢 Активен`" : "`🔴 Выключен`")}\n" +
                      $"• 📅 Расписание: {(schedOn ? $"`{schedTime}`" : "`Выкл`")}\n" +
                      $"• 📚 Домашка: {(hwOn ? $"`{hwTime}`" : "`Выкл`")}\n" +
                      $"• 🎒 Вещи и рюкзак: {(equipOn ? $"`{equipTime}`" : "`Выкл`")}";

        await SendOrEditBannerScreenAsync(chatId, messageId, "banner_alarm.png", text, keyboard);
    }

    private async Task ShowDeleteAccountConfirmationAsync(long chatId, string maxUserId, string? messageId)
    {
        string text = "⚠️ *Удаление аккаунта*\n\n" +
                      "Вы действительно хотите безвозвратно удалить свой аккаунт и все данные из MirageDiary?\n\n" +
                      "• Все сохранённые сессии и токены Госуслуг будут немедленно стёрты с сервера.\n" +
                      "• Все персональные настройки, напоминания и расписания будут удалены.\n" +
                      "• Это действие необратимо.";

        var keyboard = new List<List<Button>>
        {
            new List<Button>
            {
                new CallbackButton { Text = "🗑 Да, удалить всё", Payload = "cmd_delete_account_confirm" }
            },
            new List<Button>
            {
                new CallbackButton { Text = "❌ Отмена", Payload = "cmd_settings" }
            }
        };

        await EditOrSendMessageAsync(chatId, messageId, text, keyboard);
    }

    private async Task ExecuteDeleteAccountAsync(long chatId, string maxUserId, string? messageId)
    {
        try
        {
            await _tokenStore.RevokeTokensAsync(maxUserId);
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == maxUserId);
            if (user != null)
            {
                _db.Users.Remove(user);
                await _db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DeleteAccount] Error deleting MAX user {UserId}", maxUserId);
        }

        _stateService.ClearState(chatId);

        string goodbyeText = "👋 *Ваш аккаунт и все связанные данные успешно удалены.*\n\n" +
                             "Токены авторизации отозваны. Если вы захотите вернуться, просто отправьте /start в любое время.";

        var keyboard = new List<List<Button>>
        {
            new List<Button>
            {
                new CallbackButton { Text = "🔄 Начать заново", Payload = "cmd_back_main" }
            }
        };

        await EditOrSendMessageAsync(chatId, messageId, goodbyeText, keyboard);
    }

    private async Task ShowLinkAccountAsync(long chatId, string maxUserId, string? messageId = null)
    {
        var code = _userSyncService.GenerateLinkCode(maxUserId);
        string text = "🔗 *Связка аккаунтов MAX и Telegram*\n\n" +
                      $"Ваш код для связки: `{code}` _(нажмите, чтобы скопировать)_\n\n" +
                      "📌 *Инструкция:*\n" +
                      "1️⃣ Откройте нашего бота в **Telegram**\n" +
                      $"2️⃣ Отправьте команду:\n`/link {code}`\n\n" +
                      "⏳ Код действует **15 минут**.\n" +
                      "После связки ваши токены Госуслуг, расписание, класс и настройки автоматически синхронизируются!";

        var keyboard = new List<List<Button>>
        {
            new List<Button> { new CallbackButton { Text = "🔄 Обновить код", Payload = "cmd_link_account" } },
            new List<Button> { new CallbackButton { Text = "🔙 Настройки", Payload = "cmd_settings" } }
        };

        await SendOrEditBannerScreenAsync(chatId, messageId, "banner_settings.png", text, keyboard);
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
        var user = await EnsureUserExistsAsync(maxUserId);
        user.IsAlarmEnabled = !user.IsAlarmEnabled;
        await _db.SaveChangesAsync();
        await ShowAlarmMenuAsync(chatId, maxUserId, messageId);
    }

    // ─── Schedule Reminder Menu ───
    private async Task ShowScheduleReminderMenuAsync(long chatId, string? messageId, string maxUserId)
    {
        var user = await EnsureUserExistsAsync(maxUserId);
        var enabled = user.IsScheduleReminderEnabled;
        var time = user.ScheduleReminderTime ?? "07:30";

        var keyboard = new List<List<Button>>
        {
            new List<Button>
            {
                new CallbackButton
                {
                    Text = enabled ? "🔕 Выключить расписание" : "🔔 Включить расписание",
                    Payload = "cmd_alarm_sched_toggle"
                }
            },
            new List<Button>
            {
                new CallbackButton { Text = "07:00", Payload = "cmd_alarm_sched_time:07:00" },
                new CallbackButton { Text = "07:30", Payload = "cmd_alarm_sched_time:07:30" },
                new CallbackButton { Text = "08:00", Payload = "cmd_alarm_sched_time:08:00" },
                new CallbackButton { Text = "20:00", Payload = "cmd_alarm_sched_time:20:00" }
            },
            new List<Button> { new CallbackButton { Text = "✏️ Ввести другое время", Payload = "cmd_alarm_sched_custom" } },
            new List<Button>
            {
                new CallbackButton { Text = "⬅️ К будильнику", Payload = "cmd_alarm_menu" },
                new CallbackButton { Text = "🏠 Главное меню", Payload = "cmd_back_main" }
            }
        };

        string text = "📅 *Напоминание о расписании уроков*\n\n" +
                      $"• *Статус*: {(enabled ? "🟢 Включено" : "🔴 Выключено")}\n" +
                      $"• *Время отправки*: `{(enabled ? time : "выключено")}`\n\n" +
                      "_Каждое утро бот будет автоматически присылать вам список уроков на сегодня._\n\n" +
                      "Выберите время отправки ниже или нажмите «✏️ Ввести другое время»:";

        await EditOrSendMessageAsync(chatId, messageId, text, keyboard);
    }

    private async Task ToggleScheduleReminderAsync(long chatId, string? messageId, string maxUserId)
    {
        var user = await EnsureUserExistsAsync(maxUserId);
        user.IsScheduleReminderEnabled = !user.IsScheduleReminderEnabled;
        await _db.SaveChangesAsync();
        await ShowScheduleReminderMenuAsync(chatId, messageId, maxUserId);
    }

    private async Task SetScheduleReminderTimeAsync(long chatId, string? messageId, string maxUserId, string time)
    {
        var user = await EnsureUserExistsAsync(maxUserId);
        user.ScheduleReminderTime = time;
        user.IsScheduleReminderEnabled = true;
        await _db.SaveChangesAsync();
        await ShowScheduleReminderMenuAsync(chatId, messageId, maxUserId);
    }

    // ─── Homework Reminder Menu ───
    private async Task ShowHomeworkReminderMenuAsync(long chatId, string? messageId, string maxUserId)
    {
        var user = await EnsureUserExistsAsync(maxUserId);
        var enabled = user.IsHomeworkReminderEnabled;
        var time = user.HomeworkReminderTime ?? "17:00";

        var keyboard = new List<List<Button>>
        {
            new List<Button>
            {
                new CallbackButton
                {
                    Text = enabled ? "🔕 Выключить домашку" : "🔔 Включить домашку",
                    Payload = "cmd_alarm_hw_toggle"
                }
            },
            new List<Button>
            {
                new CallbackButton { Text = "15:00", Payload = "cmd_alarm_hw_time:15:00" },
                new CallbackButton { Text = "16:00", Payload = "cmd_alarm_hw_time:16:00" },
                new CallbackButton { Text = "17:00", Payload = "cmd_alarm_hw_time:17:00" },
                new CallbackButton { Text = "18:00", Payload = "cmd_alarm_hw_time:18:00" },
                new CallbackButton { Text = "19:00", Payload = "cmd_alarm_hw_time:19:00" }
            },
            new List<Button> { new CallbackButton { Text = "✏️ Ввести другое время", Payload = "cmd_alarm_hw_custom" } },
            new List<Button>
            {
                new CallbackButton { Text = "⬅️ К будильнику", Payload = "cmd_alarm_menu" },
                new CallbackButton { Text = "🏠 Главное меню", Payload = "cmd_back_main" }
            }
        };

        string text = "📚 *Напоминание о домашнем задании*\n\n" +
                      $"• *Статус*: {(enabled ? "🟢 Включено" : "🔴 Выключено")}\n" +
                      $"• *Время отправки*: `{(enabled ? time : "выключено")}`\n\n" +
                      "_Каждый вечер бот будет присылать вам заданную на завтра домашку._\n\n" +
                      "Выберите удобное время ниже или нажмите «✏️ Ввести другое время»:";

        await EditOrSendMessageAsync(chatId, messageId, text, keyboard);
    }

    private async Task ToggleHomeworkReminderAsync(long chatId, string? messageId, string maxUserId)
    {
        var user = await EnsureUserExistsAsync(maxUserId);
        user.IsHomeworkReminderEnabled = !user.IsHomeworkReminderEnabled;
        await _db.SaveChangesAsync();
        await ShowHomeworkReminderMenuAsync(chatId, messageId, maxUserId);
    }

    private async Task SetHomeworkReminderTimeAsync(long chatId, string? messageId, string maxUserId, string time)
    {
        var user = await EnsureUserExistsAsync(maxUserId);
        user.HomeworkReminderTime = time;
        user.IsHomeworkReminderEnabled = true;
        await _db.SaveChangesAsync();
        await ShowHomeworkReminderMenuAsync(chatId, messageId, maxUserId);
    }

    // ─── Equipment Reminder Menu ───
    private async Task ShowEquipmentReminderMenuAsync(long chatId, string? messageId, string maxUserId)
    {
        var user = await EnsureUserExistsAsync(maxUserId);
        var enabled = user.IsEquipmentReminderEnabled;
        var time = user.EquipmentReminderTime ?? "20:30";

        var keyboard = new List<List<Button>>
        {
            new List<Button>
            {
                new CallbackButton
                {
                    Text = enabled ? "🔕 Выключить сбор рюкзака" : "🔔 Включить сбор рюкзака",
                    Payload = "cmd_alarm_equip_toggle"
                }
            },
            new List<Button>
            {
                new CallbackButton { Text = "19:00", Payload = "cmd_alarm_equip_time:19:00" },
                new CallbackButton { Text = "20:00", Payload = "cmd_alarm_equip_time:20:00" },
                new CallbackButton { Text = "20:30", Payload = "cmd_alarm_equip_time:20:30" },
                new CallbackButton { Text = "21:00", Payload = "cmd_alarm_equip_time:21:00" },
                new CallbackButton { Text = "21:30", Payload = "cmd_alarm_equip_time:21:30" }
            },
            new List<Button> { new CallbackButton { Text = "✏️ Ввести другое время", Payload = "cmd_alarm_equip_custom" } },
            new List<Button>
            {
                new CallbackButton { Text = "⬅️ К будильнику", Payload = "cmd_alarm_menu" },
                new CallbackButton { Text = "🏠 Главное меню", Payload = "cmd_back_main" }
            }
        };

        string text = "🎒 *Умный будильник: сбор рюкзака и принадлежности*\n\n" +
                      $"• *Статус*: {(enabled ? "🟢 Включено" : "🔴 Выключено")}\n" +
                      $"• *Время отправки*: `{(enabled ? time : "выключено")}`\n\n" +
                      "_Бот сканирует уроки и домашку на завтра и напоминает взять: циркуль, линейку, форму, краски, атлас и др._\n\n" +
                      "Выберите время сбора рюкзака или нажмите «✏️ Ввести другое время»:";

        await EditOrSendMessageAsync(chatId, messageId, text, keyboard);
    }

    private async Task ToggleEquipmentReminderAsync(long chatId, string? messageId, string maxUserId)
    {
        var user = await EnsureUserExistsAsync(maxUserId);
        user.IsEquipmentReminderEnabled = !user.IsEquipmentReminderEnabled;
        await _db.SaveChangesAsync();
        await ShowEquipmentReminderMenuAsync(chatId, messageId, maxUserId);
    }

    private async Task SetEquipmentReminderTimeAsync(long chatId, string? messageId, string maxUserId, string time)
    {
        var user = await EnsureUserExistsAsync(maxUserId);
        user.EquipmentReminderTime = time;
        user.IsEquipmentReminderEnabled = true;
        await _db.SaveChangesAsync();
        await ShowEquipmentReminderMenuAsync(chatId, messageId, maxUserId);
    }

    // =========================================================================
    // SMART ALARM (EQUIPMENT CHECK)
    // =========================================================================

    private async Task ExecuteAlarmCheckAsync(long chatId, string maxUserId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == maxUserId);
        if (user == null || string.IsNullOrEmpty(user.AccessToken))
        {
            await ShowLoginPromptAsync(chatId, maxUserId);
            return;
        }

        var hwString = await _diaryService.GetHomeworkStringAsync(maxUserId);
        var found = TriggerWords.Where(w => hwString.ToLowerInvariant().Contains(w)).ToList();

        if (found.Any())
        {
            var items = string.Join("\n• ", found.Select(t => char.ToUpper(t[0]) + t[1..]));
            await SendUserMessageAsync(chatId, new SendMessageRequest
            {
                Text = $"🎒 *Умный будильник: сбор рюкзака на завтра!*\n\nВам понадобятся:\n• {items}\n\nНе забудьте положить их в рюкзак прямо сейчас! 🎒",
                Format = MessageFormat.Markdown
            });
        }
        else
        {
            await SendUserMessageAsync(chatId, new SendMessageRequest
            {
                Text = "🎒 *Умный будильник*\n\nСпецинструментов на завтра не требуется. Проверьте обычные тетради и учебники по расписанию! ✅",
                Format = MessageFormat.Markdown
            });
        }
    }

    // =========================================================================
    // SUBSCRIPTION & PREMIUM
    // =========================================================================

    private async Task ShowSubscriptionAsync(long chatId, string maxUserId, string? messageId = null)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == maxUserId);
        bool hasPrem = user != null && user.HasPremium();
        bool isAdmin = user?.Role == "Admin";

        string statusLine;
        if (isAdmin)
        {
            statusLine = "⭐️ *Ваш статус:* Вечный Премиум (Администратор) 👑\n\n";
        }
        else if (hasPrem)
        {
            statusLine = user!.SubscriptionExpiresAtUtc != null
                ? $"⭐️ *Ваш статус:* Премиум активен (до {user.SubscriptionExpiresAtUtc:dd.MM.yyyy}) ✅\n\n"
                : "⭐️ *Ваш статус:* Премиум активен (Бессрочно) ✅\n\n";
        }
        else
        {
            statusLine = "⭐️ *Ваш статус:* Базовый аккаунт\n\n";
        }

        string text = $"⭐ *MirageDiary Premium (Бета-тест)*\n\n" +
                      statusLine +
                      "Премиум открывает доступ к передовым возможностям бота:\n\n" +
                      "🔥 *Мгновенный Live Grade Watcher*\n" +
                      "_Уведомления о новых оценках по расписанию звонков прямо в момент выставления учителем._\n\n" +
                      "⏰ *Умный будильник («Спи дольше»)*\n" +
                      "_Автоматический сдвиг времени будильника при отмене или переносе первого урока._\n\n" +
                      "🎒 *Контроль инвентаря и формы*\n" +
                      "_Умные напоминания взять лыжи, спортивную форму, краски или циркуль накануне уроков._\n\n" +
                      "⚡️ *Максимальный приоритет P2P-кэша*\n" +
                      "_Мгновенная загрузка расписания и домашних заданий даже в дни падений Госуслуг._\n\n" +
                      "💰 *Стоимость в период бета-теста:* **50 ₽ / месяц**";

        var keyboard = new List<List<Button>>();

        if (!hasPrem && !isAdmin)
        {
            keyboard.Add(new List<Button>
            {
                new CallbackButton { Text = "💳 Оформить подписку за 50 ₽ (Бета)", Payload = "cmd_sub_activate_beta" }
            });
        }

        keyboard.Add(new List<Button> { new CallbackButton { Text = "🏠 Главное меню", Payload = "cmd_back_main" } });

        await SendOrEditBannerScreenAsync(chatId, messageId, "banner_premium.png", text, keyboard);
    }

    private async Task ActivateBetaSubscriptionAsync(long chatId, string maxUserId, string? messageId = null)
    {
        var user = await EnsureUserExistsAsync(maxUserId);
        user.IsSubscribed = true;
        user.SubscriptionExpiresAtUtc = DateTime.UtcNow.AddDays(30);
        await _db.SaveChangesAsync();

        string text = "🎉 *Поздравляем с активацией MirageDiary Premium!* 🚀\n\n" +
                      "Вам подключен бета-доступ на 30 дней. Все функции активированы:\n" +
                      "• Уведомления о новых оценках по звонкам\n" +
                      "• Умный будильник с распознаванием отмен уроков\n" +
                      "• Контроль формы и инструментов\n\n" +
                      "Спасибо за поддержку проекта!";

        var keyboard = new List<List<Button>>
        {
            new List<Button> { new CallbackButton { Text = "🏠 Главное меню", Payload = "cmd_back_main" } }
        };

        await EditOrSendMessageAsync(chatId, messageId, text, keyboard);
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
                _logger.LogWarning(ex, "Failed to edit MAX message {MessageId}, sending a new message instead.", messageId);
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send MAX message to user {UserId}", userId);
            throw;
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
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == maxUserId);
        bool isNew = false;
        if (user == null)
        {
            user = new UserEntity
            {
                Id = maxUserId,
                AccessToken = "",
                RefreshToken = "",
                StudentId = "",
                ClassName = "",
                OrgName = "",
                Role = "Student",
                Platform = "max",
                HasAcceptedPrivacyPolicy = false,
                Shift = 1,
                UtcOffsetHours = 5,
                IsAlarmEnabled = true,
                IsScheduleReminderEnabled = true,
                ScheduleReminderTime = "07:30",
                IsHomeworkReminderEnabled = true,
                HomeworkReminderTime = "17:00",
                IsEquipmentReminderEnabled = true,
                EquipmentReminderTime = "20:30"
            };
            _db.Users.Add(user);
            isNew = true;
        }

        bool changed = false;
        if (!string.IsNullOrEmpty(username) && user.Username != username)
        {
            user.Username = username;
            changed = true;
        }
        if (!string.IsNullOrEmpty(firstName) && user.FirstName != firstName)
        {
            user.FirstName = firstName;
            changed = true;
        }
        if (!string.IsNullOrEmpty(lastName) && user.LastName != lastName)
        {
            user.LastName = lastName;
            changed = true;
        }
        if (user.Platform != "max")
        {
            user.Platform = "max";
            changed = true;
        }

        bool needsActivityTouch = user.UpdatedAt == null || (DateTime.UtcNow - user.UpdatedAt.Value).TotalMinutes >= 30;

        if (changed || isNew || needsActivityTouch)
        {
            user.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        return user;
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
}
