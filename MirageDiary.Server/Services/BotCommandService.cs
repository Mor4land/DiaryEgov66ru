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
using MirageDiary.Server.Security;

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
    private readonly DiaryBotWorkflowEngine _engine;

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
        MirageDiary.Core.ITokenStore tokenStore,
        DiaryBotWorkflowEngine engine)
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
        _engine = engine;
    }

    private InlineKeyboardMarkup ConvertToTelegramMarkup(List<List<BotButton>>? buttons)
    {
        if (buttons == null || buttons.Count == 0) return new InlineKeyboardMarkup(Array.Empty<InlineKeyboardButton[]>());

        var rows = new List<InlineKeyboardButton[]>();
        foreach (var row in buttons)
        {
            var rowButtons = new List<InlineKeyboardButton>();
            foreach (var btn in row)
            {
                if (!string.IsNullOrEmpty(btn.Url))
                {
                    rowButtons.Add(InlineKeyboardButton.WithUrl(btn.Text, btn.Url));
                }
                else if (!string.IsNullOrEmpty(btn.WebAppUrl))
                {
                    rowButtons.Add(InlineKeyboardButton.WithWebApp(btn.Text, new WebAppInfo { Url = btn.WebAppUrl }));
                }
                else
                {
                    rowButtons.Add(InlineKeyboardButton.WithCallbackData(btn.Text, btn.CallbackData ?? "noop"));
                }
            }
            rows.Add(rowButtons.ToArray());
        }
        return new InlineKeyboardMarkup(rows);
    }

    private async Task RenderScreenAsync(long chatId, int? msgId, BotScreen screen)
    {
        var markup = ConvertToTelegramMarkup(screen.Buttons);
        if (!string.IsNullOrEmpty(screen.BannerFileName))
        {
            await SendOrEditBannerScreenAsync(chatId, msgId, screen.BannerFileName, screen.Text, markup);
        }
        else
        {
            if (msgId.HasValue)
            {
                try
                {
                    await _botClient.EditMessageText(chatId, msgId.Value, screen.Text, ParseMode.Markdown, replyMarkup: markup);
                    return;
                }
                catch { }
            }
            await _botClient.SendMessage(chatId, screen.Text, ParseMode.Markdown, replyMarkup: markup);
        }
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

        // /start or /menu or cancel — always reset and show main menu
        if (text.StartsWith("/start") || text.StartsWith("/menu") || IsCancelCommand(text))
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
            await SafeSendMessageAsync(chatId, "⏳ _Защита от спама: подождите 2 секунды перед следующим запросом._");
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
            await SafeSendMessageAsync(chatId,
                "⭐ *MirageDiary*\n\n" +
                "Раздел платной подписки закрыт — все функции бота (расписание, оценки, калькулятор, домашка и уведомления) полностью бесплатны! 🚀");
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
            var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            string dateArg = parts.Length > 1 ? parts[1].Trim() : "";
            await ExecuteHomeworkAsync(chatId, chatIdStr, dateArg);
        }
        else if (text.StartsWith("/schedule"))
        {
            var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            string dateArg = parts.Length > 1 ? parts[1].Trim() : "";
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
        else if (text.StartsWith("/settings") || text.StartsWith("/настройки"))
        {
            await ShowSettingsMenuAsync(chatId, null, chatIdStr);
        }
        else if (text == "/" || text.StartsWith("/help") || text.StartsWith("/помощь") || text.StartsWith("/команды"))
        {
            await ShowHelpAsync(chatId, null);
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

        string safeOrg = TelegramMarkdownEscaper.Escape(user.OrgName);
        await SafeSendMessageAsync(chatId,
            $"✅ Расписание для школы *{safeOrg}* успешно сохранено!\n\n" +
            $"Теперь ученики смогут получить его через кнопку «📅 Расписание».");
    }

    // ─── State machine input ──────────────────────────────────────────────────
    private async Task HandleStateInputAsync(long chatId, string text, UserSession session)
    {
        switch (session.State)
        {
            case UserState.WaitingForHeadTeacherId:
                if (IsCancelCommand(text))
                {
                    _stateService.ClearState(chatId);
                    await SafeSendMessageAsync(chatId, "❌ Назначение завуча отменено.");
                    return;
                }
                _stateService.UpdateState(chatId, UserState.WaitingForHeadTeacherOrg, text);
                await SafeSendMessageAsync(chatId,
                    "🏫 Теперь введите название школы, к которой привязать этого завуча:\n_(или напишите «Отмена»)_");
                break;

            case UserState.WaitingForHeadTeacherOrg:
                if (IsCancelCommand(text))
                {
                    _stateService.ClearState(chatId);
                    await SafeSendMessageAsync(chatId, "❌ Назначение завуча отменено.");
                    return;
                }
                await SaveHeadTeacherAsync(chatId, session.TempData ?? "", text);
                _stateService.ClearState(chatId);
                break;

            case UserState.WaitingForBroadcastText:
                if (IsCancelCommand(text))
                {
                    _stateService.ClearState(chatId);
                    await SafeSendMessageAsync(chatId, "❌ Рассылка отменена.");
                    return;
                }
                await DoBroadcastAsync(chatId, text);
                _stateService.ClearState(chatId);
                break;

            case UserState.WaitingForScheduleReminderTime:
                if (IsCancelCommand(text))
                {
                    _stateService.ClearState(chatId);
                    await SafeSendMessageAsync(chatId, "❌ Ввод времени отменён.");
                    return;
                }
                if (TryParseTime(text, out var schedTime))
                {
                    var user = await EnsureUserExistsAsync(chatId.ToString());
                    user.ScheduleReminderTime = schedTime;
                    user.IsScheduleReminderEnabled = true;
                    await _db.SaveChangesAsync();
                    _stateService.ClearState(chatId);
                    await SafeSendMessageAsync(chatId, $"✅ Время напоминания о расписании установлено на *{schedTime}*!\n\nКаждое утро в это время вы будете получать список уроков.");
                }
                else
                {
                    await SafeSendMessageAsync(chatId, "❌ Неверный формат времени. Введите время в формате *ЧЧ:ММ* (например: `07:30` или `8:00`) или напишите «Отмена»:");
                }
                break;

            case UserState.WaitingForHomeworkReminderTime:
                if (IsCancelCommand(text))
                {
                    _stateService.ClearState(chatId);
                    await SafeSendMessageAsync(chatId, "❌ Ввод времени отменён.");
                    return;
                }
                if (TryParseTime(text, out var hwTime))
                {
                    var user = await EnsureUserExistsAsync(chatId.ToString());
                    user.HomeworkReminderTime = hwTime;
                    user.IsHomeworkReminderEnabled = true;
                    await _db.SaveChangesAsync();
                    _stateService.ClearState(chatId);
                    await SafeSendMessageAsync(chatId, $"✅ Время напоминания о домашке установлено на *{hwTime}*!\n\nКаждый день в это время бот пришлет список заданий на завтра.");
                }
                else
                {
                    await SafeSendMessageAsync(chatId, "❌ Неверный формат времени. Введите время в формате *ЧЧ:ММ* (например: `17:00` или `18:30`) или напишите «Отмена»:");
                }
                break;

            case UserState.WaitingForEquipmentReminderTime:
                if (IsCancelCommand(text))
                {
                    _stateService.ClearState(chatId);
                    await SafeSendMessageAsync(chatId, "❌ Ввод времени отменён.");
                    return;
                }
                if (TryParseTime(text, out var equipTime))
                {
                    var user = await EnsureUserExistsAsync(chatId.ToString());
                    user.EquipmentReminderTime = equipTime;
                    user.IsEquipmentReminderEnabled = true;
                    await _db.SaveChangesAsync();
                    _stateService.ClearState(chatId);
                    await SafeSendMessageAsync(chatId, $"✅ Время сбора рюкзака установлено на *{equipTime}*!\n\nВечером бот напомнит, какие вещи и инструменты положить в рюкзак.");
                }
                else
                {
                    await SafeSendMessageAsync(chatId, "❌ Неверный формат времени. Введите время в формате *ЧЧ:ММ* (например: `20:30` или `21:00`) или напишите «Отмена»:");
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

            case { } s when s.StartsWith("cmd_homework_refresh:"):
                var hwRefDate = s["cmd_homework_refresh:".Length..];
                await ExecuteHomeworkAsync(chatId.Value, chatIdStr, hwRefDate, msgId, forceRefresh: true);
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

            case { } s when s.StartsWith("cmd_schedule_refresh:"):
                var schedRefDate = s["cmd_schedule_refresh:".Length..];
                await ExecuteScheduleAsync(chatId.Value, chatIdStr, schedRefDate, msgId, forceRefresh: true);
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
            case "cmd_help":
                await ShowHelpAsync(chatId.Value, msgId);
                break;

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
        if (user == null || !user.HasAcceptedPrivacyPolicy)
        {
            var onboardingScreen = await _engine.BuildMainMenuScreenAsync(chatIdStr, "telegram");
            await RenderScreenAsync(chatId, msgId, onboardingScreen);
            return;
        }

        if (!await EnsureChannelSubscribedAsync(chatId, chatIdStr, msgId))
        {
            return;
        }

        var screen = await _engine.BuildMainMenuScreenAsync(chatIdStr, "telegram");
        await RenderScreenAsync(chatId, msgId, screen);
    }

    // ─── Settings & Account ───────────────────────────────────────────
    private async Task ShowSettingsMenuAsync(long chatId, int? msgId, string chatIdStr)
    {
        var screen = await _engine.BuildSettingsMenuScreenAsync(chatIdStr);
        await RenderScreenAsync(chatId, msgId, screen);
    }

    // ─── Help & Commands List ───────────────────────────────────────────
    private async Task ShowHelpAsync(long chatId, int? msgId = null)
    {
        var screen = _engine.BuildHelpScreen();
        await RenderScreenAsync(chatId, msgId, screen);
    }

    // ─── Dedicated Smart Alarm Menu ───────────────────────────────────────────
    private async Task ShowAlarmMenuAsync(long chatId, int msgId, string chatIdStr)
    {
        var screen = await _engine.BuildAlarmMenuScreenAsync(chatIdStr);
        await RenderScreenAsync(chatId, msgId, screen);
    }

    private async Task ToggleMasterAlarmAsync(long chatId, int msgId, string chatIdStr)
    {
        var res = await _engine.ToggleMasterAlarmAsync(chatIdStr);
        if (res.NextScreen != null)
        {
            await RenderScreenAsync(chatId, msgId, res.NextScreen);
        }
        else
        {
            await ShowAlarmMenuAsync(chatId, msgId, chatIdStr);
        }
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
        var screen = await _engine.ExecuteDeleteAccountAsync(chatIdStr);
        _stateService.ClearState(chatId);
        _currentBannerByChat.TryRemove(chatId, out _);

        if (msgId.HasValue)
        {
            try { await _botClient.DeleteMessage(chatId, msgId.Value); } catch { }
        }

        await SafeSendMessageAsync(
            chatId: chatId,
            text: screen.Text,
            markup: new ReplyKeyboardRemove());
    }

    private async Task ShowLinkAccountAsync(long chatId, string chatIdStr, int? msgId = null)
    {
        var screen = await _engine.BuildLinkAccountScreenAsync(chatIdStr);
        await RenderScreenAsync(chatId, msgId, screen);
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
            string peerInfo = peer != null && !string.IsNullOrEmpty(peer.FirstName) ? $" ({TelegramMarkdownEscaper.Escape(peer.FirstName)})" : "";
            await SafeSendMessageAsync(chatId,
                $"✅ *Аккаунты успешно связаны!*{peerInfo}\n\n" +
                $"Все токены, класс, расписание и настройки синхронизированы с вашим аккаунтом на другой платформе.");
        }
        else
        {
            string safeMsg = TelegramMarkdownEscaper.Escape(message);
            await SafeSendMessageAsync(chatId, $"❌ *Ошибка связки:* {safeMsg}");
        }
    }

    // ─── Schedule Reminder Menu ───────────────────────────────────────────────
    private async Task ShowScheduleReminderMenuAsync(long chatId, int msgId, string chatIdStr)
    {
        var screen = await _engine.BuildScheduleReminderMenuScreenAsync(chatIdStr);
        await RenderScreenAsync(chatId, msgId, screen);
    }

    private async Task ToggleScheduleReminderAsync(long chatId, int msgId, string chatIdStr)
    {
        await _engine.ToggleScheduleReminderAsync(chatIdStr);
        await ShowScheduleReminderMenuAsync(chatId, msgId, chatIdStr);
    }

    private async Task SetScheduleReminderTimeAsync(long chatId, int msgId, string chatIdStr, string time)
    {
        await _engine.SetScheduleReminderTimeAsync(chatIdStr, time);
        await ShowScheduleReminderMenuAsync(chatId, msgId, chatIdStr);
    }

    // ─── Homework Reminder Menu ───────────────────────────────────────────────
    private async Task ShowHomeworkReminderMenuAsync(long chatId, int msgId, string chatIdStr)
    {
        var screen = await _engine.BuildHomeworkReminderMenuScreenAsync(chatIdStr);
        await RenderScreenAsync(chatId, msgId, screen);
    }

    private async Task ToggleHomeworkReminderAsync(long chatId, int msgId, string chatIdStr)
    {
        await _engine.ToggleHomeworkReminderAsync(chatIdStr);
        await ShowHomeworkReminderMenuAsync(chatId, msgId, chatIdStr);
    }

    private async Task SetHomeworkReminderTimeAsync(long chatId, int msgId, string chatIdStr, string time)
    {
        await _engine.SetHomeworkReminderTimeAsync(chatIdStr, time);
        await ShowHomeworkReminderMenuAsync(chatId, msgId, chatIdStr);
    }

    // ─── Equipment Reminder Menu ──────────────────────────────────────────────
    private async Task ShowEquipmentReminderMenuAsync(long chatId, int msgId, string chatIdStr)
    {
        var screen = await _engine.BuildEquipmentReminderMenuScreenAsync(chatIdStr);
        await RenderScreenAsync(chatId, msgId, screen);
    }

    private async Task ToggleEquipmentReminderAsync(long chatId, int msgId, string chatIdStr)
    {
        await _engine.ToggleEquipmentReminderAsync(chatIdStr);
        await ShowEquipmentReminderMenuAsync(chatId, msgId, chatIdStr);
    }

    private async Task SetEquipmentReminderTimeAsync(long chatId, int msgId, string chatIdStr, string time)
    {
        await _engine.SetEquipmentReminderTimeAsync(chatIdStr, time);
        await ShowEquipmentReminderMenuAsync(chatId, msgId, chatIdStr);
    }

    private async Task<UserEntity> EnsureUserExistsAsync(string chatIdStr, string? username = null, string? firstName = null, string? lastName = null)
    {
        return await _engine.EnsureUserExistsAsync(chatIdStr, username, firstName, lastName, "telegram");
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

        string displayTarget = !string.IsNullOrEmpty(user.Username) ? $"@{TelegramMarkdownEscaper.Escape(user.Username)} (`{user.Id}`)" : $"`{user.Id}`";
        string safeOrg = TelegramMarkdownEscaper.Escape(orgName);
        await SafeSendMessageAsync(adminChatId,
            $"✅ Пользователь {displayTarget} назначен завучем школы *{safeOrg}*.\n\nВозвращайтесь в меню: /menu");
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

        await SafeSendMessageAsync(adminChatId,
            $"📢 Начинаю рассылку для *{users.Count}* пользователей...");

        foreach (var user in users)
        {
            if (!long.TryParse(user.Id, out long uid)) continue;
            try
            {
                await SafeSendMessageAsync(uid,
                    $"📢 *Сообщение от администратора:*\n\n{broadcastText}");
                sent++;
                await Task.Delay(50); // respect Telegram rate limits
            }
            catch { failed++; }
        }

        await SafeSendMessageAsync(adminChatId,
            $"✅ Рассылка завершена.\n✔️ Доставлено: *{sent}*\n❌ Ошибок: *{failed}*\n\nВозвращайтесь: /menu");
    }

    // =========================================================================
    // FEATURES
    // =========================================================================

    // ─── Homework ─────────────────────────────────────────────────────────────
    private async Task ExecuteHomeworkAsync(long chatId, string chatIdStr, string? dateArg = null, int? msgId = null, bool forceRefresh = false)
    {
        var screen = await _engine.BuildHomeworkScreenAsync(chatIdStr, dateArg, forceRefresh);
        await RenderScreenAsync(chatId, msgId, screen);
    }

    // ─── Schedule ─────────────────────────────────────────────────────────────
    private async Task ExecuteScheduleAsync(long chatId, string chatIdStr, string? dateArg = null, int? msgId = null, bool forceRefresh = false)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == chatIdStr);
        if ((user == null || string.IsNullOrEmpty(user.AccessToken)) && !string.IsNullOrEmpty(user?.OrgName))
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

                string safeOrg = TelegramMarkdownEscaper.Escape(user.OrgName);
                string scheduleCaption = $"📅 Расписание — {safeOrg}\n_(Загружено администрацией школы)_";

                if (schedule.FileType == "photo")
                {
                    if (msgId.HasValue)
                    {
                        try
                        {
                            var inputMedia = new InputMediaPhoto(InputFile.FromFileId(schedule.FileId))
                            {
                                Caption = scheduleCaption,
                                ParseMode = ParseMode.Markdown
                            };
                            await _botClient.EditMessageMedia(chatId, msgId.Value, inputMedia, replyMarkup: uploadedNavButtons);
                            _currentBannerByChat[chatId] = "school_schedule_photo";
                            return;
                        }
                        catch { }
                    }
                    await _botClient.SendPhoto(chatId, InputFile.FromFileId(schedule.FileId), caption: scheduleCaption, parseMode: ParseMode.Markdown, replyMarkup: uploadedNavButtons);
                }
                else
                {
                    await _botClient.SendDocument(chatId, InputFile.FromFileId(schedule.FileId), caption: scheduleCaption, parseMode: ParseMode.Markdown, replyMarkup: uploadedNavButtons);
                }
                return;
            }
        }

        var screen = await _engine.BuildScheduleScreenAsync(chatIdStr, dateArg, forceRefresh);
        await RenderScreenAsync(chatId, msgId, screen);
    }

    // ─── Marks ────────────────────────────────────────────────────────────────
    private async Task ExecuteMarksAsync(long chatId, string chatIdStr, int? msgId = null, bool forceRefresh = false)
    {
        var screen = await _engine.BuildMarksScreenAsync(chatIdStr, forceRefresh);
        await RenderScreenAsync(chatId, msgId, screen);
    }

    // ─── Grade Calculator Screens ─────────────────────────────────────────────
    private async Task ShowGradeCalculatorSubjectsAsync(long chatId, string chatIdStr, int? msgId = null, bool forceRefresh = false)
    {
        var screen = await _engine.BuildGradeCalculatorSubjectsScreenAsync(chatIdStr, forceRefresh);
        await RenderScreenAsync(chatId, msgId, screen);
    }

    private async Task ShowSubjectTargetOptionsAsync(long chatId, string chatIdStr, int msgId, int subjectIndex)
    {
        var screen = await _engine.BuildSubjectTargetOptionsScreenAsync(chatIdStr, subjectIndex);
        await RenderScreenAsync(chatId, msgId, screen);
    }

    private async Task ShowTargetCalculationResultAsync(long chatId, string chatIdStr, int msgId, int subjectIndex, int targetGrade)
    {
        var screen = await _engine.BuildTargetCalculationResultScreenAsync(chatIdStr, subjectIndex, targetGrade);
        await RenderScreenAsync(chatId, msgId, screen);
    }

    // ─── Alarm check ──────────────────────────────────────────────────────────
    private async Task ExecuteAlarmCheckAsync(long chatId, string chatIdStr)
    {
        var screen = await _engine.BuildAlarmCheckScreenAsync(chatIdStr);
        await RenderScreenAsync(chatId, null, screen);
    }

    // ─── Profile ──────────────────────────────────────────────────────────────
    private async Task ExecuteProfileAsync(long chatId, string chatIdStr, int? msgId = null, bool forceRefresh = false)
    {
        var screen = await _engine.BuildProfileScreenAsync(chatIdStr, forceRefresh);
        await RenderScreenAsync(chatId, msgId, screen);
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
        var screen = await _engine.PerformLogoutAsync(chatIdStr);
        await RenderScreenAsync(chatId, msgId, screen);
    }

    // ─── Children menu ────────────────────────────────────────────────────────
    private async Task ShowChildrenMenuAsync(long chatId, string chatIdStr, int? msgId = null)
    {
        var screen = await _engine.BuildChildrenMenuScreenAsync(chatIdStr);
        await RenderScreenAsync(chatId, msgId, screen);
    }

    // =========================================================================
    // BELL SCHEDULE & PREMIUM SUBSCRIPTION
    // =========================================================================

    private async Task ExecuteNowAsync(long chatId, string chatIdStr, int? msgId = null)
    {
        var screen = await _engine.BuildNowScreenAsync(chatIdStr);
        await RenderScreenAsync(chatId, msgId, screen);
    }

    private async Task ShowSubscriptionAsync(long chatId, int? msgId = null)
    {
        var screen = await _engine.BuildSubscriptionScreenAsync(chatId.ToString());
        await RenderScreenAsync(chatId, msgId, screen);
    }

    private async Task ActivateBetaSubscriptionAsync(long chatId, string chatIdStr, int? msgId = null)
    {
        var res = await _engine.ActivateBetaSubscriptionAsync(chatIdStr);
        if (res.NextScreen != null)
        {
            await RenderScreenAsync(chatId, msgId, res.NextScreen);
        }
        else
        {
            await ShowSubscriptionAsync(chatId, msgId);
        }
    }

    private async Task ShowPrivacyPolicyOnboardingAsync(long chatId, int? msgId = null)
    {
        var screen = _engine.BuildPrivacyPolicyOnboardingScreen();
        await RenderScreenAsync(chatId, msgId, screen);
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
        string safeChannel = TelegramMarkdownEscaper.Escape(channel);

        string text = "📢 *Обязательная подписка на канал проекта*\n\n" +
                      $"Для использования бота *Mirage Diary*, пожалуйста, подпишитесь на наш официальный Telegram-канал: *{safeChannel}*\n\n" +
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
            await SafeSendMessageAsync(chatId, text, markup);

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
            catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (ex.Message.Contains("can't parse entities"))
            {
                try
                {
                    await _botClient.EditMessageText(
                        chatId: chatId,
                        messageId: msgId.Value,
                        text: text,
                        replyMarkup: markup);
                    return;
                }
                catch { }
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
                catch (Telegram.Bot.Exceptions.ApiRequestException capEx) when (capEx.Message.Contains("can't parse entities"))
                {
                    try
                    {
                        await _botClient.EditMessageCaption(
                            chatId: chatId,
                            messageId: msgId.Value,
                            caption: text,
                            replyMarkup: markup);
                        return;
                    }
                    catch { }
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
        await SafeSendMessageAsync(chatId, text, markup, parseMode);
    }

    private async Task<Message?> SafeSendMessageAsync(
        long chatId,
        string text,
        ReplyMarkup? markup = null,
        ParseMode parseMode = ParseMode.Markdown,
        CancellationToken ct = default)
    {
        try
        {
            return await _botClient.SendMessage(
                chatId: chatId,
                text: text,
                parseMode: parseMode,
                replyMarkup: markup,
                cancellationToken: ct);
        }
        catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (ex.Message.Contains("can't parse entities"))
        {
            return await _botClient.SendMessage(
                chatId: chatId,
                text: text,
                replyMarkup: markup,
                cancellationToken: ct);
        }
    }

    private static bool IsCancelCommand(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();
        return trimmed.Equals("отмена", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("cancel", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("/cancel", StringComparison.OrdinalIgnoreCase);
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

            Message sentMsg;
            try
            {
                sentMsg = await _botClient.SendPhoto(
                    chatId: chatId,
                    photo: freshFile,
                    caption: caption,
                    parseMode: ParseMode.Markdown,
                    replyMarkup: markup);
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (ex.Message.Contains("can't parse entities"))
            {
                sentMsg = await _botClient.SendPhoto(
                    chatId: chatId,
                    photo: freshFile,
                    caption: caption,
                    replyMarkup: markup);
            }

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
            await SafeSendMessageAsync(chatId, caption, markup);
        }
    }
}

