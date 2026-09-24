using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using MirageDiary.Core;
using MirageDiary.Server.Data;
using MirageDiary.Server.Models;
using MirageDiary.Server.Security;

namespace MirageDiary.Server.Services;

/// <summary>
/// Единый доменный движок бизнес-логики ботов (Telegram и MAX).
/// Инкапсулирует сценарии расписания, оценок, домашнего задания, умного будильника,
/// настроек и ролевой модели без привязки к конкретному транспорту мессенджера.
/// </summary>
public class DiaryBotWorkflowEngine
{
    private readonly AppDbContext _db;
    private readonly DiaryInteractionService _diaryService;
    private readonly SchoolBellScheduleService _bellSchedule;
    private readonly RoleService _roleService;
    private readonly MaintenanceModeService _maintenanceService;
    private readonly UserSyncService _userSyncService;
    private readonly IConfiguration _config;
    private readonly ITokenStore _tokenStore;

    public static readonly string[] TriggerWords =
    {
        "циркуль", "линейка", "краски", "альбом", "форма",
        "транспортир", "контурные карты", "кисти", "карандаши", "тетрадь"
    };

    public DiaryBotWorkflowEngine(
        AppDbContext db,
        DiaryInteractionService diaryService,
        SchoolBellScheduleService bellSchedule,
        RoleService roleService,
        MaintenanceModeService maintenanceService,
        UserSyncService userSyncService,
        IConfiguration config,
        ITokenStore tokenStore)
    {
        _db = db;
        _diaryService = diaryService;
        _bellSchedule = bellSchedule;
        _roleService = roleService;
        _maintenanceService = maintenanceService;
        _userSyncService = userSyncService;
        _config = config;
        _tokenStore = tokenStore;
    }

    public async Task<UserEntity> EnsureUserExistsAsync(string userId, string? username = null, string? firstName = null, string? lastName = null, string platform = "telegram")
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null)
        {
            user = new UserEntity
            {
                Id = userId,
                Role = "Student",
                HasAcceptedPrivacyPolicy = false,
                Platform = platform,
                Username = username ?? "",
                FirstName = firstName ?? "",
                LastName = lastName ?? "",
                UpdatedAt = DateTime.UtcNow
            };
            _db.Users.Add(user);
            await _db.SaveChangesAsync();
        }
        else
        {
            bool modified = false;
            if (!string.IsNullOrEmpty(username) && user.Username != username) { user.Username = username; modified = true; }
            if (!string.IsNullOrEmpty(firstName) && user.FirstName != firstName) { user.FirstName = firstName; modified = true; }
            if (!string.IsNullOrEmpty(lastName) && user.LastName != lastName) { user.LastName = lastName; modified = true; }
            if (modified)
            {
                user.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
            }
        }
        return user;
    }

    // ─── Главное меню ──────────────────────────────────────────────────────────
    public async Task<BotScreen> BuildMainMenuScreenAsync(string userId, string platform = "telegram")
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null)
        {
            user = await EnsureUserExistsAsync(userId, platform: platform);
        }

        if (!user.HasAcceptedPrivacyPolicy)
        {
            return BuildPrivacyPolicyOnboardingScreen();
        }

        var role = await _roleService.GetUserRoleAsync(userId);
        if (role != user.Role)
        {
            user.Role = role;
            await _db.SaveChangesAsync();
        }

        var isAuthorized = !string.IsNullOrEmpty(user.AccessToken);
        var isMaintenance = _maintenanceService.IsMaintenanceModeActive();
        var webAppUrl = _config["MiniAppUrl"] ?? "https://miragediary.space";

        var buttons = new List<List<BotButton>>();

        if (!isAuthorized)
        {
            if (platform == "telegram")
            {
                buttons.Add(new List<BotButton> { BotButton.Link("🔐 Войти через Госуслуги", webAppUrl) });
            }
            else
            {
                var maxBotApp = _config["MaxBot:Username"] ?? _config["MaxBotUsername"] ?? "se13960436_bot";
                buttons.Add(new List<BotButton> { BotButton.MaxApp("🔐 Войти через Госуслуги", maxBotApp, userId) });
            }
        }
        else if (platform == "max")
        {
            var maxBotApp = _config["MaxBot:Username"] ?? _config["MaxBotUsername"] ?? "se13960436_bot";
            buttons.Add(new List<BotButton> { BotButton.MaxApp("📱 Открыть Дневник", maxBotApp, userId) });
        }

        buttons.Add(new List<BotButton>
        {
            BotButton.Callback("📅 Расписание", "cmd_schedule"),
            BotButton.Callback("📚 Домашка", "cmd_homework")
        });
        buttons.Add(new List<BotButton>
        {
            BotButton.Callback("📊 Оценки", "cmd_marks"),
            BotButton.Callback("🎯 Калькулятор", "cmd_calc")
        });
        buttons.Add(new List<BotButton>
        {
            BotButton.Callback("👤 Профиль", "cmd_profile"),
            BotButton.Callback("⚙️ Настройки", "cmd_settings")
        });

        if (role == "Admin")
        {
            buttons.Add(new List<BotButton>
            {
                BotButton.Callback("🔔 До звонка", "cmd_now"),
                BotButton.Callback("👑 Админ-панель", "cmd_admin_panel")
            });
        }
        else
        {
            buttons.Add(new List<BotButton>
            {
                BotButton.Callback("🔔 До звонка", "cmd_now")
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
            string safeOrg = !string.IsNullOrEmpty(user?.OrgName) ? TelegramMarkdownEscaper.Escape(user.OrgName) : "Mirage Diary";
            header = $"🏫 *Завуч* • {safeOrg}";
        }
        else if (isAuthorized)
        {
            string safeClass = !string.IsNullOrEmpty(user.ClassName) ? TelegramMarkdownEscaper.Escape(user.ClassName) : "";
            string safeOrg = !string.IsNullOrEmpty(user.OrgName) ? TelegramMarkdownEscaper.Escape(user.OrgName) : "";

            if (!string.IsNullOrEmpty(safeClass) && !string.IsNullOrEmpty(safeOrg))
                header = $"🎓 *{safeClass}* • {safeOrg}";
            else if (!string.IsNullOrEmpty(safeClass))
                header = $"🎓 *{safeClass}* • Mirage Diary";
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
            text += "\n\n💡 _Отправьте фото или PDF с расписанием звонков — я сохраню его для учеников вашей школы._";
        }

        return BotScreen.Create(text, "banner_menu.png").WithButtons(buttons);
    }

    // ─── Настройки ────────────────────────────────────────────────────────────
    public async Task<BotScreen> BuildSettingsMenuScreenAsync(string userId)
    {
        var text = "⚙️ *Настройки*\n\nВыберите раздел для настройки уведомлений или управления аккаунтом:";
        var buttons = new List<List<BotButton>>
        {
            new() { BotButton.Callback("⏰ Умный будильник", "cmd_alarm") },
            new() { BotButton.Callback("🔗 Связать аккаунт", "cmd_link_account") },
            new() { BotButton.Callback("⭐ Подписка", "cmd_subscription") },
            new() { BotButton.Callback("🗑 Удалить аккаунт", "cmd_delete_account") },
            new() { BotButton.Callback("🏠 Главное меню", "cmd_back_main") }
        };
        return BotScreen.Create(text, "banner_settings.png").WithButtons(buttons);
    }

    // ─── Справка ──────────────────────────────────────────────────────────────
    public BotScreen BuildHelpScreen()
    {
        var text = "ℹ️ *Справка по Mirage Diary*\n\n" +
                   "• *Расписание* — уроки, кабинеты, преподаватели и темы уроков.\n" +
                   "• *Домашка* — задания на сегодня и завтра.\n" +
                   "• *Оценки* — средний балл и прогноз итоговой оценки.\n" +
                   "• *Калькулятор* — расчет количества пятерок для нужной четвертной оценки.\n" +
                   "• *Будильник* — утренние напоминания расписания и проверка рюкзака вечером.\n" +
                   "• *До звонка* — сколько минут осталось до конца текущего урока или перемены.\n\n" +
                   "💡 _Все данные берутся напрямую из ФГИС «Моя Школа» через защищенный шлюз Госуслуг._";

        var buttons = new List<List<BotButton>>
        {
            new() { BotButton.Callback("🏠 Главное меню", "cmd_back_main") }
        };
        return BotScreen.Create(text, "banner_menu.png").WithButtons(buttons);
    }

    // ─── Будильник и напоминания ──────────────────────────────────────────────
    public async Task<BotScreen> BuildAlarmMenuScreenAsync(string userId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        bool master = user?.IsAlarmEnabled ?? true;
        bool sched  = master && (user?.IsScheduleReminderEnabled ?? true);
        bool hw     = master && (user?.IsHomeworkReminderEnabled ?? true);
        bool equip  = master && (user?.IsEquipmentReminderEnabled ?? true);

        string schedTime = user?.ScheduleReminderTime ?? "07:30";
        string hwTime    = user?.HomeworkReminderTime ?? "17:00";
        string equipTime = user?.EquipmentReminderTime ?? "20:30";

        var text = "⏰ *Умный будильник*\n\n" +
                   $"Общий переключатель: {(master ? "🟢 *ВКЛЮЧЕН*" : "🔴 *ВЫКЛЮЧЕН*")}\n\n" +
                   $"• 📅 Расписание утром: {(sched ? $"🟢 в `{schedTime}`" : "🔴 выкл")}\n" +
                   $"• 📚 Домашка днем: {(hw ? $"🟢 в `{hwTime}`" : "🔴 выкл")}\n" +
                   $"• 🎒 Сбор рюкзака вечером: {(equip ? $"🟢 в `{equipTime}`" : "🔴 выкл")}\n\n" +
                   "Нажмите на нужный пункт для изменения времени или статуса:";

        var buttons = new List<List<BotButton>>
        {
            new() { BotButton.Callback(master ? "🔴 Выключить все напоминания" : "🟢 Включить все напоминания", "alarm_toggle_master") },
            new() { BotButton.Callback($"📅 Утреннее расписание ({schedTime})", "alarm_menu_sched") },
            new() { BotButton.Callback($"📚 Напоминание ДЗ ({hwTime})", "alarm_menu_hw") },
            new() { BotButton.Callback($"🎒 Сбор рюкзака ({equipTime})", "alarm_menu_equip") },
            new() { BotButton.Callback("🎒 Проверить рюкзак сейчас", "cmd_alarm_check") },
            new() { BotButton.Callback("⚙️ Назад в настройки", "cmd_settings") }
        };

        return BotScreen.Create(text, "banner_alarm.png").WithButtons(buttons);
    }

    public async Task<BotCallbackResult> ToggleMasterAlarmAsync(string userId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user != null)
        {
            user.IsAlarmEnabled = !user.IsAlarmEnabled;
            user.IsScheduleReminderEnabled = user.IsAlarmEnabled;
            user.IsHomeworkReminderEnabled = user.IsAlarmEnabled;
            user.IsEquipmentReminderEnabled = user.IsAlarmEnabled;
            await _db.SaveChangesAsync();
            await _userSyncService.SyncUserAcrossPlatformsAsync(user);
        }
        var screen = await BuildAlarmMenuScreenAsync(userId);
        return BotCallbackResult.Ok(screen, user?.IsAlarmEnabled == true ? "Будильник включен" : "Будильник выключен");
    }

    public async Task<BotScreen> BuildScheduleReminderMenuScreenAsync(string userId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        bool enabled = (user?.IsAlarmEnabled ?? true) && (user?.IsScheduleReminderEnabled ?? true);
        string time = user?.ScheduleReminderTime ?? "07:30";

        var text = $"📅 *Напоминание расписания утром*\n\n" +
                   $"Статус: {(enabled ? "🟢 *Включено*" : "🔴 *Выключено*")}\n" +
                   $"Текущее время: *{time}*\n\n" +
                   "Выберите подходящее время отправки расписания перед уроками:";

        var buttons = new List<List<BotButton>>
        {
            new() { BotButton.Callback(enabled ? "🔴 Отключить" : "🟢 Включить", "alarm_toggle_sched") },
            new()
            {
                BotButton.Callback("07:00" == time ? "✅ 07:00" : "07:00", "alarm_set_sched_07:00"),
                BotButton.Callback("07:30" == time ? "✅ 07:30" : "07:30", "alarm_set_sched_07:30"),
                BotButton.Callback("08:00" == time ? "✅ 08:00" : "08:00", "alarm_set_sched_08:00")
            },
            new() { BotButton.Callback("⏰ Назад в меню будильника", "cmd_alarm") }
        };

        return BotScreen.Create(text, "banner_alarm.png").WithButtons(buttons);
    }

    public async Task<BotCallbackResult> ToggleScheduleReminderAsync(string userId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user != null)
        {
            user.IsScheduleReminderEnabled = !user.IsScheduleReminderEnabled;
            if (user.IsScheduleReminderEnabled) user.IsAlarmEnabled = true;
            await _db.SaveChangesAsync();
            await _userSyncService.SyncUserAcrossPlatformsAsync(user);
        }
        var screen = await BuildScheduleReminderMenuScreenAsync(userId);
        return BotCallbackResult.Ok(screen, "Настройка сохранена");
    }

    public async Task<BotCallbackResult> SetScheduleReminderTimeAsync(string userId, string time)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user != null)
        {
            user.ScheduleReminderTime = time;
            user.IsScheduleReminderEnabled = true;
            user.IsAlarmEnabled = true;
            await _db.SaveChangesAsync();
            await _userSyncService.SyncUserAcrossPlatformsAsync(user);
        }
        var screen = await BuildScheduleReminderMenuScreenAsync(userId);
        return BotCallbackResult.Ok(screen, $"Время установлено: {time}");
    }

    public async Task<BotScreen> BuildHomeworkReminderMenuScreenAsync(string userId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        bool enabled = (user?.IsAlarmEnabled ?? true) && (user?.IsHomeworkReminderEnabled ?? true);
        string time = user?.HomeworkReminderTime ?? "17:00";

        var text = $"📚 *Напоминание домашнего задания*\n\n" +
                   $"Статус: {(enabled ? "🟢 *Включено*" : "🔴 *Выключено*")}\n" +
                   $"Текущее время: *{time}*\n\n" +
                   "Выберите время получения сводки ДЗ после школы:";

        var buttons = new List<List<BotButton>>
        {
            new() { BotButton.Callback(enabled ? "🔴 Отключить" : "🟢 Включить", "alarm_toggle_hw") },
            new()
            {
                BotButton.Callback("15:00" == time ? "✅ 15:00" : "15:00", "alarm_set_hw_15:00"),
                BotButton.Callback("17:00" == time ? "✅ 17:00" : "17:00", "alarm_set_hw_17:00"),
                BotButton.Callback("19:00" == time ? "✅ 19:00" : "19:00", "alarm_set_hw_19:00")
            },
            new() { BotButton.Callback("⏰ Назад в меню будильника", "cmd_alarm") }
        };

        return BotScreen.Create(text, "banner_alarm.png").WithButtons(buttons);
    }

    public async Task<BotCallbackResult> ToggleHomeworkReminderAsync(string userId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user != null)
        {
            user.IsHomeworkReminderEnabled = !user.IsHomeworkReminderEnabled;
            if (user.IsHomeworkReminderEnabled) user.IsAlarmEnabled = true;
            await _db.SaveChangesAsync();
            await _userSyncService.SyncUserAcrossPlatformsAsync(user);
        }
        var screen = await BuildHomeworkReminderMenuScreenAsync(userId);
        return BotCallbackResult.Ok(screen, "Настройка сохранена");
    }

    public async Task<BotCallbackResult> SetHomeworkReminderTimeAsync(string userId, string time)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user != null)
        {
            user.HomeworkReminderTime = time;
            user.IsHomeworkReminderEnabled = true;
            user.IsAlarmEnabled = true;
            await _db.SaveChangesAsync();
            await _userSyncService.SyncUserAcrossPlatformsAsync(user);
        }
        var screen = await BuildHomeworkReminderMenuScreenAsync(userId);
        return BotCallbackResult.Ok(screen, $"Время установлено: {time}");
    }

    public async Task<BotScreen> BuildEquipmentReminderMenuScreenAsync(string userId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        bool enabled = (user?.IsAlarmEnabled ?? true) && (user?.IsEquipmentReminderEnabled ?? true);
        string time = user?.EquipmentReminderTime ?? "20:30";

        var text = $"🎒 *Сбор рюкзака на завтра*\n\n" +
                   $"Статус: {(enabled ? "🟢 *Включено*" : "🔴 *Выключено*")}\n" +
                   $"Текущее время: *{time}*\n\n" +
                   "Бот проверяет расписание на завтра и напоминает взять форму, краски, циркуль или атлас.";

        var buttons = new List<List<BotButton>>
        {
            new() { BotButton.Callback(enabled ? "🔴 Отключить" : "🟢 Включить", "alarm_toggle_equip") },
            new()
            {
                BotButton.Callback("19:00" == time ? "✅ 19:00" : "19:00", "alarm_set_equip_19:00"),
                BotButton.Callback("20:30" == time ? "✅ 20:30" : "20:30", "alarm_set_equip_20:30"),
                BotButton.Callback("21:30" == time ? "✅ 21:30" : "21:30", "alarm_set_equip_21:30")
            },
            new() { BotButton.Callback("⏰ Назад в меню будильника", "cmd_alarm") }
        };

        return BotScreen.Create(text, "banner_alarm.png").WithButtons(buttons);
    }

    public async Task<BotCallbackResult> ToggleEquipmentReminderAsync(string userId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user != null)
        {
            user.IsEquipmentReminderEnabled = !user.IsEquipmentReminderEnabled;
            if (user.IsEquipmentReminderEnabled) user.IsAlarmEnabled = true;
            await _db.SaveChangesAsync();
            await _userSyncService.SyncUserAcrossPlatformsAsync(user);
        }
        var screen = await BuildEquipmentReminderMenuScreenAsync(userId);
        return BotCallbackResult.Ok(screen, "Настройка сохранена");
    }

    public async Task<BotCallbackResult> SetEquipmentReminderTimeAsync(string userId, string time)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user != null)
        {
            user.EquipmentReminderTime = time;
            user.IsEquipmentReminderEnabled = true;
            user.IsAlarmEnabled = true;
            await _db.SaveChangesAsync();
            await _userSyncService.SyncUserAcrossPlatformsAsync(user);
        }
        var screen = await BuildEquipmentReminderMenuScreenAsync(userId);
        return BotCallbackResult.Ok(screen, $"Время установлено: {time}");
    }

    // ─── Расписание уроков ────────────────────────────────────────────────────
    public async Task<BotScreen> BuildScheduleScreenAsync(string userId, string? dateArg = null, bool forceRefresh = false)
    {
        var scheduleText = await _diaryService.GetScheduleStringAsync(userId, dateArg, forceRefresh);
        var buttons = new List<List<BotButton>>
        {
            new()
            {
                BotButton.Callback("⬅️ Вчера", "sched_date_yesterday"),
                BotButton.Callback("Сегодня", "sched_date_today"),
                BotButton.Callback("Завтра ➡️", "sched_date_tomorrow")
            },
            new()
            {
                BotButton.Callback("🔄 Обновить", $"sched_refresh_{dateArg ?? "today"}"),
                BotButton.Callback("🏠 Меню", "cmd_back_main")
            }
        };

        return BotScreen.Create(scheduleText, "banner_schedule.png").WithButtons(buttons);
    }

    // ─── Домашнее задание ─────────────────────────────────────────────────────
    public async Task<BotScreen> BuildHomeworkScreenAsync(string userId, string? dateArg = null, bool forceRefresh = false)
    {
        var hwText = await _diaryService.GetHomeworkStringAsync(userId, dateArg, forceRefresh);
        var buttons = new List<List<BotButton>>
        {
            new()
            {
                BotButton.Callback("Сегодня", "hw_date_today"),
                BotButton.Callback("На завтра ➡️", "hw_date_tomorrow")
            },
            new()
            {
                BotButton.Callback("🔄 Обновить", $"hw_refresh_{dateArg ?? "today"}"),
                BotButton.Callback("🏠 Меню", "cmd_back_main")
            }
        };

        return BotScreen.Create(hwText, "banner_homework.png").WithButtons(buttons);
    }

    // ─── Оценки и средний балл ────────────────────────────────────────────────
    public async Task<BotScreen> BuildMarksScreenAsync(string userId, bool forceRefresh = false)
    {
        var marksText = await _diaryService.GetMarksStringAsync(userId, forceRefresh);
        var buttons = new List<List<BotButton>>
        {
            new()
            {
                BotButton.Callback("🎯 Калькулятор оценок", "cmd_calc"),
                BotButton.Callback("🔄 Обновить", "marks_refresh")
            },
            new()
            {
                BotButton.Callback("🏠 Главное меню", "cmd_back_main")
            }
        };

        return BotScreen.Create(marksText, "banner_marks.png").WithButtons(buttons);
    }

    // ─── Калькулятор оценок ───────────────────────────────────────────────────
    public async Task<BotScreen> BuildGradeCalculatorSubjectsScreenAsync(string userId, bool forceRefresh = false)
    {
        var subjects = await _diaryService.GetSubjectGradesSummaryAsync(userId, forceRefresh);
        if (subjects == null || subjects.Count == 0)
        {
            var emptyText = "🎯 *Калькулятор оценок*\n\nВ дневнике пока нет предметов с оценками.";
            var emptyButtons = new List<List<BotButton>> { new() { BotButton.Callback("🏠 Меню", "cmd_back_main") } };
            return BotScreen.Create(emptyText, "banner_marks.png").WithButtons(emptyButtons);
        }

        var text = "🎯 *Калькулятор оценок*\n\nВыберите предмет для расчета необходимых оценок:";
        var buttons = new List<List<BotButton>>();

        for (int i = 0; i < subjects.Count; i += 2)
        {
            var row = new List<BotButton>();
            var s1 = subjects[i];
            row.Add(BotButton.Callback($"{s1.StatusEmoji} {Truncate(s1.SubjectName, 14)}", $"calc_subj_{i}"));

            if (i + 1 < subjects.Count)
            {
                var s2 = subjects[i + 1];
                row.Add(BotButton.Callback($"{s2.StatusEmoji} {Truncate(s2.SubjectName, 14)}", $"calc_subj_{i + 1}"));
            }
            buttons.Add(row);
        }

        buttons.Add(new List<BotButton> { BotButton.Callback("🏠 Меню", "cmd_back_main") });
        return BotScreen.Create(text, "banner_marks.png").WithButtons(buttons);
    }

    public async Task<BotScreen> BuildSubjectTargetOptionsScreenAsync(string userId, int subjectIndex)
    {
        var subjects = await _diaryService.GetSubjectGradesSummaryAsync(userId);
        if (subjects == null || subjectIndex < 0 || subjectIndex >= subjects.Count)
        {
            return await BuildGradeCalculatorSubjectsScreenAsync(userId);
        }

        var s = subjects[subjectIndex];
        string safeSubject = TelegramMarkdownEscaper.Escape(s.SubjectName);
        string gpaStr = s.TotalWeight > 0 ? $"{s.AverageGpa:F2}" : "—";

        var text = $"🎯 *Калькулятор оценок*\n\n" +
                   $"📚 *Предмет:* {safeSubject}\n" +
                   $"📊 *Текущий балл:* `{gpaStr}`\n" +
                   $"🔮 *Прогноз четверти:* *{s.FinalForecast}*\n\n" +
                   "Какую оценку в четверти вы хотите получить?";

        var buttons = new List<List<BotButton>>
        {
            new()
            {
                BotButton.Callback("🟢 Цель 5", $"calc_target_{subjectIndex}_5"),
                BotButton.Callback("🟡 Цель 4", $"calc_target_{subjectIndex}_4"),
                BotButton.Callback("🔴 Цель 3", $"calc_target_{subjectIndex}_3")
            },
            new()
            {
                BotButton.Callback("⬅️ Выбрать другой предмет", "cmd_calc"),
                BotButton.Callback("🏠 Меню", "cmd_back_main")
            }
        };

        return BotScreen.Create(text, "banner_marks.png").WithButtons(buttons);
    }

    public async Task<BotScreen> BuildTargetCalculationResultScreenAsync(string userId, int subjectIndex, int targetGrade)
    {
        var subjects = await _diaryService.GetSubjectGradesSummaryAsync(userId);
        if (subjects == null || subjectIndex < 0 || subjectIndex >= subjects.Count)
        {
            return await BuildGradeCalculatorSubjectsScreenAsync(userId);
        }

        var s = subjects[subjectIndex];
        string safeSubject = TelegramMarkdownEscaper.Escape(s.SubjectName);
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
            text = $"🎉 *Отличные новости по предмету {safeSubject}!* 🚀\n\n" +
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
                   $"📚 *Предмет:* {safeSubject}\n\n" +
                   $"• *Текущий балл:* `{currentGpa:F2}`\n" +
                   $"• *Требуемый порог:* `≥ {targetThreshold:F2}`\n\n" +
                   $"Чтобы гарантированно получить *{targetGrade}* в четверти, вам нужно заработать:\n" +
                   $"• 🟢 *{neededFivesW1}* пятёрок за ответы на уроках (вес 1)\n" +
                   $"• 🟢 *{neededFivesW2}* пятёрки за контрольные работы (вес 2){foursOption}\n\n" +
                   $"💡 _Каждая хорошая оценка за самостоятельную или контрольную работу быстрее всего поднимет ваш средний балл!_";
        }

        var buttons = new List<List<BotButton>>
        {
            new()
            {
                BotButton.Callback("⬅️ К целям предмета", $"calc_subj_{subjectIndex}"),
                BotButton.Callback("📚 К предметам", "cmd_calc")
            },
            new()
            {
                BotButton.Callback("🏠 Главное меню", "cmd_back_main")
            }
        };

        return BotScreen.Create(text, "banner_marks.png").WithButtons(buttons);
    }

    // ─── Проверка рюкзака ─────────────────────────────────────────────────────
    public async Task<BotScreen> BuildAlarmCheckScreenAsync(string userId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null || string.IsNullOrEmpty(user.AccessToken))
        {
            return BotScreen.Create("❌ Вы не авторизованы. Войдите через Mini App.", "banner_alarm.png");
        }

        var hwString = await _diaryService.GetHomeworkStringAsync(userId, "завтра");
        var schedString = await _diaryService.GetScheduleStringAsync(userId, "завтра");
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

        string text;
        if (foundTriggers.Any())
        {
            var items = string.Join("\n• ", foundTriggers.Select(t => char.ToUpper(t[0]) + t[1..]));
            text = $"🎒 *Умный будильник: сбор рюкзака на завтра!*\n\nВам понадобятся:\n• {items}\n\nНе забудьте положить их в рюкзак прямо сейчас! 🎒";
        }
        else
        {
            text = "🎒 *Умный будильник*\n\nСпецинструментов на завтра не требуется. Проверьте обычные тетради и учебники по расписанию! ✅";
        }

        var buttons = new List<List<BotButton>>
        {
            new()
            {
                BotButton.Callback("⏰ Настройки будильника", "cmd_alarm"),
                BotButton.Callback("🏠 Главное меню", "cmd_back_main")
            }
        };

        return BotScreen.Create(text, "banner_alarm.png").WithButtons(buttons);
    }

    // ─── Профиль ──────────────────────────────────────────────────────────────
    public async Task<BotScreen> BuildProfileScreenAsync(string userId, bool forceRefresh = false)
    {
        var profileText = await _diaryService.GetProfileStringAsync(userId, forceRefresh);
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);

        var buttons = new List<List<BotButton>>();
        if (user?.Role == "Parent")
        {
            buttons.Add(new List<BotButton> { BotButton.Callback("👨‍👧 Выбрать ребенка", "cmd_children_menu") });
        }
        buttons.Add(new List<BotButton>
        {
            BotButton.Callback("🚪 Выйти из аккаунта", "cmd_logout_confirm"),
            BotButton.Callback("🔄 Обновить", "profile_refresh")
        });
        buttons.Add(new List<BotButton> { BotButton.Callback("🏠 Главное меню", "cmd_back_main") });

        return BotScreen.Create(profileText, "banner_profile.png").WithButtons(buttons);
    }

    // ─── Выход из аккаунта ────────────────────────────────────────────────────
    public BotScreen BuildLogoutConfirmationScreen()
    {
        var text = "🚪 *Выход из аккаунта*\n\n" +
                   "Вы уверены, что хотите выйти? Токены авторизации будут удалены, и бот больше не сможет получать ваши оценки и расписание.";

        var buttons = new List<List<BotButton>>
        {
            new()
            {
                BotButton.Callback("❌ Да, выйти", "cmd_logout_execute"),
                BotButton.Callback("Отмена", "cmd_profile")
            }
        };

        return BotScreen.Create(text, "banner_profile.png").WithButtons(buttons);
    }

    public async Task<BotScreen> PerformLogoutAsync(string userId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user != null)
        {
            user.AccessToken = "";
            user.RefreshToken = "";
            user.SessionId = "";
            user.StudentId = "";
            await _db.SaveChangesAsync();
            await _tokenStore.RevokeTokensAsync(userId);
            await _userSyncService.SyncUserAcrossPlatformsAsync(user);
        }

        var text = "✅ *Вы успешно вышли из аккаунта.*\n\nЧтобы снова использовать бота, авторизуйтесь через Mini App.";
        var buttons = new List<List<BotButton>>
        {
            new() { BotButton.Callback("🏠 Главное меню", "cmd_back_main") }
        };

        return BotScreen.Create(text, "banner_menu.png").WithButtons(buttons);
    }

    // ─── Выбор ребенка (Родитель) ─────────────────────────────────────────────
    public async Task<BotScreen> BuildChildrenMenuScreenAsync(string userId)
    {
        var children = await _diaryService.GetChildrenListAsync(userId);
        if (children == null || children.Count == 0)
        {
            var text = "👨‍👧 *Выбор ребенка*\n\nДети не найдены в вашей учетной записи Госуслуг.";
            var buttons = new List<List<BotButton>> { new() { BotButton.Callback("👤 Профиль", "cmd_profile") } };
            return BotScreen.Create(text, "banner_profile.png").WithButtons(buttons);
        }

        var screenText = "👨‍👧 *Выберите ребенка для просмотра дневника:*";
        var screenButtons = new List<List<BotButton>>();

        foreach (var c in children)
        {
            string name = $"{c.StudentLastName} {c.StudentFirstName}".Trim();
            if (string.IsNullOrEmpty(name)) name = "Ученик";
            screenButtons.Add(new List<BotButton> { BotButton.Callback($"👶 {name}", $"select_child_{c.StudentId}") });
        }

        screenButtons.Add(new List<BotButton> { BotButton.Callback("👤 Профиль", "cmd_profile") });
        return BotScreen.Create(screenText, "banner_profile.png").WithButtons(screenButtons);
    }

    public async Task<BotCallbackResult> SelectChildAsync(string userId, string studentId)
    {
        bool ok = await _diaryService.SelectChildAsync(userId, studentId);
        var screen = await BuildProfileScreenAsync(userId, forceRefresh: true);
        return BotCallbackResult.Ok(screen, ok ? "Ребенок выбран" : "Ошибка выбора");
    }

    // ─── До звонка ────────────────────────────────────────────────────────────
    public async Task<BotScreen> BuildNowScreenAsync(string userId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        int shift = user?.Shift ?? 1;
        var localTime = DateTime.UtcNow.AddHours(user?.UtcOffsetHours ?? 5);

        var status = _bellSchedule.GetCurrentStatus(localTime, shift);
        string schoolName = !string.IsNullOrWhiteSpace(user?.OrgName) ? user.OrgName : "МАОУ СОШ № 113";

        string text = $"🔔 *Расписание звонков ({schoolName})*\n\n" +
                      $"{status.StatusMessage}\n\n" +
                      $"🕐 *Точное время:* `{localTime:HH:mm:ss}` (Екатеринбург)\n" +
                      $"🎓 *Смена:* {shift}-я смена";

        var buttons = new List<List<BotButton>>
        {
            new()
            {
                BotButton.Callback("🔄 Обновить", "cmd_now"),
                BotButton.Callback(shift == 1 ? "Сменить на 2 смену" : "Сменить на 1 смену", shift == 1 ? "cmd_set_shift:2" : "cmd_set_shift:1")
            },
            new() { BotButton.Callback("🏠 Главное меню", "cmd_back_main") }
        };

        return BotScreen.Create(text, "banner_now.png").WithButtons(buttons);
    }

    public async Task<BotCallbackResult> SetShiftAsync(string userId, int shift)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user != null)
        {
            user.Shift = shift;
            await _db.SaveChangesAsync();
            await _userSyncService.SyncUserAcrossPlatformsAsync(user);
        }
        var screen = await BuildNowScreenAsync(userId);
        return BotCallbackResult.Ok(screen, $"Смена переключена на {shift}");
    }

    // ─── Подписка Live Grade Watcher ──────────────────────────────────────────
    public async Task<BotScreen> BuildSubscriptionScreenAsync(string userId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        bool isSub = user?.IsSubscribed ?? false;
        var exp = user?.SubscriptionExpiresAtUtc;

        string statusText = isSub
            ? $"🟢 *Активна* (до {exp?.ToString("dd.MM.yyyy") ?? "бессрочно"})"
            : "⚪ *Не активна*";

        var text = $"⭐ *Подписка Mirage Diary Plus*\n\n" +
                   $"Статус: {statusText}\n\n" +
                   "🔥 *Возможности Plus:*\n" +
                   "• Мгновенные уведомления о новых оценках (Live Grade Watcher)\n" +
                   "• Приоритетное обновление расписания и домашних заданий\n" +
                   "• Умный будильник с анализом расписания звонков вашей школы\n\n" +
                   "🎁 _Сейчас доступен бесплатный Beta-доступ на 30 дней!_";

        var buttons = new List<List<BotButton>>();
        if (!isSub)
        {
            buttons.Add(new List<BotButton> { BotButton.Callback("🎁 Активировать Beta (30 дней)", "cmd_sub_activate_beta") });
        }
        buttons.Add(new List<BotButton> { BotButton.Callback("⚙️ Настройки", "cmd_settings") });

        return BotScreen.Create(text, "banner_settings.png").WithButtons(buttons);
    }

    public async Task<BotCallbackResult> ActivateBetaSubscriptionAsync(string userId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user != null)
        {
            user.IsSubscribed = true;
            user.SubscriptionExpiresAtUtc = DateTime.UtcNow.AddDays(30);
            await _db.SaveChangesAsync();
            await _userSyncService.SyncUserAcrossPlatformsAsync(user);
        }
        var screen = await BuildSubscriptionScreenAsync(userId);
        return BotCallbackResult.Ok(screen, "🎉 Подписка активирована на 30 дней!");
    }

    // ─── 152-ФЗ Политика конфиденциальности ───────────────────────────────────
    public BotScreen BuildPrivacyPolicyOnboardingScreen()
    {
        var text = "📜 *Политика обработки персональных данных*\n\n" +
                   "Для доступа к школьному дневнику и расписанию бот обрабатывает ваши авторизационные данные в соответствии с 152-ФЗ РФ.\n\n" +
                   "• Данные хранятся в защищенном виде с использованием двухслойного шифрования AES-256-GCM (`enc:v2:`).\n" +
                   "• Серверы расположены на территории Российской Федерации.\n" +
                   "• Вы можете в любой момент отозвать согласие и удалить данные через меню настроек.\n\n" +
                   "Нажимая кнопку ниже, вы подтверждаете согласие с Политикой конфиденциальности.";

        var buttons = new List<List<BotButton>>
        {
            new() { BotButton.Callback("✅ Принять и продолжить", "cmd_accept_privacy") },
            new() { BotButton.Link("📄 Читать политику", "https://miragediary.space/privacy") }
        };

        return BotScreen.Create(text, "banner_menu.png").WithButtons(buttons);
    }

    public async Task AcceptPrivacyPolicyAsync(string userId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user != null)
        {
            user.HasAcceptedPrivacyPolicy = true;
            user.PrivacyPolicyAcceptedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
    }

    // ─── Удаление аккаунта ────────────────────────────────────────────────────
    public BotScreen BuildDeleteAccountConfirmationScreen()
    {
        var text = "⚠️ *Удаление аккаунта (152-ФЗ РФ)*\n\n" +
                   "Вы действительно хотите навсегда стереть свой профиль из системы Mirage Diary?\n\n" +
                   "• Все токены ЕСИА и данные сессий будут уничтожены.\n" +
                   "• Настройки будильника и профиля будут удалены.\n" +
                   "• Для использования бота потребуется повторный вход.";

        var buttons = new List<List<BotButton>>
        {
            new()
            {
                BotButton.Callback("🗑 Да, удалить всё", "cmd_delete_account_confirm"),
                BotButton.Callback("Отмена", "cmd_settings")
            }
        };

        return BotScreen.Create(text, "banner_settings.png").WithButtons(buttons);
    }

    public async Task<BotScreen> ExecuteDeleteAccountAsync(string userId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user != null)
        {
            _db.Users.Remove(user);
            await _db.SaveChangesAsync();
            await _tokenStore.RevokeTokensAsync(userId);
        }

        var text = "✅ *Ваш аккаунт и персональные данные полностью удалены.*\n\nСпасибо, что пользовались Mirage Diary!";
        var buttons = new List<List<BotButton>>
        {
            new() { BotButton.Callback("🏠 В начало", "cmd_back_main") }
        };

        return BotScreen.Create(text, "banner_menu.png").WithButtons(buttons);
    }

    // ─── Связка аккаунтов ─────────────────────────────────────────────────────
    public async Task<BotScreen> BuildLinkAccountScreenAsync(string userId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        string platform = user?.Platform ?? "telegram";

        var text = "🔗 *Связка аккаунтов (Telegram ⇄ MAX)*\n\n" +
                   "Вы можете связать свои аккаунты в Telegram и мессенджере MAX, чтобы авторизация и настройки автоматически синхронизировались.\n\n" +
                   "Отправьте боту команду в формате:\n" +
                   "`/link КОД_СВЯЗКИ`\n\n" +
                   "💡 _Код связки можно получить в настройках второго мессенджера._";

        var buttons = new List<List<BotButton>>
        {
            new() { BotButton.Callback("⚙️ Назад в настройки", "cmd_settings") }
        };

        return BotScreen.Create(text, "banner_settings.png").WithButtons(buttons);
    }

    private static string Truncate(string str, int maxLength)
    {
        if (string.IsNullOrEmpty(str)) return string.Empty;
        return str.Length <= maxLength ? str : str[..maxLength] + "…";
    }
}
