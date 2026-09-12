using System;
using System.IO;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MirageDiary.Server.Data;
using MirageDiary.Server.Models;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace MirageDiary.Server.Services;

public class BotCommandService
{
    private static readonly ConcurrentDictionary<string, string> _bannerFileIdCache = new();
    private static readonly ConcurrentDictionary<long, string> _currentBannerByChat = new();
    private static readonly ConcurrentDictionary<string, DateTime> _profileRefreshCooldown = new();
    private readonly ITelegramBotClient _botClient;
    private readonly DiaryInteractionService _diaryService;
    private readonly Microsoft.Extensions.Configuration.IConfiguration _config;
    private readonly RoleService _roleService;
    private readonly BotStateService _stateService;
    private readonly MaintenanceModeService _maintenanceService;
    private readonly AppDbContext _db;
    private readonly MirageDiary.Core.ITokenStore _tokenStore;

    // ─── Trigger words for SmartAlarm ────────────────────────────────────────
    private static readonly string[] TriggerWords =
    {
        "циркуль", "линейка", "краски", "альбом", "форма",
        "транспортир", "контурные карты", "кисти", "карандаши", "тетрадь"
    };

    private readonly SchoolBellScheduleService _bellSchedule;
    private readonly UserSyncService _userSyncService;

    public BotCommandService(
        ITelegramBotClient botClient,
        DiaryInteractionService diaryService,
        Microsoft.Extensions.Configuration.IConfiguration config,
        RoleService roleService,
        BotStateService stateService,
        MaintenanceModeService maintenanceService,
        AppDbContext db,
        SchoolBellScheduleService bellSchedule,
        UserSyncService userSyncService,
        MirageDiary.Core.ITokenStore tokenStore)
    {
        _botClient = botClient;
        _diaryService = diaryService;
        _config = config;
        _roleService = roleService;
        _stateService = stateService;
        _maintenanceService = maintenanceService;
        _db = db;
        _bellSchedule = bellSchedule;
        _userSyncService = userSyncService;
        _tokenStore = tokenStore;
    }

    // =========================================================================
    // ENTRY POINT
    // =========================================================================

    public async Task HandleUpdateAsync(Update update)
    {
        if (update.Type == UpdateType.Message && update.Message != null)
            await HandleMessageAsync(update.Message);
        else if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery != null)
            await HandleCallbackQueryAsync(update.CallbackQuery);
    }

    // =========================================================================
    // MESSAGE HANDLER
    // =========================================================================

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, DateTime> _userCooldowns = new();

    private bool CheckUserCooldown(long chatId)
    {
        var now = DateTime.UtcNow;
        if (_userCooldowns.TryGetValue(chatId, out var lastTime))
        {
            if ((now - lastTime).TotalSeconds < 2.0)
            {
                return false;
            }
        }
        _userCooldowns[chatId] = now;
        return true;
    }

    private async Task HandleMessageAsync(Message message)
    {
        var chatId = message.Chat.Id;
        var chatIdStr = chatId.ToString();
        var text = message.Text?.Trim() ?? "";

        string? username = message.From?.Username;
        string? firstName = message.From?.FirstName;
        string? lastName = message.From?.LastName;

        var currentUser = await EnsureUserExistsAsync(chatIdStr, username, firstName, lastName);

        // /start or /menu — always reset and show main menu
        if (text.StartsWith("/start") || text.StartsWith("/menu") || text == "Отмена")
        {
            _stateService.ClearState(chatId);
            await ShowMainMenuAsync(chatId);
            return;
        }

        if (text.StartsWith("/delete_account"))
        {
            _stateService.ClearState(chatId);
            await ShowDeleteAccountConfirmationAsync(chatId, chatIdStr, null);
            return;
        }

        if (text.StartsWith("/logout") || text.StartsWith("/exit"))
        {
            _stateService.ClearState(chatId);
            await ShowLogoutConfirmationAsync(chatId, chatIdStr, null);
            return;
        }

        if (!CheckUserCooldown(chatId))
        {
            await _botClient.SendMessage(chatId, "⏳ _Защита от спама: подождите 2 секунды перед следующим запросом._", parseMode: ParseMode.Markdown);
            return;
        }



        // File uploads (schedule from HeadTeacher / Admin)
        if (message.Type == MessageType.Photo || message.Type == MessageType.Document)
        {
            await HandleFileUploadAsync(chatId, chatIdStr, message);
            return;
        }

        // State machine
        var session = _stateService.GetSession(chatId);
        if (session.State != UserState.Normal)
        {
            await HandleStateInputAsync(chatId, text, session);
            return;
        }

        // Text command shortcuts
        if (text.StartsWith("/now") || text.StartsWith("/bell") || text.StartsWith("/звонок"))
        {
            await ExecuteNowAsync(chatId, chatIdStr);
        }
        else if (text.StartsWith("/premium") || text.StartsWith("/sub") || text.StartsWith("/подписка"))
        {
            await _botClient.SendMessage(chatId,
                "⭐ *MirageDiary*\n\n" +
                "Раздел платной подписки закрыт — все функции бота (расписание, оценки, калькулятор, домашка и уведомления) полностью бесплатны! 🚀",
                parseMode: ParseMode.Markdown);
        }
        else if (text.StartsWith("/link") || text.StartsWith("/sync") || text.StartsWith("/связать"))
        {
            await HandleLinkCommandAsync(chatId, chatIdStr, text);
        }
        else if (text.StartsWith("/alarm"))   await ExecuteAlarmCheckAsync(chatId, chatIdStr);
        else if (text.StartsWith("/profile") || text.StartsWith("/me"))
        {
            await ExecuteProfileAsync(chatId, chatIdStr);
        }
        else if (text.StartsWith("/homework"))
        {
            string dateArg = text.Length > 9 ? text[9..].Trim() : "";
            await ExecuteHomeworkAsync(chatId, chatIdStr, dateArg);
        }
        else if (text.StartsWith("/schedule"))
        {
            string dateArg = text.Length > 9 ? text[9..].Trim() : "";
            await ExecuteScheduleAsync(chatId, chatIdStr, dateArg);
        }
        else if (text.StartsWith("/marks") || text.StartsWith("/grades"))
        {
            await ExecuteMarksAsync(chatId, chatIdStr);
        }
        else if (text.StartsWith("/calc") || text.StartsWith("/calculator"))
        {
            await ShowGradeCalculatorSubjectsAsync(chatId, chatIdStr);
        }
        else if (text.StartsWith("/children") || text.StartsWith("/kids"))
        {
            await ShowChildrenMenuAsync(chatId, chatIdStr);
        }
    }

    // ─── File upload (schedule) ───────────────────────────────────────────────
    private async Task HandleFileUploadAsync(long chatId, string chatIdStr, Message message)
    {
        var role = await _roleService.GetUserRoleAsync(chatIdStr);
        if (role != "HeadTeacher" && role != "Admin")
        {
            await _botClient.SendMessage(chatId, "❌ Только завучи могут загружать расписание.");
            return;
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == chatIdStr);
        if (user == null || string.IsNullOrEmpty(user.OrgName))
        {
            await _botClient.SendMessage(chatId, "❌ Вы не привязаны ни к какой школе.");
            return;
        }

        string fileId = message.Type == MessageType.Photo
            ? message.Photo![^1].FileId
            : message.Document!.FileId;
        string fileType = message.Type == MessageType.Photo ? "photo" : "document";

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

        await _botClient.SendMessage(chatId,
            $"✅ Расписание для школы *{user.OrgName}* успешно сохранено!\n\n" +
            $"Теперь ученики смогут получить его через кнопку «📅 Расписание».",
            parseMode: ParseMode.Markdown);
    }

    // ─── State machine input ──────────────────────────────────────────────────
    private async Task HandleStateInputAsync(long chatId, string text, UserSession session)
    {
        switch (session.State)
        {
            case UserState.WaitingForHeadTeacherId:
                _stateService.UpdateState(chatId, UserState.WaitingForHeadTeacherOrg, text);
                await _botClient.SendMessage(chatId,
                    "🏫 Теперь введите название школы, к которой привязать этого завуча:\n_(или напишите «Отмена»)_",
                    parseMode: ParseMode.Markdown);
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
                if (text.Trim().Equals("отмена", StringComparison.OrdinalIgnoreCase))
                {
                    _stateService.ClearState(chatId);
                    await _botClient.SendMessage(chatId, "❌ Ввод времени отменён.");
                    return;
                }
                if (TryParseTime(text, out var schedTime))
                {
                    var user = await EnsureUserExistsAsync(chatId.ToString());
                    user.ScheduleReminderTime = schedTime;
                    user.IsScheduleReminderEnabled = true;
                    await _db.SaveChangesAsync();
                    _stateService.ClearState(chatId);
                    await _botClient.SendMessage(chatId, $"✅ Время напоминания о расписании установлено на *{schedTime}*!\n\nКаждое утро в это время вы будете получать список уроков.", parseMode: ParseMode.Markdown);
                }
                else
                {
                    await _botClient.SendMessage(chatId, "❌ Неверный формат времени. Введите время в формате *ЧЧ:ММ* (например: `07:30` или `8:00`) или напишите «Отмена»:", parseMode: ParseMode.Markdown);
                }
                break;

            case UserState.WaitingForHomeworkReminderTime:
                if (text.Trim().Equals("отмена", StringComparison.OrdinalIgnoreCase))
                {
                    _stateService.ClearState(chatId);
                    await _botClient.SendMessage(chatId, "❌ Ввод времени отменён.");
                    return;
                }
                if (TryParseTime(text, out var hwTime))
                {
                    var user = await EnsureUserExistsAsync(chatId.ToString());
                    user.HomeworkReminderTime = hwTime;
                    user.IsHomeworkReminderEnabled = true;
                    await _db.SaveChangesAsync();
                    _stateService.ClearState(chatId);
                    await _botClient.SendMessage(chatId, $"✅ Время напоминания о домашке установлено на *{hwTime}*!\n\nКаждый день в это время бот пришлет список заданий на завтра.", parseMode: ParseMode.Markdown);
                }
                else
                {
                    await _botClient.SendMessage(chatId, "❌ Неверный формат времени. Введите время в формате *ЧЧ:ММ* (например: `17:00` или `18:30`) или напишите «Отмена»:", parseMode: ParseMode.Markdown);
                }
                break;

            case UserState.WaitingForEquipmentReminderTime:
                if (text.Trim().Equals("отмена", StringComparison.OrdinalIgnoreCase))
                {
                    _stateService.ClearState(chatId);
                    await _botClient.SendMessage(chatId, "❌ Ввод времени отменён.");
                    return;
                }
                if (TryParseTime(text, out var equipTime))
                {
                    var user = await EnsureUserExistsAsync(chatId.ToString());
                    user.EquipmentReminderTime = equipTime;
                    user.IsEquipmentReminderEnabled = true;
                    await _db.SaveChangesAsync();
                    _stateService.ClearState(chatId);
                    await _botClient.SendMessage(chatId, $"✅ Время сбора рюкзака установлено на *{equipTime}*!\n\nВечером бот напомнит, какие вещи и инструменты положить в рюкзак.", parseMode: ParseMode.Markdown);
                }
                else
                {
                    await _botClient.SendMessage(chatId, "❌ Неверный формат времени. Введите время в формате *ЧЧ:ММ* (например: `20:30` или `21:00`) или напишите «Отмена»:", parseMode: ParseMode.Markdown);
                }
                break;
        }
    }

    // =========================================================================
    // CALLBACK QUERY HANDLER
    // =========================================================================

    private async Task HandleCallbackQueryAsync(CallbackQuery cq)
    {
        var chatId = cq.Message?.Chat.Id;
        if (chatId == null) return;
        var msgId = cq.Message!.MessageId;
        var chatIdStr = chatId.ToString()!;
        var data = cq.Data ?? "";
        // Some commands provide custom notification alerts or text in AnswerCallbackQuery
        if (data != "cmd_profile_refresh" && !data.StartsWith("cmd_select_child:"))
        {
            _ = _botClient.AnswerCallbackQuery(cq.Id);
        }

        // Don't clear state if we're in a wizard flow triggered by admin panel
        if (!data.StartsWith("cmd_admin_"))
            _stateService.ClearState(chatId.Value);

        switch (data)
        {
            // ── Main menu ──────────────────────────────────────────────────
            case "cmd_back_main":
                await ShowMainMenuAsync(chatId.Value, msgId);
                break;

            case "cmd_back_main_new":
                await ShowMainMenuAsync(chatId.Value, msgId);
                break;

            case "cmd_now":
                await ExecuteNowAsync(chatId.Value, chatIdStr, msgId);
                break;

            case "cmd_subscription":
                await ShowMainMenuAsync(chatId.Value, msgId);
                break;

            case "cmd_accept_privacy":
                var pu = await _db.Users.FindAsync(chatIdStr);
                if (pu != null)
                {
                    pu.HasAcceptedPrivacyPolicy = true;
                    pu.PrivacyPolicyAcceptedAtUtc = DateTime.UtcNow;
                    pu.UpdatedAt = DateTime.UtcNow;
                    await _db.SaveChangesAsync();
                    await _userSyncService.SyncUserAcrossPlatformsAsync(pu);
                }
                await ShowMainMenuAsync(chatId.Value, msgId);
                break;

            case "cmd_check_channel_sub":
                var chName = _config["Telegram:RequiredChannel"] ?? "";
                bool isSubbed = true;
                if (!string.IsNullOrWhiteSpace(chName))
                {
                    try
                    {
                        var m = await _botClient.GetChatMember(chName, chatId.Value);
                        isSubbed = m.Status is ChatMemberStatus.Member or ChatMemberStatus.Administrator or ChatMemberStatus.Creator or ChatMemberStatus.Restricted;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ChannelCheck] Error on check: {ex.Message}");
                        isSubbed = true;
                    }
                }

                if (isSubbed)
                {
                    await _botClient.AnswerCallbackQuery(cq.Id, "✅ Подписка подтверждена!", showAlert: false);
                    await ShowMainMenuAsync(chatId.Value, msgId);
                }
                else
                {
                    await _botClient.AnswerCallbackQuery(cq.Id, "❌ Вы ещё не подписались на канал. Пожалуйста, подпишитесь и повторите проверку!", showAlert: true);
                }
                break;

            case "cmd_sub_activate_beta":
                await ActivateBetaSubscriptionAsync(chatId.Value, chatIdStr, msgId);
                break;

            case { } s when s.StartsWith("cmd_set_shift:"):
                if (int.TryParse(s["cmd_set_shift:".Length..], out int newShift))
                {
                    var u = await _db.Users.FindAsync(chatIdStr);
                    if (u != null)
                    {
                        u.Shift = newShift;
                        await _db.SaveChangesAsync();
                    }
                    await ExecuteNowAsync(chatId.Value, chatIdStr, msgId);
                }
                break;

            // ── Homework ───────────────────────────────────────────────────
            case "cmd_homework_refresh":
                await ExecuteHomeworkAsync(chatId.Value, chatIdStr, null, msgId, forceRefresh: true);
                break;

            case { } s when s.StartsWith("cmd_homework:"):
                var dateParam = s["cmd_homework:".Length..];
                await ExecuteHomeworkAsync(chatId.Value, chatIdStr, dateParam, msgId);
                break;

            case "cmd_homework":
                await ExecuteHomeworkAsync(chatId.Value, chatIdStr, null, msgId);
                break;

            // ── Schedule ───────────────────────────────────────────────────
            case "cmd_schedule_refresh":
                await ExecuteScheduleAsync(chatId.Value, chatIdStr, null, msgId, forceRefresh: true);
                break;

            case { } s when s.StartsWith("cmd_schedule:"):
                var schedDate = s["cmd_schedule:".Length..];
                await ExecuteScheduleAsync(chatId.Value, chatIdStr, schedDate, msgId);
                break;

            case "cmd_schedule":
                await ExecuteScheduleAsync(chatId.Value, chatIdStr, null, msgId);
                break;

            // ── Marks & Calculator ──────────────────────────────────────────
            case "cmd_marks_refresh":
                await ExecuteMarksAsync(chatId.Value, chatIdStr, msgId, forceRefresh: true);
                break;

            case "cmd_marks":
                await ExecuteMarksAsync(chatId.Value, chatIdStr, msgId);
                break;

            case "cmd_calc_refresh":
                await ShowGradeCalculatorSubjectsAsync(chatId.Value, chatIdStr, msgId, forceRefresh: true);
                break;

            case "cmd_calc":
                await ShowGradeCalculatorSubjectsAsync(chatId.Value, chatIdStr, msgId);
                break;

            case { } s when s.StartsWith("cmd_calc_subj:"):
                if (int.TryParse(s["cmd_calc_subj:".Length..], out int subjIdx))
                {
                    await ShowSubjectTargetOptionsAsync(chatId.Value, chatIdStr, msgId, subjIdx);
                }
                break;

            case { } s when s.StartsWith("cmd_calc_target:"):
                var parts = s["cmd_calc_target:".Length..].Split(':');
                if (parts.Length == 2 && int.TryParse(parts[0], out int sIdx) && int.TryParse(parts[1], out int targetG))
                {
                    await ShowTargetCalculationResultAsync(chatId.Value, chatIdStr, msgId, sIdx, targetG);
                }
                break;

            // ── Profile & Children ──────────────────────────────────────────
            case "cmd_profile_refresh":
                if (_profileRefreshCooldown.TryGetValue(chatIdStr, out var lastRefreshTime))
                {
                    var elapsed = DateTime.UtcNow - lastRefreshTime;
                    var remaining = TimeSpan.FromSeconds(20) - elapsed;
                    if (remaining > TimeSpan.Zero)
                    {
                        await _botClient.AnswerCallbackQuery(cq.Id, $"⏳ Подождите {Math.Ceiling(remaining.TotalSeconds)} сек. перед следующим обновлением!", showAlert: true);
                        break;
                    }
                }
                _profileRefreshCooldown[chatIdStr] = DateTime.UtcNow;
                await _botClient.AnswerCallbackQuery(cq.Id, "🔄 Обновляем данные профиля...", showAlert: false);
                await ExecuteProfileAsync(chatId.Value, chatIdStr, msgId, forceRefresh: true);
                break;

            case "cmd_profile":
                await ExecuteProfileAsync(chatId.Value, chatIdStr, msgId);
                break;

            case "cmd_children":
                await ShowChildrenMenuAsync(chatId.Value, chatIdStr, msgId);
                break;

            case { } s when s.StartsWith("cmd_select_child:"):
                var childId = s["cmd_select_child:".Length..];
                await _diaryService.SelectChildAsync(chatIdStr, childId);
                await _botClient.AnswerCallbackQuery(cq.Id, "✅ Ребенок успешно выбран!");
                await ExecuteProfileAsync(chatId.Value, chatIdStr, msgId);
                break;

            // ── Auth / Logout ──
            case "cmd_logout":
                await ShowLogoutConfirmationAsync(chatId.Value, chatIdStr, msgId);
                break;

            case "cmd_confirm_logout":
                await PerformLogoutAsync(chatId.Value, chatIdStr, msgId);
                break;

            // ── Settings & Smart Reminders ──────────────────────────────────
            case "cmd_settings":
                await ShowSettingsMenuAsync(chatId.Value, msgId, chatIdStr);
                break;

            case "cmd_alarm_menu":
                await ShowAlarmMenuAsync(chatId.Value, msgId, chatIdStr);
                break;

            case "cmd_alarm_toggle_master":
                await ToggleMasterAlarmAsync(chatId.Value, msgId, chatIdStr);
                break;

            case "cmd_alarm_check_now":
                await ExecuteAlarmCheckAsync(chatId.Value, chatIdStr);
                break;

            case "cmd_delete_account":
                await ShowDeleteAccountConfirmationAsync(chatId.Value, chatIdStr, msgId);
                break;

            case "cmd_delete_account_confirm":
                await ExecuteDeleteAccountAsync(chatId.Value, chatIdStr, msgId);
                break;

            case "cmd_link_account":
                await ShowLinkAccountAsync(chatId.Value, chatIdStr, msgId);
                break;

            case "cmd_alarm_sched":
                await ShowScheduleReminderMenuAsync(chatId.Value, msgId, chatIdStr);
                break;

            case "cmd_alarm_sched_toggle":
                await ToggleScheduleReminderAsync(chatId.Value, msgId, chatIdStr);
                break;

            case { } s when s.StartsWith("cmd_alarm_sched_time:"):
                var sTime = s["cmd_alarm_sched_time:".Length..];
                await SetScheduleReminderTimeAsync(chatId.Value, msgId, chatIdStr, sTime);
                break;

            case "cmd_alarm_sched_custom":
                _stateService.UpdateState(chatId.Value, UserState.WaitingForScheduleReminderTime);
                await SafeEditTextOrCaptionAsync(chatId.Value, msgId,
                    "📅 *Напоминание о расписании*\n\nВведите время отправки в формате *ЧЧ:ММ* (например, `07:15` или `08:00`):\n_(или напишите «Отмена»)_",
                    new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("❌ Отмена", "cmd_alarm_sched")));
                break;

            case "cmd_alarm_hw":
                await ShowHomeworkReminderMenuAsync(chatId.Value, msgId, chatIdStr);
                break;

            case "cmd_alarm_hw_toggle":
                await ToggleHomeworkReminderAsync(chatId.Value, msgId, chatIdStr);
                break;

            case { } s when s.StartsWith("cmd_alarm_hw_time:"):
                var hTime = s["cmd_alarm_hw_time:".Length..];
                await SetHomeworkReminderTimeAsync(chatId.Value, msgId, chatIdStr, hTime);
                break;

            case "cmd_alarm_hw_custom":
                _stateService.UpdateState(chatId.Value, UserState.WaitingForHomeworkReminderTime);
                await SafeEditTextOrCaptionAsync(chatId.Value, msgId,
                    "📚 *Напоминание о домашнем задании*\n\nВведите время отправки в формате *ЧЧ:ММ* (например, `16:30` или `18:00`):\n_(или напишите «Отмена»)_",
                    new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("❌ Отмена", "cmd_alarm_hw")));
                break;

            case "cmd_alarm_equip":
                await ShowEquipmentReminderMenuAsync(chatId.Value, msgId, chatIdStr);
                break;

            case "cmd_alarm_equip_toggle":
                await ToggleEquipmentReminderAsync(chatId.Value, msgId, chatIdStr);
                break;

            case { } s when s.StartsWith("cmd_alarm_equip_time:"):
                var eTime = s["cmd_alarm_equip_time:".Length..];
                await SetEquipmentReminderTimeAsync(chatId.Value, msgId, chatIdStr, eTime);
                break;

            case "cmd_alarm_equip_custom":
                _stateService.UpdateState(chatId.Value, UserState.WaitingForEquipmentReminderTime);
                await SafeEditTextOrCaptionAsync(chatId.Value, msgId,
                    "🎒 *Умный будильник (сбор рюкзака)*\n\nВведите время напоминания в формате *ЧЧ:ММ* (например, `20:15` или `21:00`):\n_(или напишите «Отмена»)_",
                    new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("❌ Отмена", "cmd_alarm_equip")));
                break;

            // ── Admin Panel ────────────────────────────────────────────────
            case "cmd_admin_panel":
                var roleCheck = await _roleService.GetUserRoleAsync(chatIdStr);
                if (roleCheck == "Admin")
                    await ShowAdminPanelAsync(chatId.Value, msgId);
                break;

            case "cmd_admin_headteachers":
                await ShowHeadTeachersMenuAsync(chatId.Value, msgId, chatIdStr);
                break;

            case "cmd_admin_add_ht":
                _stateService.UpdateState(chatId.Value, UserState.WaitingForHeadTeacherId);
                await SafeEditTextOrCaptionAsync(chatId.Value, msgId,
                    "👤 *Добавление завуча*\n\nВведите Telegram ID, @username или имя пользователя:\n_(или напишите «Отмена»)_",
                    new InlineKeyboardMarkup(
                        InlineKeyboardButton.WithCallbackData("❌ Отмена", "cmd_admin_headteachers")),
                    ParseMode.Markdown);
                break;

            case { } s when s.StartsWith("cmd_admin_remove_ht:"):
                var htId = s["cmd_admin_remove_ht:".Length..];
                await RemoveHeadTeacherAsync(chatId.Value, msgId, chatIdStr, htId);
                break;

            case "cmd_admin_maintenance":
                await ShowMaintenanceMenuAsync(chatId.Value, msgId, chatIdStr);
                break;

            case "cmd_admin_maintenance_on":
                await ToggleMaintenanceAsync(chatId.Value, msgId, chatIdStr, true);
                break;

            case "cmd_admin_maintenance_off":
                await ToggleMaintenanceAsync(chatId.Value, msgId, chatIdStr, false);
                break;

            case "cmd_admin_broadcast":
                var roleBc = await _roleService.GetUserRoleAsync(chatIdStr);
                if (roleBc == "Admin")
                {
                    _stateService.UpdateState(chatId.Value, UserState.WaitingForBroadcastText);
                    await SafeEditTextOrCaptionAsync(chatId.Value, msgId,
                        "📢 *Рассылка*\n\nВведите текст сообщения для всех пользователей бота:\n_(или напишите «Отмена»)_",
                        new InlineKeyboardMarkup(
                            InlineKeyboardButton.WithCallbackData("❌ Отмена", "cmd_admin_panel")),
                        ParseMode.Markdown);
                }
                break;

            case "cmd_admin_stats":
                await ShowStatsAsync(chatId.Value, msgId, chatIdStr);
                break;

            case "cmd_back_admin":
                await ShowAdminPanelAsync(chatId.Value, msgId);
                break;
        }
    }

    // =========================================================================
    // SCREENS — каждый экран редактирует одно сообщение
    // =========================================================================

    // ─── Main menu ────────────────────────────────────────────────────────────
    private async Task ShowMainMenuAsync(long chatId, int? msgId = null)
    {
        var chatIdStr = chatId.ToString();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == chatIdStr);

        // 1. Check Privacy Policy Onboarding (152-FZ)
        if (user == null)
        {
            user = new UserEntity { Id = chatIdStr, Role = "Student", HasAcceptedPrivacyPolicy = false };
            _db.Users.Add(user);
            await _db.SaveChangesAsync();
            await ShowPrivacyPolicyOnboardingAsync(chatId, msgId);
            return;
        }
        else if (!user.HasAcceptedPrivacyPolicy)
        {
            await ShowPrivacyPolicyOnboardingAsync(chatId, msgId);
            return;
        }

        // 2. Check Mandatory Telegram Channel Subscription
        if (!await EnsureChannelSubscribedAsync(chatId, chatIdStr, msgId))
        {
            return;
        }

        var role = await _roleService.GetUserRoleAsync(chatIdStr);
        if (role != user.Role)
        {
            user.Role = role;
            await _db.SaveChangesAsync();
        }
        var isAuthorized = !string.IsNullOrEmpty(user.AccessToken);
        var isMaintenance = _maintenanceService.IsMaintenanceModeActive();
        var webAppUrl = _config["MiniAppUrl"] ?? "https://t.me";

        var buttons = new List<IEnumerable<InlineKeyboardButton>>();

        if (!isAuthorized)
        {
            buttons.Add(new[] { InlineKeyboardButton.WithWebApp("🔐 Войти через Госуслуги", new WebAppInfo { Url = webAppUrl }) });
        }

        buttons.Add(new[]
        {
            InlineKeyboardButton.WithCallbackData("📅 Расписание", "cmd_schedule"),
            InlineKeyboardButton.WithCallbackData("📚 Домашка", "cmd_homework")
        });
        buttons.Add(new[]
        {
            InlineKeyboardButton.WithCallbackData("📊 Оценки", "cmd_marks"),
            InlineKeyboardButton.WithCallbackData("🎯 Калькулятор", "cmd_calc")
        });
        buttons.Add(new[]
        {
            InlineKeyboardButton.WithCallbackData("👤 Профиль", "cmd_profile"),
            InlineKeyboardButton.WithCallbackData("⚙️ Настройки", "cmd_settings")
        });

        if (role == "Admin")
        {
            buttons.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData("🔔 До звонка", "cmd_now"),
                InlineKeyboardButton.WithCallbackData("👑 Админ-панель", "cmd_admin_panel")
            });
        }
        else
        {
            buttons.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData("🔔 До звонка", "cmd_now")
            });
        }

        var maintenanceBanner = isMaintenance ? "\n🔧 _Идут технические работы_" : "";

        string header;
        if (role == "Admin")
        {
            header = "👑 *Администратор* • Mirage Diary";
        }
        else if (role == "HeadTeacher")
        {
            header = $"🏫 *Завуч* • {(!string.IsNullOrEmpty(user?.OrgName) ? user.OrgName : "Mirage Diary")}";
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
            header = "👋 *Mirage Diary* — школьный дневник";
        }

        var text = header + maintenanceBanner;
        if (role == "HeadTeacher")
        {
            text += "\n\n💡 _Отправьте фото или PDF с расписанием — я сохраню его для учеников вашей школы._";
        }

        var markup = new InlineKeyboardMarkup(buttons);

        await SendOrEditBannerScreenAsync(chatId, msgId, "banner_menu.png", text, markup);
    }

    // ─── Settings & Account ───────────────────────────────────────────
    private async Task ShowSettingsMenuAsync(long chatId, int msgId, string chatIdStr)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == chatIdStr);
        var alarmOn = user?.IsAlarmEnabled ?? true;
        var webAppUrl = _config["MiniAppUrl"] ?? "https://t.me";

        var buttons = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData($"⏰ Будильник: {(alarmOn ? "🟢 Вкл" : "🔴 Выкл")}", "cmd_alarm_menu") },
            new[]
            {
                InlineKeyboardButton.WithWebApp("🔄 Госуслуги", new WebAppInfo { Url = webAppUrl }),
                InlineKeyboardButton.WithCallbackData("🔗 Связать с MAX", "cmd_link_account")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🗑 Удалить аккаунт", "cmd_delete_account"),
                InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "cmd_back_main")
            }
        });

        string text = "⚙️ *Настройки*\n\n" +
                      $"• ⏰ Будильник: {(alarmOn ? "`🟢 Включен`" : "`🔴 Выключен`")}\n" +
                      $"• 👤 ID: `{chatIdStr}`";

        await SendOrEditBannerScreenAsync(chatId, msgId, "banner_settings.png", text, buttons);
    }

    // ─── Dedicated Smart Alarm Menu ───────────────────────────────────────────
    private async Task ShowAlarmMenuAsync(long chatId, int msgId, string chatIdStr)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == chatIdStr);
        var alarmOn = user?.IsAlarmEnabled ?? true;
        var schedOn = user?.IsScheduleReminderEnabled ?? true;
        var schedTime = user?.ScheduleReminderTime ?? "07:30";
        var hwOn = user?.IsHomeworkReminderEnabled ?? true;
        var hwTime = user?.HomeworkReminderTime ?? "17:00";
        var equipOn = user?.IsEquipmentReminderEnabled ?? true;
        var equipTime = user?.EquipmentReminderTime ?? "20:30";

        var buttons = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData(
                alarmOn ? "🔔 Будильник: 🟢 ВКЛЮЧЁН" : "🔕 Будильник: 🔴 ВЫКЛЮЧЕН",
                "cmd_alarm_toggle_master") },
            new[]
            {
                InlineKeyboardButton.WithCallbackData($"📅 Расписание: {(schedOn ? schedTime : "ВЫКЛ")}", "cmd_alarm_sched"),
                InlineKeyboardButton.WithCallbackData($"📚 Домашка: {(hwOn ? hwTime : "ВЫКЛ")}", "cmd_alarm_hw")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData($"🎒 Вещи: {(equipOn ? equipTime : "ВЫКЛ")}", "cmd_alarm_equip"),
                InlineKeyboardButton.WithCallbackData("⚡ Проверить вещи", "cmd_alarm_check_now")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("⬅️ Настройки", "cmd_settings"),
                InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "cmd_back_main")
            }
        });

        string text = "⏰ *Умный будильник*\n\n" +
                      $"• 🔔 Статус: {(alarmOn ? "`🟢 Активен`" : "`🔴 Выключен`")}\n" +
                      $"• 📅 Расписание: {(schedOn ? $"`{schedTime}`" : "`Выкл`")}\n" +
                      $"• 📚 Домашка: {(hwOn ? $"`{hwTime}`" : "`Выкл`")}\n" +
                      $"• 🎒 Вещи и рюкзак: {(equipOn ? $"`{equipTime}`" : "`Выкл`")}";

        await SendOrEditBannerScreenAsync(chatId, msgId, "banner_alarm.png", text, buttons);
    }

    private async Task ToggleMasterAlarmAsync(long chatId, int msgId, string chatIdStr)
    {
        var user = await EnsureUserExistsAsync(chatIdStr);
        user.IsAlarmEnabled = !user.IsAlarmEnabled;
        await _db.SaveChangesAsync();
        await ShowAlarmMenuAsync(chatId, msgId, chatIdStr);
    }

    private async Task ShowDeleteAccountConfirmationAsync(long chatId, string chatIdStr, int? msgId)
    {
        string text = "⚠️ *Удаление аккаунта*\n\n" +
                      "Вы действительно хотите безвозвратно удалить свой аккаунт и все данные из MirageDiary?\n\n" +
                      "• Все сохранённые сессии и токены Госуслуг будут немедленно стёрты с сервера.\n" +
                      "• Все персональные настройки, напоминания и расписания будут удалены.\n" +
                      "• Это действие необратимо.";

        var markup = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("🗑 Да, удалить всё", "cmd_delete_account_confirm") },
            new[] { InlineKeyboardButton.WithCallbackData("❌ Отмена", "cmd_settings") }
        });

        await SafeEditTextOrCaptionAsync(chatId, msgId, text, markup);
    }

    private async Task ExecuteDeleteAccountAsync(long chatId, string chatIdStr, int? msgId)
    {
        try
        {
            await _tokenStore.RevokeTokensAsync(chatIdStr);
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == chatIdStr);
            if (user != null)
            {
                _db.Users.Remove(user);
                await _db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DeleteAccount] Error deleting user {chatIdStr}: {ex.Message}");
        }

        _stateService.ClearState(chatId);
        _currentBannerByChat.TryRemove(chatId, out _);

        string goodbye = "🗑 *Ваш аккаунт и все связанные персональные данные успешно удалены.*\n\n" +
                         "Все токены и сохранённые параметры стёрты с сервера.\n" +
                         "Если захотите вернуться, просто отправьте команду /start.";

        if (msgId.HasValue)
        {
            try { await _botClient.DeleteMessage(chatId, msgId.Value); } catch { }
        }

        await _botClient.SendMessage(
            chatId: chatId,
            text: goodbye,
            parseMode: ParseMode.Markdown,
            replyMarkup: new ReplyKeyboardRemove());
    }

    private async Task ShowLinkAccountAsync(long chatId, string chatIdStr, int? msgId = null)
    {
        var code = _userSyncService.GenerateLinkCode(chatIdStr);
        string text = "🔗 *Связка аккаунтов Telegram и MAX*\n\n" +
                      $"Ваш код для связки: `{code}` _(нажмите, чтобы скопировать)_\n\n" +
                      "📌 *Инструкция:*\n" +
                      "1️⃣ Откройте нашего бота в мессенджере **MAX**\n" +
                      $"2️⃣ Отправьте команду:\n`/link {code}`\n\n" +
                      "⏳ Код действует **15 минут**.\n" +
                      "После связки ваши токены Госуслуг, расписание, класс и настройки автоматически синхронизируются!";

        var markup = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("🔄 Обновить код", "cmd_link_account") },
            new[] { InlineKeyboardButton.WithCallbackData("🔙 Настройки", "cmd_settings") }
        });

        await SendOrEditBannerScreenAsync(chatId, msgId, "banner_settings.png", text, markup);
    }

    private async Task HandleLinkCommandAsync(long chatId, string chatIdStr, string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            await ShowLinkAccountAsync(chatId, chatIdStr, null);
            return;
        }

        string code = parts[1].Trim();
        var (success, message, peer) = await _userSyncService.TryLinkAccountsByCodeAsync(chatIdStr, code);
        if (success)
        {
            string peerInfo = peer != null && !string.IsNullOrEmpty(peer.FirstName) ? $" ({peer.FirstName})" : "";
            await _botClient.SendMessage(chatId,
                $"✅ *Аккаунты успешно связаны!*{peerInfo}\n\n" +
                $"Все токены, класс, расписание и настройки синхронизированы с вашим аккаунтом на другой платформе.",
                parseMode: ParseMode.Markdown);
        }
        else
        {
            await _botClient.SendMessage(chatId, $"❌ *Ошибка связки:* {message}", parseMode: ParseMode.Markdown);
        }
    }

    // ─── Schedule Reminder Menu ───────────────────────────────────────────────
    private async Task ShowScheduleReminderMenuAsync(long chatId, int msgId, string chatIdStr)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == chatIdStr);
        var enabled = user?.IsScheduleReminderEnabled ?? true;
        var time = user?.ScheduleReminderTime ?? "07:30";

        var buttons = new List<IEnumerable<InlineKeyboardButton>>
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    enabled ? "🔕 Выключить расписание" : "🔔 Включить расписание",
                    "cmd_alarm_sched_toggle")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("07:00", "cmd_alarm_sched_time:07:00"),
                InlineKeyboardButton.WithCallbackData("07:30", "cmd_alarm_sched_time:07:30"),
                InlineKeyboardButton.WithCallbackData("08:00", "cmd_alarm_sched_time:08:00"),
                InlineKeyboardButton.WithCallbackData("20:00", "cmd_alarm_sched_time:20:00")
            },
            new[] { InlineKeyboardButton.WithCallbackData("✏️ Ввести другое время", "cmd_alarm_sched_custom") },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("⬅️ К будильнику", "cmd_alarm_menu"),
                InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "cmd_back_main")
            }
        };

        string text = "📅 *Напоминание о расписании уроков*\n\n" +
                      $"• *Статус*: {(enabled ? "🟢 Включено" : "🔴 Выключено")}\n" +
                      $"• *Время отправки*: `{(enabled ? time : "выключено")}`\n\n" +
                      "_Каждое утро бот будет автоматически присылать вам список уроков на сегодня._\n\n" +
                      "Выберите время отправки ниже или нажмите «✏️ Ввести другое время»:";

        await SafeEditTextOrCaptionAsync(chatId, msgId, text, new InlineKeyboardMarkup(buttons));
    }

    private async Task ToggleScheduleReminderAsync(long chatId, int msgId, string chatIdStr)
    {
        var user = await EnsureUserExistsAsync(chatIdStr);
        user.IsScheduleReminderEnabled = !user.IsScheduleReminderEnabled;
        await _db.SaveChangesAsync();
        await ShowScheduleReminderMenuAsync(chatId, msgId, chatIdStr);
    }

    private async Task SetScheduleReminderTimeAsync(long chatId, int msgId, string chatIdStr, string time)
    {
        var user = await EnsureUserExistsAsync(chatIdStr);
        user.ScheduleReminderTime = time;
        user.IsScheduleReminderEnabled = true;
        await _db.SaveChangesAsync();
        await ShowScheduleReminderMenuAsync(chatId, msgId, chatIdStr);
    }

    // ─── Homework Reminder Menu ───────────────────────────────────────────────
    private async Task ShowHomeworkReminderMenuAsync(long chatId, int msgId, string chatIdStr)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == chatIdStr);
        var enabled = user?.IsHomeworkReminderEnabled ?? true;
        var time = user?.HomeworkReminderTime ?? "17:00";

        var buttons = new List<IEnumerable<InlineKeyboardButton>>
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    enabled ? "🔕 Выключить домашку" : "🔔 Включить домашку",
                    "cmd_alarm_hw_toggle")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("15:00", "cmd_alarm_hw_time:15:00"),
                InlineKeyboardButton.WithCallbackData("16:00", "cmd_alarm_hw_time:16:00"),
                InlineKeyboardButton.WithCallbackData("17:00", "cmd_alarm_hw_time:17:00"),
                InlineKeyboardButton.WithCallbackData("18:00", "cmd_alarm_hw_time:18:00"),
                InlineKeyboardButton.WithCallbackData("19:00", "cmd_alarm_hw_time:19:00")
            },
            new[] { InlineKeyboardButton.WithCallbackData("✏️ Ввести другое время", "cmd_alarm_hw_custom") },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("⬅️ К будильнику", "cmd_alarm_menu"),
                InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "cmd_back_main")
            }
        };

        string text = "📚 *Напоминание о домашнем задании*\n\n" +
                      $"• *Статус*: {(enabled ? "🟢 Включено" : "🔴 Выключено")}\n" +
                      $"• *Время отправки*: `{(enabled ? time : "выключено")}`\n\n" +
                      "_Каждый вечер бот будет присылать вам заданную на завтра домашку._\n\n" +
                      "Выберите удобное время ниже или нажмите «✏️ Ввести другое время»:";

        await SafeEditTextOrCaptionAsync(chatId, msgId, text, new InlineKeyboardMarkup(buttons));
    }

    private async Task ToggleHomeworkReminderAsync(long chatId, int msgId, string chatIdStr)
    {
        var user = await EnsureUserExistsAsync(chatIdStr);
        user.IsHomeworkReminderEnabled = !user.IsHomeworkReminderEnabled;
        await _db.SaveChangesAsync();
        await ShowHomeworkReminderMenuAsync(chatId, msgId, chatIdStr);
    }

    private async Task SetHomeworkReminderTimeAsync(long chatId, int msgId, string chatIdStr, string time)
    {
        var user = await EnsureUserExistsAsync(chatIdStr);
        user.HomeworkReminderTime = time;
        user.IsHomeworkReminderEnabled = true;
        await _db.SaveChangesAsync();
        await ShowHomeworkReminderMenuAsync(chatId, msgId, chatIdStr);
    }

    // ─── Equipment Reminder Menu ──────────────────────────────────────────────
    private async Task ShowEquipmentReminderMenuAsync(long chatId, int msgId, string chatIdStr)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == chatIdStr);
        var enabled = user?.IsEquipmentReminderEnabled ?? true;
        var time = user?.EquipmentReminderTime ?? "20:30";

        var buttons = new List<IEnumerable<InlineKeyboardButton>>
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    enabled ? "🔕 Выключить сбор рюкзака" : "🔔 Включить сбор рюкзака",
                    "cmd_alarm_equip_toggle")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("19:00", "cmd_alarm_equip_time:19:00"),
                InlineKeyboardButton.WithCallbackData("20:00", "cmd_alarm_equip_time:20:00"),
                InlineKeyboardButton.WithCallbackData("20:30", "cmd_alarm_equip_time:20:30"),
                InlineKeyboardButton.WithCallbackData("21:00", "cmd_alarm_equip_time:21:00"),
                InlineKeyboardButton.WithCallbackData("21:30", "cmd_alarm_equip_time:21:30")
            },
            new[] { InlineKeyboardButton.WithCallbackData("✏️ Ввести другое время", "cmd_alarm_equip_custom") },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("⬅️ К будильнику", "cmd_alarm_menu"),
                InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "cmd_back_main")
            }
        };

        string text = "🎒 *Умный будильник: сбор рюкзака и принадлежности*\n\n" +
                      $"• *Статус*: {(enabled ? "🟢 Включено" : "🔴 Выключено")}\n" +
                      $"• *Время отправки*: `{(enabled ? time : "выключено")}`\n\n" +
                      "_Бот сканирует уроки и домашку на завтра и напоминает взять: циркуль, линейку, форму, краски, атлас и др._\n\n" +
                      "Выберите время сбора рюкзака или нажмите «✏️ Ввести другое время»:";

        await SafeEditTextOrCaptionAsync(chatId, msgId, text, new InlineKeyboardMarkup(buttons));
    }

    private async Task ToggleEquipmentReminderAsync(long chatId, int msgId, string chatIdStr)
    {
        var user = await EnsureUserExistsAsync(chatIdStr);
        user.IsEquipmentReminderEnabled = !user.IsEquipmentReminderEnabled;
        await _db.SaveChangesAsync();
        await ShowEquipmentReminderMenuAsync(chatId, msgId, chatIdStr);
    }

    private async Task SetEquipmentReminderTimeAsync(long chatId, int msgId, string chatIdStr, string time)
    {
        var user = await EnsureUserExistsAsync(chatIdStr);
        user.EquipmentReminderTime = time;
        user.IsEquipmentReminderEnabled = true;
        await _db.SaveChangesAsync();
        await ShowEquipmentReminderMenuAsync(chatId, msgId, chatIdStr);
    }

    private async Task<UserEntity> EnsureUserExistsAsync(string chatIdStr, string? username = null, string? firstName = null, string? lastName = null)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == chatIdStr);
        bool isNew = false;
        if (user == null)
        {
            user = new UserEntity
            {
                Id = chatIdStr,
                AccessToken = "",
                RefreshToken = "",
                StudentId = "",
                ClassName = "",
                OrgName = "",
                Role = "Student",
                Platform = "telegram"
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
        if (user.Platform != "telegram")
        {
            user.Platform = "telegram";
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
        var match = System.Text.RegularExpressions.Regex.Match(clean, @"^([0-1]?[0-9]|2[0-3])[:\s\.-]([0-5][0-9])$");
        if (match.Success)
        {
            int h = int.Parse(match.Groups[1].Value);
            int m = int.Parse(match.Groups[2].Value);
            formattedTime = $"{h:D2}:{m:D2}";
            return true;
        }
        return false;
    }

    // ─── Admin Panel ──────────────────────────────────────────────────────────
    private async Task ShowAdminPanelAsync(long chatId, int msgId)
    {
        var chatIdStr = chatId.ToString();
        var role = await _roleService.GetUserRoleAsync(chatIdStr);
        if (role != "Admin") return;

        var isMaintenance = _maintenanceService.IsMaintenanceModeActive();
        var userCount = await _db.Users.CountAsync();
        var htCount   = await _db.Users.CountAsync(u => u.Role == "HeadTeacher");

        var adminKey = Environment.GetEnvironmentVariable("ADMIN_KEY") ?? _config["Security:AdminKey"] ?? "";
        var baseUrl = (_config["MiniAppUrl"] ?? "https://miragediary.space").TrimEnd('/');
        var adminWebUrl = $"{baseUrl}/admin?key={Uri.EscapeDataString(adminKey)}";

        var text = $"👑 *Админ-панель*\n\n" +
                   $"👥 Пользователей в базе: *{userCount}*\n" +
                   $"🏫 Завучей: *{htCount}*\n" +
                   $"🔧 Тех. работы: *{(isMaintenance ? "Включены" : "Выключены")}*\n\n" +
                   $"🔑 *Ключ администратора:*\n`{adminKey}`\n\n" +
                   $"_Нажмите кнопку ниже для мгновенного входа в веб-консоль:_";

        var buttons = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithUrl("🌐 Войти в админку на сайте", adminWebUrl) },
            new[] { InlineKeyboardButton.WithCallbackData("🏫 Управление завучами", "cmd_admin_headteachers") },
            new[] { InlineKeyboardButton.WithCallbackData(
                isMaintenance ? "🟢 Выключить тех. работы" : "🔧 Включить тех. работы",
                "cmd_admin_maintenance") },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("📢 Рассылка", "cmd_admin_broadcast"),
                InlineKeyboardButton.WithCallbackData("📊 Статистика", "cmd_admin_stats")
            },
            new[] { InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "cmd_back_main") }
        });

        await SendOrEditBannerScreenAsync(chatId, msgId, "banner_admin.png", text, buttons);
    }

    // ─── Head Teachers management ─────────────────────────────────────────────
    private async Task ShowHeadTeachersMenuAsync(long chatId, int msgId, string chatIdStr)
    {
        var role = await _roleService.GetUserRoleAsync(chatIdStr);
        if (role != "Admin") return;

        var headTeachers = await _db.Users
            .Where(u => u.Role == "HeadTeacher")
            .ToListAsync();

        var text = "🏫 *Управление завучами*\n\n";
        if (headTeachers.Count == 0)
            text += "Завучей пока нет.";
        else
            text += string.Join("\n", headTeachers.Select(ht =>
                $"• `{ht.Id}` — {(string.IsNullOrEmpty(ht.OrgName) ? "_без школы_" : ht.OrgName)}"));

        var buttons = new List<IEnumerable<InlineKeyboardButton>>
        {
            new[] { InlineKeyboardButton.WithCallbackData("➕ Добавить завуча", "cmd_admin_add_ht") }
        };

        // Remove buttons for each headteacher
        foreach (var ht in headTeachers)
        {
            var shortId = ht.Id.Length > 8 ? ht.Id[..8] + "…" : ht.Id;
            buttons.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    $"🗑 Удалить {shortId} ({ht.OrgName})",
                    $"cmd_admin_remove_ht:{ht.Id}")
            });
        }

        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("⬅️ Назад", "cmd_back_admin") });

        await SendOrEditBannerScreenAsync(chatId, msgId, "banner_admin.png", text, new InlineKeyboardMarkup(buttons));
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
        await _botClient.SendMessage(adminChatId,
            $"✅ Пользователь {displayTarget} назначен завучем школы *{orgName}*.\n\nВозвращайтесь в меню: /menu",
            parseMode: ParseMode.Markdown);
    }

    private async Task RemoveHeadTeacherAsync(long chatId, int msgId, string chatIdStr, string htId)
    {
        var role = await _roleService.GetUserRoleAsync(chatIdStr);
        if (role != "Admin") return;

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == htId);
        if (user != null)
        {
            user.Role = "Student";
            user.OrgName = "";
            await _db.SaveChangesAsync();
        }

        await ShowHeadTeachersMenuAsync(chatId, msgId, chatIdStr);
    }

    // ─── Maintenance ──────────────────────────────────────────────────────────
    private async Task ShowMaintenanceMenuAsync(long chatId, int msgId, string chatIdStr)
    {
        var role = await _roleService.GetUserRoleAsync(chatIdStr);
        if (role != "Admin") return;

        var isMaintenance = _maintenanceService.IsMaintenanceModeActive();
        var text = $"🔧 *Технические работы*\n\n" +
                   $"Текущий статус: *{(isMaintenance ? "Включены 🔴" : "Выключены 🟢")}*\n\n" +
                   "При включении пользователи будут получать сообщение о тех. работах вместо ответа бота.";

        var buttons = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🟢 Выключить", "cmd_admin_maintenance_off"),
                InlineKeyboardButton.WithCallbackData("🔴 Включить (30 мин)", "cmd_admin_maintenance_on")
            },
            new[] { InlineKeyboardButton.WithCallbackData("⬅️ Назад", "cmd_back_admin") }
        });

        await SendOrEditBannerScreenAsync(chatId, msgId, "banner_admin.png", text, buttons);
    }

    private async Task ToggleMaintenanceAsync(long chatId, int msgId, string chatIdStr, bool enable)
    {
        var role = await _roleService.GetUserRoleAsync(chatIdStr);
        if (role != "Admin") return;

        if (enable)
            _maintenanceService.ActivateMaintenanceMode(30);
        else
            _maintenanceService.DeactivateMaintenanceMode();

        await ShowAdminPanelAsync(chatId, msgId);
    }

    // ─── Stats ────────────────────────────────────────────────────────────────
    private async Task ShowStatsAsync(long chatId, int msgId, string chatIdStr)
    {
        var role = await _roleService.GetUserRoleAsync(chatIdStr);
        if (role != "Admin") return;

        var total   = await _db.Users.CountAsync();
        var auth    = await _db.Users.CountAsync(u => u.AccessToken != "");
        var hts     = await _db.Users.CountAsync(u => u.Role == "HeadTeacher");
        var alarmOn = await _db.Users.CountAsync(u => u.IsAlarmEnabled);
        var scheds  = await _db.Schedules.CountAsync();

        var text = $"📊 *Статистика*\n\n" +
                   $"👥 Всего пользователей: *{total}*\n" +
                   $"✅ Авторизованных: *{auth}*\n" +
                   $"🏫 Завучей: *{hts}*\n" +
                   $"🔔 С будильником: *{alarmOn}*\n" +
                   $"📅 Расписаний загружено: *{scheds}*";

        var buttons = new InlineKeyboardMarkup(
            InlineKeyboardButton.WithCallbackData("⬅️ Назад", "cmd_back_admin"));

        await SendOrEditBannerScreenAsync(chatId, msgId, "banner_admin.png", text, buttons);
    }

    // ─── Broadcast ────────────────────────────────────────────────────────────
    private async Task DoBroadcastAsync(long adminChatId, string broadcastText)
    {
        var users = await _db.Users.Where(u => u.AccessToken != "").ToListAsync();
        int sent = 0, failed = 0;

        await _botClient.SendMessage(adminChatId,
            $"📢 Начинаю рассылку для *{users.Count}* пользователей...",
            parseMode: ParseMode.Markdown);

        foreach (var user in users)
        {
            if (!long.TryParse(user.Id, out long uid)) continue;
            try
            {
                await _botClient.SendMessage(uid,
                    $"📢 *Сообщение от администратора:*\n\n{broadcastText}",
                    parseMode: ParseMode.Markdown);
                sent++;
                await Task.Delay(50); // respect Telegram rate limits
            }
            catch { failed++; }
        }

        await _botClient.SendMessage(adminChatId,
            $"✅ Рассылка завершена.\n✔️ Доставлено: *{sent}*\n❌ Ошибок: *{failed}*\n\nВозвращайтесь: /menu",
            parseMode: ParseMode.Markdown);
    }

    // =========================================================================
    // FEATURES
    // =========================================================================

    // ─── Homework ─────────────────────────────────────────────────────────────
    private async Task ExecuteHomeworkAsync(long chatId, string chatIdStr, string? dateArg = null, int? msgId = null, bool forceRefresh = false)
    {
        try
        {
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == chatIdStr);
            if (user == null || string.IsNullOrEmpty(user.AccessToken))
            {
                var errMsg = "❌ Вы не авторизованы. Войдите через Mini App.";
                await SafeEditTextOrCaptionAsync(chatId, msgId, errMsg, null);
                return;
            }

            var hw = await _diaryService.GetHomeworkStringAsync(chatIdStr, dateArg, forceRefresh);

            // Compute dates for navigation buttons
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

            var navMarkup = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("⬅️ Вчера", $"cmd_homework:{prevDate}"),
                    InlineKeyboardButton.WithCallbackData("Сегодня", $"cmd_homework:{todayStr}"),
                    InlineKeyboardButton.WithCallbackData("Завтра ➡️", $"cmd_homework:{nextDate}")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("🔄 Обновить", "cmd_homework_refresh"),
                    InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "cmd_back_main")
                }
            });

            string webAppUrl = _config["MiniAppUrl"] ?? "https://t.me";

            if (hw.StartsWith("🔒") || hw.Contains("Сессия истекла") || hw.Contains("недействительна") || hw.Contains("403 Forbidden"))
            {
                var reloginMarkup = new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithWebApp("🔐 Войти через Госуслуги", new WebAppInfo { Url = webAppUrl }) },
                    new[] { InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "cmd_back_main") }
                });
                await SafeEditTextOrCaptionAsync(chatId, msgId, hw, reloginMarkup);
                return;
            }

            await SendOrEditBannerScreenAsync(chatId, msgId, "banner_homework.png", hw, navMarkup);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BotCommandService Error] Failed to execute homework command for chatId {chatId}: {ex}");
            await _botClient.SendMessage(chatId, $"❌ Произошла ошибка при получении домашнего задания: {ex.Message}");
        }
    }

    // ─── Schedule ─────────────────────────────────────────────────────────────
    private async Task ExecuteScheduleAsync(long chatId, string chatIdStr, string? dateArg = null, int? msgId = null, bool forceRefresh = false)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == chatIdStr);
        var webAppUrl = _config["MiniAppUrl"] ?? "https://t.me";

        // 1. Prioritize HeadTeacher uploaded schedule file if available for user's school
        if (user != null && !string.IsNullOrEmpty(user.OrgName))
        {
            var schedule = await _db.Schedules.FirstOrDefaultAsync(s => s.OrgName == user.OrgName);
            if (schedule != null && !string.IsNullOrEmpty(schedule.FileId))
            {
                var uploadedNavButtons = new InlineKeyboardMarkup(new[]
                {
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("🔄 Обновить", "cmd_schedule_refresh"),
                        InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "cmd_back_main")
                    }
                });

                if (schedule.FileType == "photo")
                {
                    if (msgId.HasValue)
                    {
                        try
                        {
                            var inputMedia = new InputMediaPhoto(InputFile.FromFileId(schedule.FileId))
                            {
                                Caption = $"📅 Расписание — {user.OrgName}\n_(Загружено администрацией школы)_",
                                ParseMode = ParseMode.Markdown
                            };
                            await _botClient.EditMessageMedia(chatId, msgId.Value, inputMedia, replyMarkup: uploadedNavButtons);
                            _currentBannerByChat[chatId] = "school_schedule_photo";
                            return;
                        }
                        catch { }
                    }
                    await _botClient.SendPhoto(chatId, InputFile.FromFileId(schedule.FileId), caption: $"📅 Расписание — {user.OrgName}\n_(Загружено администрацией школы)_", replyMarkup: uploadedNavButtons);
                }
                else
                {
                    await _botClient.SendDocument(chatId, InputFile.FromFileId(schedule.FileId), caption: $"📅 Расписание — {user.OrgName}\n_(Загружено администрацией школы)_", replyMarkup: uploadedNavButtons);
                }
                return;
            }
        }

        if (user == null || string.IsNullOrEmpty(user.AccessToken))
        {
            var errMsg = "❌ Вы не авторизованы. Войдите через Mini App.";
            var reloginMarkup = new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithWebApp("🔐 Войти через Госуслуги", new WebAppInfo { Url = webAppUrl }) },
                new[] { InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "cmd_back_main") }
            });
            await SafeEditTextOrCaptionAsync(chatId, msgId, errMsg, reloginMarkup);
            return;
        }

        var navButtons = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🔄 Обновить", "cmd_schedule_refresh"),
                InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "cmd_back_main")
            }
        });

        var scheduleText = await _diaryService.GetScheduleStringAsync(chatIdStr, dateArg, forceRefresh);

        if (scheduleText.StartsWith("🔒") || scheduleText.Contains("Сессия истекла") || scheduleText.Contains("недействительна") || scheduleText.Contains("403 Forbidden"))
        {
            var reloginMarkup = new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithWebApp("🔐 Войти через Госуслуги", new WebAppInfo { Url = webAppUrl }) },
                new[] { InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "cmd_back_main") }
            });
            await SafeEditTextOrCaptionAsync(chatId, msgId, scheduleText, reloginMarkup);
            return;
        }

        if (string.IsNullOrWhiteSpace(scheduleText))
        {
            scheduleText = "📅 *Расписание уроков*\n\nНа выбранную дату расписание отсутствует или уроки не запланированы.";
        }

        await SendOrEditBannerScreenAsync(chatId, msgId, "banner_schedule.png", scheduleText, navButtons);
    }

    // ─── Marks ────────────────────────────────────────────────────────────────
    private async Task ExecuteMarksAsync(long chatId, string chatIdStr, int? msgId = null, bool forceRefresh = false)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == chatIdStr);
        var webAppUrl = _config["MiniAppUrl"] ?? "https://t.me";

        if (user == null || string.IsNullOrEmpty(user.AccessToken))
        {
            var errMsg = "❌ Вы не авторизованы. Войдите через Mini App.";
            var reloginMarkup = new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithWebApp("🔐 Войти через Госуслуги", new WebAppInfo { Url = webAppUrl }) },
                new[] { InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "cmd_back_main") }
            });
            if (msgId.HasValue)
                await SafeEditTextOrCaptionAsync(chatId, msgId.Value, errMsg, reloginMarkup);
            else
                await _botClient.SendMessage(chatId, errMsg, replyMarkup: reloginMarkup);
            return;
        }

        var marksText = await _diaryService.GetMarksStringAsync(chatIdStr, forceRefresh);

        if (marksText.StartsWith("🔒") || marksText.Contains("Сессия истекла") || marksText.Contains("недействительна") || marksText.Contains("403 Forbidden"))
        {
            var reloginMarkup = new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithWebApp("🔐 Войти через Госуслуги", new WebAppInfo { Url = webAppUrl }) },
                new[] { InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "cmd_back_main") }
            });
            if (msgId.HasValue)
                await SafeEditTextOrCaptionAsync(chatId, msgId.Value, marksText, reloginMarkup);
            else
                await _botClient.SendMessage(chatId, marksText, replyMarkup: reloginMarkup);
            return;
        }
        var markup = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🎯 Калькулятор целей", "cmd_calc"),
                InlineKeyboardButton.WithCallbackData("🔄 Обновить", "cmd_marks_refresh")
            },
            new[] { InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "cmd_back_main") }
        });

        await SendOrEditBannerScreenAsync(chatId, msgId, "banner_marks.png", marksText, markup);
    }

    // ─── Grade Calculator Screens ─────────────────────────────────────────────
    private async Task ShowGradeCalculatorSubjectsAsync(long chatId, string chatIdStr, int? msgId = null, bool forceRefresh = false)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == chatIdStr);
        if (user == null || string.IsNullOrEmpty(user.AccessToken))
        {
            var err = "❌ Вы не авторизованы. Войдите через Mini App.";
            await SafeEditTextOrCaptionAsync(chatId, msgId, err, null);
            return;
        }

        var subjects = await _diaryService.GetSubjectGradesSummaryAsync(chatIdStr, forceRefresh);
        if (subjects == null || subjects.Count == 0)
        {
            var noMarks = "📊 В дневнике пока нет выставленных оценок для расчета цели.";
            var backMarkup = new InlineKeyboardMarkup(new[] { InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "cmd_back_main") });
            await SafeEditTextOrCaptionAsync(chatId, msgId, noMarks, backMarkup);
            return;
        }

        var buttons = new List<List<InlineKeyboardButton>>();
        var row = new List<InlineKeyboardButton>();

        for (int i = 0; i < subjects.Count; i++)
        {
            var s = subjects[i];
            string gpaDisplay = s.TotalWeight > 0 ? $"{s.AverageGpa:F2}" : "—";
            string btnText = $"{s.StatusEmoji} {s.SubjectName} ({gpaDisplay})";
            row.Add(InlineKeyboardButton.WithCallbackData(btnText, $"cmd_calc_subj:{i}"));

            if (row.Count == 2)
            {
                buttons.Add(row);
                row = new List<InlineKeyboardButton>();
            }
        }
        if (row.Count > 0) buttons.Add(row);

        buttons.Add(new List<InlineKeyboardButton>
        {
            InlineKeyboardButton.WithCallbackData("🔄 Обновить", "cmd_calc_refresh"),
            InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "cmd_back_main")
        });

        string text = "🎯 *Калькулятор целей и среднего балла*\n\n" +
                      "Выберите предмет, чтобы рассчитать, сколько оценок нужно получить до желаемого балла в четверти:";

        var markup = new InlineKeyboardMarkup(buttons);
        await SafeEditTextOrCaptionAsync(chatId, msgId, text, markup);
    }

    private async Task ShowSubjectTargetOptionsAsync(long chatId, string chatIdStr, int msgId, int subjectIndex)
    {
        var subjects = await _diaryService.GetSubjectGradesSummaryAsync(chatIdStr);
        if (subjects == null || subjectIndex < 0 || subjectIndex >= subjects.Count)
        {
            await ShowGradeCalculatorSubjectsAsync(chatId, chatIdStr, msgId);
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

        var buttons = new List<IEnumerable<InlineKeyboardButton>>
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("⭐ Хочу 5 (≥ 4.60)", $"cmd_calc_target:{subjectIndex}:5"),
                InlineKeyboardButton.WithCallbackData("👍 Хочу 4 (≥ 3.60)", $"cmd_calc_target:{subjectIndex}:4")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("👌 Хочу 3 (≥ 2.60)", $"cmd_calc_target:{subjectIndex}:3"),
                InlineKeyboardButton.WithCallbackData("⬅️ К предметам", "cmd_calc")
            },
            new[] { InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "cmd_back_main") }
        };

        await SafeEditTextOrCaptionAsync(chatId, msgId, text, new InlineKeyboardMarkup(buttons), ParseMode.Markdown);
    }

    private async Task ShowTargetCalculationResultAsync(long chatId, string chatIdStr, int msgId, int subjectIndex, int targetGrade)
    {
        var subjects = await _diaryService.GetSubjectGradesSummaryAsync(chatIdStr);
        if (subjects == null || subjectIndex < 0 || subjectIndex >= subjects.Count)
        {
            await ShowGradeCalculatorSubjectsAsync(chatId, chatIdStr, msgId);
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
            // Calculate needed 5s (weight 1): (S + 5k) / (W + k) >= targetThreshold
            // k * (5 - targetThreshold) >= targetThreshold * W - S
            double diff = targetThreshold * W - S;
            int neededFivesW1 = (int)Math.Ceiling(diff / (5.0 - targetThreshold));
            if (neededFivesW1 < 1) neededFivesW1 = 1;

            // Calculate needed 5s (weight 2, control work): (S + 10k) / (W + 2k) >= targetThreshold
            // k * (10 - 2 * targetThreshold) >= targetThreshold * W - S
            int neededFivesW2 = (int)Math.Ceiling(diff / (10.0 - 2.0 * targetThreshold));
            if (neededFivesW2 < 1) neededFivesW2 = 1;

            // Calculate needed 4s (weight 1) if target <= 4: (S + 4k) / (W + k) >= targetThreshold
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

        var buttons = new List<IEnumerable<InlineKeyboardButton>>
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🎯 Другая цель", $"cmd_calc_subj:{subjectIndex}"),
                InlineKeyboardButton.WithCallbackData("⬅️ К предметам", "cmd_calc")
            },
            new[] { InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "cmd_back_main") }
        };

        await SafeEditTextOrCaptionAsync(chatId, msgId, text, new InlineKeyboardMarkup(buttons), ParseMode.Markdown);
    }

    // ─── Alarm check ──────────────────────────────────────────────────────────
    private async Task ExecuteAlarmCheckAsync(long chatId, string chatIdStr)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == chatIdStr);
        if (user == null || string.IsNullOrEmpty(user.AccessToken))
        {
            await _botClient.SendMessage(chatId, "❌ Вы не авторизованы. Войдите через Mini App.");
            return;
        }
        var hwString = await _diaryService.GetHomeworkStringAsync(chatIdStr, "завтра");
        var schedString = await _diaryService.GetScheduleStringAsync(chatIdStr, "завтра");
        var combined = (hwString + " " + schedString).ToLowerInvariant();

        var foundTriggers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in TriggerWords)
        {
            if (combined.Contains(w))
                foundTriggers.Add(w);
        }

        if (combined.Contains("физкультура") || combined.Contains("физра"))
            foundTriggers.Add("спортивная форма и сменная обувь");
        if (combined.Contains("изо") || combined.Contains("рисование"))
            foundTriggers.Add("альбом, краски и кисти");
        if (combined.Contains("геометрия"))
            foundTriggers.Add("линейка, циркуль и карандаш");
        if (combined.Contains("география"))
            foundTriggers.Add("атлас и контурные карты");
        if (combined.Contains("технология") || combined.Contains("труд"))
            foundTriggers.Add("принадлежности для урока труда");

        if (foundTriggers.Any())
        {
            var items = string.Join("\n• ", foundTriggers.Select(t => char.ToUpper(t[0]) + t[1..]));
            await _botClient.SendMessage(chatId,
                $"🎒 *Умный будильник: сбор рюкзака на завтра!*\n\nВам понадобятся:\n• {items}\n\nНе забудьте положить их в рюкзак прямо сейчас! 🎒",
                parseMode: ParseMode.Markdown);
        }
        else
        {
            await _botClient.SendMessage(chatId,
                "🎒 *Умный будильник*\n\nСпецинструментов на завтра не требуется. Проверьте обычные тетради и учебники по расписанию! ✅",
                parseMode: ParseMode.Markdown);
        }
    }

    // ─── Profile ──────────────────────────────────────────────────────────────
    private async Task ExecuteProfileAsync(long chatId, string chatIdStr, int? msgId = null, bool forceRefresh = false)
    {
        var text = await _diaryService.GetProfileStringAsync(chatIdStr, forceRefresh);
        var children = await _diaryService.GetChildrenListAsync(chatIdStr, forceRefresh);

        var buttons = new List<IEnumerable<InlineKeyboardButton>>
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🔄 Обновить", "cmd_profile_refresh"),
                InlineKeyboardButton.WithCallbackData("⚙️ Настройки", "cmd_settings")
            }
        };

        if (children != null && children.Count > 1)
        {
            buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("👨‍👩‍👧‍👦 Выбор ребенка", "cmd_children") });
        }

        buttons.Add(new[]
        {
            InlineKeyboardButton.WithCallbackData("🚪 Выйти", "cmd_logout"),
            InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "cmd_back_main")
        });

        var markup = new InlineKeyboardMarkup(buttons);

        await SendOrEditBannerScreenAsync(chatId, msgId, "banner_profile.png", text, markup);
    }

    private async Task ShowLogoutConfirmationAsync(long chatId, string chatIdStr, int? msgId = null)
    {
        var text = "🚪 *Выход из аккаунта*\n\n" +
                   "Вы действительно хотите выйти из своего аккаунта Госуслуг?\n" +
                   "Все сохранённые токены доступа будут удалены с сервера.";

        var markup = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🚪 Да, выйти", "cmd_confirm_logout"),
                InlineKeyboardButton.WithCallbackData("❌ Отмена", "cmd_profile")
            }
        });

        await SafeEditTextOrCaptionAsync(chatId, msgId, text, markup);
    }

    private async Task PerformLogoutAsync(long chatId, string chatIdStr, int? msgId = null)
    {
        try
        {
            await _tokenStore.RevokeTokensAsync(chatIdStr);
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == chatIdStr);
            if (user != null)
            {
                user.AccessToken = "";
                user.RefreshToken = "";
                user.StudentId = "";
                user.SelectedStudentId = "";
                await _db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Logout] Error for {chatIdStr}: {ex.Message}");
        }

        var text = "✅ *Вы успешно вышли из аккаунта.*\n\n" +
                   "Чтобы продолжить пользоваться ботом, авторизуйтесь снова через меню.";

        var markup = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "cmd_back_main") }
        });

        await SafeEditTextOrCaptionAsync(chatId, msgId, text, markup);
    }

    // ─── Children menu ────────────────────────────────────────────────────────
    private async Task ShowChildrenMenuAsync(long chatId, string chatIdStr, int? msgId = null)
    {
        var children = await _diaryService.GetChildrenListAsync(chatIdStr);
        if (children == null || children.Count == 0)
        {
            var msg = "❌ Не удалось получить список детей из аккаунта Госуслуг. Убедитесь, что вы залогинены.";
            if (msgId.HasValue)
                await SafeEditTextOrCaptionAsync(chatId, msgId.Value, msg, markup: null, parseMode: ParseMode.Markdown);
            else
                await _botClient.SendMessage(chatId, msg, parseMode: ParseMode.Markdown);
            return;
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == chatIdStr);
        string currentActiveId = user?.SelectedStudentId ?? (children.Count > 0 ? children[0].StudentId : "");

        string text = "👨‍👩‍👧‍👦 *Выбор ребенка (Родительский аккаунт)*\n\nВыберите активного ребенка для просмотра дневника и оценок:\n\n";

        var buttons = new List<IEnumerable<InlineKeyboardButton>>();

        foreach (var child in children)
        {
            string name = $"{child.StudentFirstName}".Trim();
            if (string.IsNullOrEmpty(name)) name = "Ученик";

            bool isActive = (child.StudentId == currentActiveId || child.MdmId == currentActiveId);
            string activeMark = isActive ? " ✅" : "";

            buttons.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData($"👶 {name}{activeMark}", $"cmd_select_child:{child.StudentId}")
            });

            text += $"• *{name}*{activeMark}\n";
        }

        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "cmd_back_main") });

        var markup = new InlineKeyboardMarkup(buttons);

        if (msgId.HasValue)
            await SafeEditTextOrCaptionAsync(chatId, msgId.Value, text, markup, ParseMode.Markdown);
        else
            await _botClient.SendMessage(chatId, text, parseMode: ParseMode.Markdown, replyMarkup: markup);
    }

    // =========================================================================
    // BELL SCHEDULE & PREMIUM SUBSCRIPTION
    // =========================================================================

    private async Task ExecuteNowAsync(long chatId, string chatIdStr, int? msgId = null)
    {
        var user = await _db.Users.FindAsync(chatIdStr);
        int shift = user?.Shift ?? 1;
        var localTime = DateTime.UtcNow.AddHours(user?.UtcOffsetHours ?? 5);

        var status = _bellSchedule.GetCurrentStatus(localTime, shift);

        string text = $"🔔 *Расписание звонков (МАОУ СОШ № 113)*\n\n" +
                      $"{status.StatusMessage}\n\n" +
                      $"🕐 *Точное время:* `{localTime:HH:mm:ss}` (Екатеринбург)\n" +
                      $"🎓 *Смена:* {shift}-я смена";

        var buttons = new List<IEnumerable<InlineKeyboardButton>>
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🔄 Обновить", "cmd_now"),
                InlineKeyboardButton.WithCallbackData(shift == 1 ? "Сменить на 2 смену" : "Сменить на 1 смену", shift == 1 ? "cmd_set_shift:2" : "cmd_set_shift:1")
            },
            new[] { InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "cmd_back_main") }
        };

        var markup = new InlineKeyboardMarkup(buttons);

        await SendOrEditBannerScreenAsync(chatId, msgId, "banner_now.png", text, markup);
    }

    private async Task ShowSubscriptionAsync(long chatId, int? msgId = null)
    {
        var user = await _db.Users.FindAsync(chatId.ToString());
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

        var buttons = new List<IEnumerable<InlineKeyboardButton>>();

        if (!hasPrem && !isAdmin)
        {
            buttons.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData("💳 Оформить подписку за 50 ₽ (Бета)", "cmd_sub_activate_beta")
            });
        }

        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "cmd_back_main") });

        var markup = new InlineKeyboardMarkup(buttons);

        await SendOrEditBannerScreenAsync(chatId, msgId, "banner_premium.png", text, markup);
    }

    private async Task ActivateBetaSubscriptionAsync(long chatId, string chatIdStr, int? msgId = null)
    {
        var user = await _db.Users.FindAsync(chatIdStr);
        if (user != null)
        {
            user.IsSubscribed = true;
            user.SubscriptionExpiresAtUtc = DateTime.UtcNow.AddMonths(1);
            await _db.SaveChangesAsync();
        }

        string text = "🎉 *Поздравляем с подключением MirageDiary Premium!*\n\n" +
                      "На время бета-тестирования вам начислен **1 месяц полного доступа** за 50 ₽.\n\n" +
                      "Все премиум-функции (Live-оценки по звонкам МАОУ СОШ № 113, умный будильник и приоритетный P2P-кэш) успешно активированы! 🚀\n\n" +
                      $"⭐️ *Срок действия:* до {DateTime.UtcNow.AddMonths(1):dd.MM.yyyy}";

        var buttons = new List<IEnumerable<InlineKeyboardButton>>
        {
            new[] { InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "cmd_back_main") }
        };

        var markup = new InlineKeyboardMarkup(buttons);

        await SendOrEditBannerScreenAsync(chatId, msgId, "banner_premium.png", text, markup);
    }

    private async Task ShowPrivacyPolicyOnboardingAsync(long chatId, int? msgId = null)
    {
        string text = "🛡 *Политика конфиденциальности и защита данных (152-ФЗ)*\n\n" +
                      "Добро пожаловать в *Mirage Diary*!\n" +
                      "Перед началом использования, пожалуйста, ознакомьтесь с условиями обработки персональных данных:\n\n" +
                      "1. 🔒 *Конвертное шифрование данных:* Все сессионные токены защищены Envelope Encryption (AES-256-GCM с уникальным 256-битным случайным ключом DEK на каждую запись).\n" +
                      "2. 🚫 *Без сохранения паролей:* Пароли от Госуслуг не передаются и не сохраняются на сервере — авторизация происходит напрямую через защищенный протокол ЕСИА.\n" +
                      "3. 👨‍👩‍👧 *Для учащихся и родителей:* Сервис предназначен для образовательных целей. Несовершеннолетние пользователи используют сервис с согласия родителей (законных представителей) согласно ч. 6 ст. 9 152-ФЗ.\n" +
                      "4. 🇷🇺 *Хранение в РФ:* База данных расположена на серверах в Российской Федерации в строгом соответствии с 152-ФЗ.\n" +
                      "5. 🗑 *Право на отзыв и забвение:* Вы можете в любой момент отозвать согласие и полностью удалить свой аккаунт со всеми токенами через команду /delete_account в настройках.\n\n" +
                      "Нажмите *«✅ Согласен с политикой (152-ФЗ)»*, чтобы начать пользоваться сервисом.";

        var markup = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithUrl("📖 Читать Политику (152-ФЗ)", "https://miragediary.space/privacy") },
            new[] { InlineKeyboardButton.WithCallbackData("✅ Согласен с политикой (152-ФЗ)", "cmd_accept_privacy") }
        });

        if (msgId.HasValue)
            await SafeEditTextOrCaptionAsync(chatId, msgId.Value, text, markup);
        else
            await _botClient.SendMessage(chatId, text, parseMode: ParseMode.Markdown, replyMarkup: markup);
    }

    private async Task<bool> EnsureChannelSubscribedAsync(long chatId, string chatIdStr, int? msgId = null)
    {
        var channel = _config["Telegram:RequiredChannel"] ?? "";
        if (string.IsNullOrWhiteSpace(channel))
            return true;

        var role = await _roleService.GetUserRoleAsync(chatIdStr);
        if (role == "Admin")
            return true;

        try
        {
            var member = await _botClient.GetChatMember(channel, chatId);
            if (member.Status is ChatMemberStatus.Member or ChatMemberStatus.Administrator or ChatMemberStatus.Creator or ChatMemberStatus.Restricted)
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ChannelCheck] Warning checking channel {channel} for {chatId}: {ex.Message}");
            return true; // Graceful degradation if channel is not accessible to bot yet
        }

        string cleanChannel = channel.TrimStart('@');
        string channelLink = _config["Telegram:ChannelLink"] ?? $"https://t.me/{cleanChannel}";

        string text = "📢 *Обязательная подписка на канал проекта*\n\n" +
                      $"Для использования бота *Mirage Diary*, пожалуйста, подпишитесь на наш официальный Telegram-канал: *{channel}*\n\n" +
                      "В канале публикуются:\n" +
                      "🔔 Изменения в школьном расписании и звонках\n" +
                      "🚀 Обновления бота и Mini App\n" +
                      "🛠 Статус доступности серверов и Госуслуг\n\n" +
                      "После подписки нажмите кнопку «Проверить подписку» ниже.";

        var markup = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithUrl("📢 Перейти в канал", channelLink) },
            new[] { InlineKeyboardButton.WithCallbackData("🔄 Проверить подписку", "cmd_check_channel_sub") }
        });

        if (msgId.HasValue)
            await SafeEditTextOrCaptionAsync(chatId, msgId.Value, text, markup);
        else
            await _botClient.SendMessage(chatId, text, parseMode: ParseMode.Markdown, replyMarkup: markup);

        return false;
    }

    private async Task SafeEditTextOrCaptionAsync(
        long chatId,
        int? msgId,
        string text,
        InlineKeyboardMarkup? markup = null,
        ParseMode parseMode = ParseMode.Markdown)
    {
        if (msgId.HasValue)
        {
            // 1. Try EditMessageText first
            try
            {
                await _botClient.EditMessageText(
                    chatId: chatId,
                    messageId: msgId.Value,
                    text: text,
                    parseMode: parseMode,
                    replyMarkup: markup);
                return;
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (ex.Message.Contains("message is not modified"))
            {
                return;
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (ex.Message.Contains("there is no text in the message to edit"))
            {
                // Message is a PHOTO message with a caption! Edit caption directly!
                try
                {
                    await _botClient.EditMessageCaption(
                        chatId: chatId,
                        messageId: msgId.Value,
                        caption: text,
                        parseMode: parseMode,
                        replyMarkup: markup);
                    return;
                }
                catch (Telegram.Bot.Exceptions.ApiRequestException capEx) when (capEx.Message.Contains("message is not modified"))
                {
                    return;
                }
                catch
                {
                    try { await _botClient.DeleteMessage(chatId, msgId.Value); } catch { }
                }
            }
            catch
            {
                try { await _botClient.DeleteMessage(chatId, msgId.Value); } catch { }
            }
        }

        // 2. Fallback: send fresh text message
        await _botClient.SendMessage(
            chatId: chatId,
            text: text,
            parseMode: parseMode,
            replyMarkup: markup);
    }

    private async Task SendOrEditBannerScreenAsync(
        long chatId,
        int? msgId,
        string bannerFileName,
        string caption,
        InlineKeyboardMarkup markup)
    {
        // ⚡ Fast-path: if current banner for this chat is already bannerFileName, just update caption & markup (10-30ms!)
        if (msgId.HasValue && _currentBannerByChat.TryGetValue(chatId, out var currentBanner) && currentBanner == bannerFileName)
        {
            try
            {
                await _botClient.EditMessageCaption(
                    chatId: chatId,
                    messageId: msgId.Value,
                    caption: caption,
                    parseMode: ParseMode.Markdown,
                    replyMarkup: markup);
                return;
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (ex.Message.Contains("message is not modified"))
            {
                return;
            }
            catch
            {
                // Fallback to media edit if caption editing failed
            }
        }

        var localPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "images", "banners", bannerFileName);
        if (!File.Exists(localPath))
        {
            localPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "images", "banners", bannerFileName);
        }

        InputFile mediaFile;
        if (_bannerFileIdCache.TryGetValue(bannerFileName, out var cachedFileId))
        {
            mediaFile = InputFile.FromFileId(cachedFileId);
        }
        else if (File.Exists(localPath))
        {
            mediaFile = InputFile.FromStream(new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read), bannerFileName);
        }
        else
        {
            var baseUrl = _config["MiniAppUrl"]?.TrimEnd('/') ?? "https://miragediary.space";
            mediaFile = InputFile.FromUri($"{baseUrl}/images/banners/{bannerFileName}");
        }

        if (msgId.HasValue)
        {
            try
            {
                var inputMedia = new InputMediaPhoto(mediaFile)
                {
                    Caption = caption,
                    ParseMode = ParseMode.Markdown
                };

                var editedMsg = await _botClient.EditMessageMedia(
                    chatId: chatId,
                    messageId: msgId.Value,
                    media: inputMedia,
                    replyMarkup: markup);

                _currentBannerByChat[chatId] = bannerFileName;

                if (editedMsg?.Photo?.Length > 0 && !_bannerFileIdCache.ContainsKey(bannerFileName))
                {
                    _bannerFileIdCache.TryAdd(bannerFileName, editedMsg.Photo.Last().FileId);
                }
                return;
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (ex.Message.Contains("message is not modified"))
            {
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BotBanner] EditMedia fallback ({bannerFileName}): {ex.Message}");
                try { await _botClient.DeleteMessage(chatId, msgId.Value); } catch { }
            }
        }

        try
        {
            InputFile freshFile;
            if (_bannerFileIdCache.TryGetValue(bannerFileName, out var fileId))
            {
                freshFile = InputFile.FromFileId(fileId);
            }
            else if (File.Exists(localPath))
            {
                freshFile = InputFile.FromStream(new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read), bannerFileName);
            }
            else
            {
                var baseUrl = _config["MiniAppUrl"]?.TrimEnd('/') ?? "https://miragediary.space";
                freshFile = InputFile.FromUri($"{baseUrl}/images/banners/{bannerFileName}");
            }

            var sentMsg = await _botClient.SendPhoto(
                chatId: chatId,
                photo: freshFile,
                caption: caption,
                parseMode: ParseMode.Markdown,
                replyMarkup: markup);

            _currentBannerByChat[chatId] = bannerFileName;

            if (sentMsg?.Photo?.Length > 0 && !_bannerFileIdCache.ContainsKey(bannerFileName))
            {
                _bannerFileIdCache.TryAdd(bannerFileName, sentMsg.Photo.Last().FileId);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BotBanner] SendPhoto error ({bannerFileName}): {ex.Message}");
            if (msgId.HasValue)
            {
                try
                {
                    await SafeEditTextOrCaptionAsync(chatId, msgId.Value, caption, markup);
                    return;
                }
                catch { }
            }
            await _botClient.SendMessage(chatId, caption, parseMode: ParseMode.Markdown, replyMarkup: markup);
        }
    }
}

