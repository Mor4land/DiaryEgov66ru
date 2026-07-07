using System;
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
    private readonly ITelegramBotClient _botClient;
    private readonly DiaryInteractionService _diaryService;
    private readonly Microsoft.Extensions.Configuration.IConfiguration _config;
    private readonly RoleService _roleService;
    private readonly BotStateService _stateService;
    private readonly MaintenanceModeService _maintenanceService;
    private readonly AppDbContext _db;

    // ─── Trigger words for SmartAlarm ────────────────────────────────────────
    private static readonly string[] TriggerWords =
    {
        "циркуль", "линейка", "краски", "альбом", "форма",
        "транспортир", "контурные карты", "кисти", "карандаши", "тетрадь"
    };

    public BotCommandService(
        ITelegramBotClient botClient,
        DiaryInteractionService diaryService,
        Microsoft.Extensions.Configuration.IConfiguration config,
        RoleService roleService,
        BotStateService stateService,
        MaintenanceModeService maintenanceService,
        AppDbContext db)
    {
        _botClient = botClient;
        _diaryService = diaryService;
        _config = config;
        _roleService = roleService;
        _stateService = stateService;
        _maintenanceService = maintenanceService;
        _db = db;
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

    private async Task HandleMessageAsync(Message message)
    {
        var chatId = message.Chat.Id;
        var chatIdStr = chatId.ToString();
        var text = message.Text?.Trim() ?? "";

        // /start or /menu — always reset and show main menu
        if (text.StartsWith("/start") || text.StartsWith("/menu") || text == "Отмена")
        {
            _stateService.ClearState(chatId);
            await ShowMainMenuAsync(chatId);
            return;
        }

        // Check maintenance mode (except for admins)
        if (_maintenanceService.IsMaintenanceModeActive())
        {
            var role = await _roleService.GetUserRoleAsync(chatIdStr);
            if (role != "Admin")
            {
                await _botClient.SendMessage(chatId,
                    "🔧 *Бот находится на техническом обслуживании.*\nПожалуйста, попробуйте позже.",
                    parseMode: ParseMode.Markdown);
                return;
            }
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
        if (text.StartsWith("/alarm"))   await ExecuteAlarmCheckAsync(chatId, chatIdStr);
        else if (text.StartsWith("/homework")) await ExecuteHomeworkAsync(chatId, chatIdStr);
        else if (text.StartsWith("/schedule")) await ExecuteScheduleAsync(chatId, chatIdStr);
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

        await _botClient.AnswerCallbackQuery(cq.Id);

        // Don't clear state if we're in a wizard flow triggered by admin panel
        if (!data.StartsWith("cmd_admin_"))
            _stateService.ClearState(chatId.Value);

        switch (data)
        {
            // ── Main menu ──────────────────────────────────────────────────
            case "cmd_back_main":
                await ShowMainMenuAsync(chatId.Value, msgId);
                break;

            // ── Homework ───────────────────────────────────────────────────
            case "cmd_homework":
                await ExecuteHomeworkAsync(chatId.Value, chatIdStr);
                break;

            // ── Schedule ───────────────────────────────────────────────────
            case "cmd_schedule":
                await ExecuteScheduleAsync(chatId.Value, chatIdStr);
                break;

            // ── Settings ───────────────────────────────────────────────────
            case "cmd_settings":
                await ShowSettingsMenuAsync(chatId.Value, msgId, chatIdStr);
                break;

            case "cmd_toggle_alarm":
                await ToggleAlarmAsync(chatId.Value, msgId, chatIdStr);
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
                await _botClient.EditMessageText(chatId.Value, msgId,
                    "👤 *Добавление завуча*\n\nВведите Telegram ID пользователя:\n_(или напишите «Отмена»)_",
                    parseMode: ParseMode.Markdown,
                    replyMarkup: new InlineKeyboardMarkup(
                        InlineKeyboardButton.WithCallbackData("❌ Отмена", "cmd_admin_headteachers")));
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
                    await _botClient.EditMessageText(chatId.Value, msgId,
                        "📢 *Рассылка*\n\nВведите текст сообщения для всех пользователей бота:\n_(или напишите «Отмена»)_",
                        parseMode: ParseMode.Markdown,
                        replyMarkup: new InlineKeyboardMarkup(
                            InlineKeyboardButton.WithCallbackData("❌ Отмена", "cmd_admin_panel")));
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
        var role = await _roleService.GetUserRoleAsync(chatIdStr);
        var isMaintenance = _maintenanceService.IsMaintenanceModeActive();
        var webAppUrl = _config["MiniAppUrl"] ?? "https://t.me";

        var buttons = new List<IEnumerable<InlineKeyboardButton>>
        {
            new[] { InlineKeyboardButton.WithWebApp("📱 Открыть Дневник", new WebAppInfo { Url = webAppUrl }) },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("📚 Домашка", "cmd_homework"),
                InlineKeyboardButton.WithCallbackData("📅 Расписание", "cmd_schedule")
            },
            new[] { InlineKeyboardButton.WithCallbackData("⚙️ Настройки", "cmd_settings") }
        };

        if (role == "Admin")
            buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("👑 Админ-панель", "cmd_admin_panel") });

        var maintenanceBanner = isMaintenance ? "\n\n🔧 _Идут технические работы_" : "";

        var roleEmoji = role switch
        {
            "Admin"       => "👑 Администратор",
            "HeadTeacher" => "🏫 Завуч",
            _             => "🎓 Ученик"
        };

        var text = $"*Mirage Diary* — школьный дневник\n" +
                   $"Роль: {roleEmoji}{maintenanceBanner}\n\n" +
                   (role == "HeadTeacher"
                       ? "💡 Отправьте мне фото или PDF с расписанием — я сохраню его для учеников вашей школы."
                       : "Выберите действие:");

        if (msgId.HasValue)
            await _botClient.EditMessageText(chatId, msgId.Value, text,
                parseMode: ParseMode.Markdown,
                replyMarkup: new InlineKeyboardMarkup(buttons));
        else
            await _botClient.SendMessage(chatId, text,
                parseMode: ParseMode.Markdown,
                replyMarkup: new InlineKeyboardMarkup(buttons));
    }

    // ─── Settings ─────────────────────────────────────────────────────────────
    private async Task ShowSettingsMenuAsync(long chatId, int msgId, string chatIdStr)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == chatIdStr);
        var alarmOn = user?.IsAlarmEnabled ?? true;

        var buttons = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData(
                alarmOn ? "🔔 Умный будильник: ВКЛ" : "🔕 Умный будильник: ВЫКЛ",
                "cmd_toggle_alarm") },
            new[] { InlineKeyboardButton.WithCallbackData("⬅️ Назад", "cmd_back_main") }
        });

        try
        {
            await _botClient.EditMessageText(chatId, msgId,
                "⚙️ *Настройки*\n\n" +
                "🔔 *Умный будильник* — бот напомнит взять нужные вещи, если они упомянуты в домашнем задании (циркуль, линейка и др.).\n\n" +
                $"Статус: {(alarmOn ? "Включён ✅" : "Выключен ❌")}",
                parseMode: ParseMode.Markdown,
                replyMarkup: buttons);
        }
        catch (Telegram.Bot.Exceptions.ApiRequestException ex)
            when (ex.Message.Contains("message is not modified")) { /* ignore double-tap */ }
    }

    private async Task ToggleAlarmAsync(long chatId, int msgId, string chatIdStr)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == chatIdStr);
        if (user == null)
        {
            // User not in DB yet — create a stub with alarm explicitly disabled
            user = new UserEntity
            {
                Id = chatIdStr, AccessToken = "", RefreshToken = "",
                StudentId = "", ClassName = "", OrgName = "",
                Role = "Student", IsAlarmEnabled = false
            };
            _db.Users.Add(user);
        }
        else
        {
            user.IsAlarmEnabled = !user.IsAlarmEnabled;
        }
        await _db.SaveChangesAsync();
        await ShowSettingsMenuAsync(chatId, msgId, chatIdStr);
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

        var text = $"👑 *Админ-панель*\n\n" +
                   $"👥 Пользователей в базе: *{userCount}*\n" +
                   $"🏫 Завучей: *{htCount}*\n" +
                   $"🔧 Тех. работы: *{(isMaintenance ? "Включены" : "Выключены")}*";

        var buttons = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("🏫 Управление завучами", "cmd_admin_headteachers") },
            new[] { InlineKeyboardButton.WithCallbackData(
                isMaintenance ? "🟢 Выключить тех. работы" : "🔧 Включить тех. работы",
                "cmd_admin_maintenance") },
            new[] { InlineKeyboardButton.WithCallbackData("📢 Рассылка", "cmd_admin_broadcast") },
            new[] { InlineKeyboardButton.WithCallbackData("📊 Статистика", "cmd_admin_stats") },
            new[] { InlineKeyboardButton.WithCallbackData("⬅️ Назад", "cmd_back_main") }
        });

        await _botClient.EditMessageText(chatId, msgId, text,
            parseMode: ParseMode.Markdown,
            replyMarkup: buttons);
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

        await _botClient.EditMessageText(chatId, msgId, text,
            parseMode: ParseMode.Markdown,
            replyMarkup: new InlineKeyboardMarkup(buttons));
    }

    private async Task SaveHeadTeacherAsync(long adminChatId, string targetId, string orgName)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == targetId);
        if (user == null)
        {
            user = new UserEntity
            {
                Id = targetId, Role = "HeadTeacher", OrgName = orgName,
                AccessToken = "", RefreshToken = "", StudentId = "", ClassName = ""
            };
            _db.Users.Add(user);
        }
        else
        {
            user.Role = "HeadTeacher";
            user.OrgName = orgName;
        }
        await _db.SaveChangesAsync();

        await _botClient.SendMessage(adminChatId,
            $"✅ Пользователь `{targetId}` назначен завучем школы *{orgName}*.\n\nВозвращайтесь в меню: /menu",
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

        await _botClient.EditMessageText(chatId, msgId, text,
            parseMode: ParseMode.Markdown,
            replyMarkup: buttons);
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

        await _botClient.EditMessageText(chatId, msgId, text,
            parseMode: ParseMode.Markdown,
            replyMarkup: buttons);
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
    private async Task ExecuteHomeworkAsync(long chatId, string chatIdStr)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == chatIdStr);
        if (user == null || string.IsNullOrEmpty(user.AccessToken))
        {
            await _botClient.SendMessage(chatId, "❌ Вы не авторизованы. Войдите через Mini App.");
            return;
        }
        await _botClient.SendMessage(chatId, "⏳ Загружаю домашнее задание...");
        var hw = await _diaryService.GetHomeworkStringAsync(chatIdStr);
        await _botClient.SendMessage(chatId, hw);
    }

    // ─── Schedule ─────────────────────────────────────────────────────────────
    private async Task ExecuteScheduleAsync(long chatId, string chatIdStr)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == chatIdStr);
        if (user == null || string.IsNullOrEmpty(user.OrgName))
        {
            await _botClient.SendMessage(chatId, "❌ Вы не авторизованы или ваша школа неизвестна.");
            return;
        }

        var schedule = await _db.Schedules.FirstOrDefaultAsync(s => s.OrgName == user.OrgName);
        if (schedule == null || string.IsNullOrEmpty(schedule.FileId))
        {
            await _botClient.SendMessage(chatId,
                $"📭 Расписание для школы *{user.OrgName}* ещё не загружено завучем.",
                parseMode: ParseMode.Markdown);
            return;
        }

        if (schedule.FileType == "photo")
            await _botClient.SendPhoto(chatId, InputFile.FromFileId(schedule.FileId),
                caption: $"📅 Расписание — {user.OrgName}");
        else
            await _botClient.SendDocument(chatId, InputFile.FromFileId(schedule.FileId),
                caption: $"📅 Расписание — {user.OrgName}");
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
        if (!user.IsAlarmEnabled)
        {
            await _botClient.SendMessage(chatId, "🔕 Умный будильник отключён в настройках.");
            return;
        }

        var hwString = await _diaryService.GetHomeworkStringAsync(chatIdStr);
        var found = TriggerWords.Where(w => hwString.ToLowerInvariant().Contains(w)).ToList();

        if (found.Any())
        {
            var items = string.Join(", ", found);
            await _botClient.SendMessage(chatId,
                $"🔔 *Умный будильник*\n\nЗавтра не забудьте взять: *{items}*! 🎒",
                parseMode: ParseMode.Markdown);
        }
        else
        {
            await _botClient.SendMessage(chatId,
                "✅ Ничего особенного брать не нужно. Приятного дня!");
        }
    }
}
