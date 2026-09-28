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
    private readonly BotStateService _stateService;
    private readonly BotCaptchaService _captchaService;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _profileRefreshCooldown = new();

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
        ITokenStore tokenStore,
        BotStateService? stateService = null,
        BotCaptchaService? captchaService = null)
    {
        _db = db;
        _diaryService = diaryService;
        _bellSchedule = bellSchedule;
        _roleService = roleService;
        _maintenanceService = maintenanceService;
        _userSyncService = userSyncService;
        _config = config;
        _tokenStore = tokenStore;
        _stateService = stateService ?? new BotStateService();
        _captchaService = captchaService ?? new BotCaptchaService();
    }

    public static bool IsUserVerified(UserEntity? user)
    {
        if (user == null) return false;
        return user.HasPassedCaptcha || user.HasAcceptedPrivacyPolicy || !string.IsNullOrEmpty(user.AccessToken);
    }

    public string BuildMiniAppUrl(string userId, string? firstName = null, bool force = false, string? mode = null)
    {
        var baseUrl = (_config["MiniAppUrl"] ?? "https://miragediary.space").TrimEnd('/');
        var fnParam = !string.IsNullOrEmpty(firstName) ? $"&firstName={Uri.EscapeDataString(firstName)}" : "";
        var forceParam = force ? "&force=1" : "";
        var modeParam = !string.IsNullOrEmpty(mode) ? $"&mode={Uri.EscapeDataString(mode)}" : "";
        return $"{baseUrl}?userId={Uri.EscapeDataString(userId)}{fnParam}{forceParam}{modeParam}";
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

    public async Task<(bool hasBoth, UserEntity? peerUser)> CheckDualPlatformRegistrationAsync(UserEntity? user)
    {
        if (user == null || string.IsNullOrWhiteSpace(user.Id)) return (false, null);

        bool isCurrentMax = user.Id.StartsWith("max_");

        // 1. Direct LinkedUserId check
        if (!string.IsNullOrEmpty(user.LinkedUserId))
        {
            var peer = await _db.Users.FirstOrDefaultAsync(u => u.Id == user.LinkedUserId);
            if (peer != null)
            {
                bool peerIsMax = peer.Id.StartsWith("max_");
                if (isCurrentMax != peerIsMax) return (true, peer);
            }
        }

        // 2. Shared Oid or StudentId
        string oid = user.Oid ?? "";
        string studentId = user.StudentId ?? "";
        if (!string.IsNullOrEmpty(oid) || !string.IsNullOrEmpty(studentId))
        {
            var peer = await _db.Users.FirstOrDefaultAsync(u => u.Id != user.Id &&
                ((!string.IsNullOrEmpty(oid) && u.Oid == oid) ||
                 (!string.IsNullOrEmpty(studentId) && u.StudentId == studentId)));

            if (peer != null)
            {
                bool peerIsMax = peer.Id.StartsWith("max_");
                if (isCurrentMax != peerIsMax)
                {
                    if (string.IsNullOrEmpty(user.LinkedUserId))
                    {
                        user.LinkedUserId = peer.Id;
                        peer.LinkedUserId = user.Id;
                        await _db.SaveChangesAsync();
                    }
                    return (true, peer);
                }
            }
        }

        return (false, null);
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
                buttons.Add(new List<BotButton> { BotButton.WebApp("🔐 Войти через Госуслуги", BuildMiniAppUrl(userId, user?.FirstName)) });
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

        if (role == "Parent" || !string.IsNullOrEmpty(user?.SelectedStudentId))
        {
            buttons.Add(new List<BotButton>
            {
                BotButton.Callback("👨‍👧 Дети", "cmd_children_menu")
            });
        }

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
        else if (role == "Parent" || !string.IsNullOrEmpty(user?.SelectedStudentId))
        {
            string safeClass = !string.IsNullOrEmpty(user?.ClassName) ? TelegramMarkdownEscaper.Escape(user.ClassName) : "";
            string safeOrg = !string.IsNullOrEmpty(user?.OrgName) ? TelegramMarkdownEscaper.Escape(user.OrgName) : "";

            if (!string.IsNullOrEmpty(safeClass) && !string.IsNullOrEmpty(safeOrg))
                header = $"👨‍👧 *Родитель ({safeClass})* • {safeOrg}";
            else if (!string.IsNullOrEmpty(safeClass))
                header = $"👨‍👧 *Родитель ({safeClass})* • Mirage Diary";
            else
                header = "👨‍👧 *Родитель* • Mirage Diary";
        }
        else if (isAuthorized)
        {
            string safeClass = !string.IsNullOrEmpty(user?.ClassName) ? TelegramMarkdownEscaper.Escape(user.ClassName) : "";
            string safeOrg = !string.IsNullOrEmpty(user?.OrgName) ? TelegramMarkdownEscaper.Escape(user.OrgName) : "";

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

        var (hasBothPlatforms, _) = await CheckDualPlatformRegistrationAsync(user);
        if (hasBothPlatforms && string.IsNullOrEmpty(user?.NotificationChannel))
        {
            text += "\n\n🔔 *Куда присылать уведомления?*\n" +
                    "Вы привязали и Telegram, и MAX! Выберите, куда отправлять оценки, расписание и домашку:";

            int insertIndex = isAuthorized && platform == "max" ? 1 : 0;
            buttons.Insert(insertIndex, new List<BotButton>
            {
                BotButton.Callback("✈️ В Telegram", "cmd_set_notify_channel:telegram"),
                BotButton.Callback("💬 В MAX", "cmd_set_notify_channel:max"),
                BotButton.Callback("🔄 В оба", "cmd_set_notify_channel:all")
            });
        }

        return BotScreen.Create(text, "banner_menu.png").WithButtons(buttons);
    }

    // ─── Настройки ────────────────────────────────────────────────────────────
    // ─── Настройки ────────────────────────────────────────────────────────────
    public async Task<BotScreen> BuildSettingsMenuScreenAsync(string userId, string platform = "telegram")
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        var alarmOn = user?.IsAlarmEnabled ?? true;
        var gradesOn = user?.IsGradeWatcherEnabled ?? true;
        var webAppUrl = _config["MiniAppUrl"] ?? "https://t.me";
        var maxBotApp = _config["MaxBot:Username"] ?? _config["MaxBotUsername"] ?? "se13960436_bot";

        var buttons = new List<List<BotButton>>
        {
            new() { BotButton.Callback($"⏰ Будильник: {(alarmOn ? "🟢 Вкл" : "🔴 Выкл")}", "cmd_alarm_menu") }
        };

        if (platform.Equals("max", StringComparison.OrdinalIgnoreCase))
        {
            buttons.Add(new List<BotButton>
            {
                BotButton.MaxApp("🔄 Госуслуги", maxBotApp, userId),
                BotButton.Callback("🔗 Связать с Telegram", "cmd_link_account")
            });
        }
        else
        {
            buttons.Add(new List<BotButton>
            {
                BotButton.WebApp("🔄 Госуслуги", BuildMiniAppUrl(userId, user?.FirstName, force: true)),
                BotButton.Callback("🔗 Связать с MAX", "cmd_link_account")
            });
        }

        buttons.Add(new List<BotButton>
        {
            BotButton.Callback("ℹ️ Справка", "cmd_help"),
            BotButton.Callback("🗑 Удалить аккаунт", "cmd_delete_account")
        });

        var (hasBoth, _) = await CheckDualPlatformRegistrationAsync(user);
        string channelLine = "";
        if (hasBoth)
        {
            string chText = (user?.NotificationChannel?.ToLowerInvariant()) switch
            {
                "telegram" => "`✈️ Только Telegram`",
                "max" => "`💬 Только MAX`",
                "all" => "`🔄 В оба (TG + MAX)`",
                _ => "`⚠️ Не выбран (в оба)`"
            };
            channelLine = $"\n• 📨 Доставка уведомлений: {chText}";

            buttons.Insert(buttons.Count - 1, new List<BotButton>
            {
                BotButton.Callback("📨 Выбор мессенджера для пушей", "cmd_settings_notify_channel")
            });
        }

        buttons.Add(new List<BotButton>
        {
            BotButton.Callback("🏠 Главное меню", "cmd_back_main")
        });

        string text = "⚙️ *Настройки*\n\n" +
                      $"• 📊 Оповещения об оценках: {(gradesOn ? "`🟢 Включены`" : "`🔴 Выключены`")}\n" +
                      $"• ⏰ Будильник: {(alarmOn ? "`🟢 Включен`" : "`🔴 Выключен`")}" +
                      channelLine + "\n" +
                      $"• 👤 ID: `{userId}`";

        return BotScreen.Create(text, "banner_settings.png").WithButtons(buttons);
    }

    public async Task<BotCallbackResult> ToggleSettingsGradeWatcherAsync(string userId, string platform = "telegram")
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user != null)
        {
            user.IsGradeWatcherEnabled = !user.IsGradeWatcherEnabled;
            await _db.SaveChangesAsync();
            await _userSyncService.SyncUserAcrossPlatformsAsync(user);
        }
        var screen = await BuildSettingsMenuScreenAsync(userId, platform);
        return BotCallbackResult.Ok(screen, user?.IsGradeWatcherEnabled == true ? "Оповещения об оценках включены" : "Оповещения об оценках выключены");
    }

    public async Task<BotScreen> BuildNotificationChannelSelectScreenAsync(string userId, string platform = "telegram")
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        string ch = user?.NotificationChannel?.ToLowerInvariant() ?? "";

        var text = "📨 *Выбор мессенджера для уведомлений*\n\n" +
                   "Вы зарегистрированы одновременно в Telegram и в MAX.\n\n" +
                   "Куда присылать оповещения о новых оценках, расписании и домашке:\n\n" +
                   "• ✈️ *Только Telegram* — пуши приходят только в чат Telegram\n" +
                   "• 💬 *Только MAX* — пуши приходят только в чат MAX\n" +
                   "• 🔄 *В оба мессенджера* — дублировать во все привязанные чаты\n\n" +
                   "💡 _Вы можете изменить этот выбор в любой момент в /settings._";

        var buttons = new List<List<BotButton>>
        {
            new() { BotButton.Callback($"{(ch == "telegram" ? "✅ " : "")}✈️ Только в Telegram", "cmd_set_notify_channel:telegram") },
            new() { BotButton.Callback($"{(ch == "max" ? "✅ " : "")}💬 Только в MAX", "cmd_set_notify_channel:max") },
            new() { BotButton.Callback($"{(ch == "all" || string.IsNullOrEmpty(ch) ? "✅ " : "")}🔄 В оба мессенджера", "cmd_set_notify_channel:all") },
            new() { BotButton.Callback("⚙️ Назад в настройки", "cmd_settings"), BotButton.Callback("🏠 Главное меню", "cmd_back_main") }
        };

        return BotScreen.Create(text, "banner_settings.png").WithButtons(buttons);
    }

    public async Task<BotCallbackResult> SetNotificationChannelPreferenceAsync(string userId, string channel, string platform = "telegram")
    {
        channel = channel.ToLowerInvariant().Trim();
        if (channel is not ("telegram" or "max" or "all"))
        {
            channel = "all";
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user != null)
        {
            user.NotificationChannel = channel;

            // Синхронизируем парный аккаунт
            var (hasBoth, peer) = await CheckDualPlatformRegistrationAsync(user);
            if (peer != null)
            {
                peer.NotificationChannel = channel;
            }

            await _db.SaveChangesAsync();
            await _userSyncService.SyncUserAcrossPlatformsAsync(user);
        }

        string alertText = channel switch
        {
            "telegram" => "Уведомления будут приходить только в Telegram ✈️",
            "max" => "Уведомления будут приходить только в MAX 💬",
            _ => "Уведомления будут приходить в оба мессенджера 🔄"
        };

        var screen = await BuildSettingsMenuScreenAsync(userId, platform);
        return BotCallbackResult.Ok(screen, alertText);
    }

    // ─── Справка ──────────────────────────────────────────────────────────────
    public BotScreen BuildHelpScreen()
    {
        var text = "ℹ️ *Справка по Mirage Diary*\n\n" +
                   "📋 *Основные команды бота:*\n" +
                   "• `/start` или `/menu` — Главное меню\n" +
                   "• `/schedule` `[дата]` — Расписание уроков (`/schedule завтра`, `/расписание`)\n" +
                   "• `/homework` `[дата]` — Домашнее задание (`/homework завтра`, `/дз`)\n" +
                   "• `/marks` — Оценки и средний балл за четверть (`/оценки`)\n" +
                   "• `/calc` — Калькулятор оценок и расчет пятерок (`/калькулятор`)\n" +
                   "• `/now` — Сколько времени до звонка и смены (`/звонок`)\n" +
                   "• `/alarm` — Умный будильник, напоминания и рюкзак (`/будильник`)\n" +
                   "• `/children` — Выбор ребенка для родителей (`/дети`)\n" +
                   "• `/profile` — Профиль ученика, класс и школа (`/профиль`)\n" +
                   "• `/settings` — Настройки уведомлений и аккаунта (`/настройки`)\n" +
                   "• `/notify_tg` / `/notify_max` / `/notify_all` — Выбор мессенджера для пушей\n" +
                   "• `/link` — Связать Telegram и MAX аккаунты (`/связать`)\n" +
                   "• `/grades_off` / `/grades_on` — Выключение / включение пушей об оценках\n" +
                   "• `/logout` — Безопасный выход из учетной записи (`/выйти`)\n" +
                   "• `/stop` — Полное удаление аккаунта и сессий\n" +
                   "• `/help` — Эта справка по всем командам (`/помощь`)\n\n" +
                   "🔮 *Симулятор оценок в /calc:* выберите предмет и нажмите «Если 2 / 3 / 4 / 5», чтобы узнать, на сколько упадёт или вырастет балл и сколько пятёрок нужно для компенсации.\n\n" +
                   "💡 _Все данные загружаются напрямую из ФГИС «Моя Школа» через защищенный шлюз Госуслуг._";

        var buttons = new List<List<BotButton>>
        {
            new()
            {
                BotButton.Callback("⚙️ Настройки", "cmd_settings"),
                BotButton.Callback("🏠 Главное меню", "cmd_back_main")
            }
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
        bool grades = user?.IsGradeWatcherEnabled ?? true;

        string schedTime = user?.ScheduleReminderTime ?? "07:30";
        string hwTime    = user?.HomeworkReminderTime ?? "17:00";
        string equipTime = user?.EquipmentReminderTime ?? "20:30";

        var text = "⏰ *Умный будильник*\n\n" +
                   $"• 🔔 Общий статус: {(master ? "`🟢 Активен`" : "`🔴 Выключен`")}\n" +
                   $"• 📊 Новые оценки: {(grades ? "`🟢 Вкл`" : "`🔴 Выкл`")}\n" +
                   $"• 📅 Расписание: {(sched ? $"`{schedTime}`" : "`Выкл`")}\n" +
                   $"• 📚 Домашка: {(hw ? $"`{hwTime}`" : "`Выкл`")}\n" +
                   $"• 🎒 Вещи и рюкзак: {(equip ? $"`{equipTime}`" : "`Выкл`")}";

        var buttons = new List<List<BotButton>>
        {
            new() { BotButton.Callback(master ? "🔔 Уведомления: 🟢 ВКЛЮЧЕНЫ" : "🔕 Уведомления: 🔴 ВЫКЛЮЧЕНЫ", "cmd_alarm_toggle_master") },
            new()
            {
                BotButton.Callback($"📊 Оценки: {(grades ? "🟢 ВКЛ" : "🔴 ВЫКЛ")}", "cmd_alarm_grades_toggle"),
                BotButton.Callback($"📅 Расписание: {(sched ? schedTime : "ВЫКЛ")}", "cmd_alarm_sched")
            },
            new()
            {
                BotButton.Callback($"📚 Домашка: {(hw ? hwTime : "ВЫКЛ")}", "cmd_alarm_hw"),
                BotButton.Callback($"🎒 Вещи: {(equip ? equipTime : "ВЫКЛ")}", "cmd_alarm_equip")
            },
            new()
            {
                BotButton.Callback("⚡ Проверить рюкзак сейчас", "cmd_alarm_check_now")
            },
            new()
            {
                BotButton.Callback("⬅️ Настройки", "cmd_settings"),
                BotButton.Callback("🏠 Главное меню", "cmd_back_main")
            }
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
        return BotCallbackResult.Ok(screen, user?.IsAlarmEnabled == true ? "Уведомления включены" : "Уведомления выключены");
    }

    public async Task<BotCallbackResult> ToggleGradeWatcherReminderAsync(string userId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user != null)
        {
            user.IsGradeWatcherEnabled = !user.IsGradeWatcherEnabled;
            await _db.SaveChangesAsync();
            await _userSyncService.SyncUserAcrossPlatformsAsync(user);
        }
        var screen = await BuildAlarmMenuScreenAsync(userId);
        return BotCallbackResult.Ok(screen, user?.IsGradeWatcherEnabled == true ? "Оповещения об оценках включены" : "Оповещения об оценках выключены");
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
            new() { BotButton.Callback(enabled ? "🔕 Выключить расписание" : "🔔 Включить расписание", "cmd_alarm_sched_toggle") },
            new()
            {
                BotButton.Callback("07:00" == time ? "✅ 07:00" : "07:00", "cmd_alarm_sched_time:07:00"),
                BotButton.Callback("07:30" == time ? "✅ 07:30" : "07:30", "cmd_alarm_sched_time:07:30"),
                BotButton.Callback("08:00" == time ? "✅ 08:00" : "08:00", "cmd_alarm_sched_time:08:00"),
                BotButton.Callback("20:00" == time ? "✅ 20:00" : "20:00", "cmd_alarm_sched_time:20:00")
            },
            new() { BotButton.Callback("✏️ Ввести другое время", "cmd_alarm_sched_custom") },
            new()
            {
                BotButton.Callback("⬅️ К будильнику", "cmd_alarm_menu"),
                BotButton.Callback("🏠 Главное меню", "cmd_back_main")
            }
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
            new() { BotButton.Callback(enabled ? "🔕 Выключить домашку" : "🔔 Включить домашку", "cmd_alarm_hw_toggle") },
            new()
            {
                BotButton.Callback("15:00" == time ? "✅ 15:00" : "15:00", "cmd_alarm_hw_time:15:00"),
                BotButton.Callback("16:00" == time ? "✅ 16:00" : "16:00", "cmd_alarm_hw_time:16:00"),
                BotButton.Callback("17:00" == time ? "✅ 17:00" : "17:00", "cmd_alarm_hw_time:17:00"),
                BotButton.Callback("18:00" == time ? "✅ 18:00" : "18:00", "cmd_alarm_hw_time:18:00"),
                BotButton.Callback("19:00" == time ? "✅ 19:00" : "19:00", "cmd_alarm_hw_time:19:00")
            },
            new() { BotButton.Callback("✏️ Ввести другое время", "cmd_alarm_hw_custom") },
            new()
            {
                BotButton.Callback("⬅️ К будильнику", "cmd_alarm_menu"),
                BotButton.Callback("🏠 Главное меню", "cmd_back_main")
            }
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
            new() { BotButton.Callback(enabled ? "🔕 Выключить напоминание" : "🔔 Включить напоминание", "cmd_alarm_equip_toggle") },
            new()
            {
                BotButton.Callback("19:00" == time ? "✅ 19:00" : "19:00", "cmd_alarm_equip_time:19:00"),
                BotButton.Callback("20:00" == time ? "✅ 20:00" : "20:00", "cmd_alarm_equip_time:20:00"),
                BotButton.Callback("20:30" == time ? "✅ 20:30" : "20:30", "cmd_alarm_equip_time:20:30"),
                BotButton.Callback("21:00" == time ? "✅ 21:00" : "21:00", "cmd_alarm_equip_time:21:00"),
                BotButton.Callback("21:30" == time ? "✅ 21:30" : "21:30", "cmd_alarm_equip_time:21:30")
            },
            new() { BotButton.Callback("✏️ Ввести другое время", "cmd_alarm_equip_custom") },
            new()
            {
                BotButton.Callback("⬅️ К будильнику", "cmd_alarm_menu"),
                BotButton.Callback("🏠 Главное меню", "cmd_back_main")
            }
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
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if ((user == null || string.IsNullOrEmpty(user.AccessToken)) && !string.IsNullOrEmpty(user?.OrgName))
        {
            var uploadedSchedule = await _db.Schedules.FirstOrDefaultAsync(s => s.OrgName == user.OrgName);
            if (uploadedSchedule != null && !string.IsNullOrEmpty(uploadedSchedule.FileId))
            {
                string safeOrg = TelegramMarkdownEscaper.Escape(user.OrgName);
                string scheduleCaption = $"📅 Расписание — *{safeOrg}*\n_(загружено администрацией школы)_";
                var uploadedNavButtons = new List<List<BotButton>>
                {
                    new()
                    {
                        BotButton.Callback("🔄 Обновить", "cmd_schedule_refresh"),
                        BotButton.Callback("🏠 Главное меню", "cmd_back_main")
                    }
                };
                return new BotScreen
                {
                    Text = scheduleCaption,
                    FileId = uploadedSchedule.FileId,
                    FileType = uploadedSchedule.FileType,
                    Buttons = uploadedNavButtons
                };
            }
        }

        var scheduleText = await _diaryService.GetScheduleStringAsync(userId, dateArg, forceRefresh);

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
        string curDateStr = currentBaseDate.ToString("yyyy-MM-dd");

        bool isToday = (curDateStr == todayStr);
        string todayLabel = isToday ? "• Сегодня •" : "Сегодня";

        var buttons = new List<List<BotButton>>
        {
            new()
            {
                BotButton.Callback("⬅️ Вчера", $"cmd_schedule:{prevDate}"),
                BotButton.Callback(todayLabel, $"cmd_schedule:{todayStr}"),
                BotButton.Callback("Завтра ➡️", $"cmd_schedule:{nextDate}")
            },
            new()
            {
                BotButton.Callback("🔄 Обновить", $"cmd_schedule_refresh:{curDateStr}"),
                BotButton.Callback("🏠 Главное меню", "cmd_back_main")
            }
        };

        return BotScreen.Create(scheduleText, "banner_schedule.png").WithButtons(buttons);
    }

    // ─── Домашнее задание ─────────────────────────────────────────────────────
    public async Task<BotScreen> BuildHomeworkScreenAsync(string userId, string? dateArg = null, bool forceRefresh = false)
    {
        var hwText = await _diaryService.GetHomeworkStringAsync(userId, dateArg, forceRefresh);

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
        string curDateStr = currentBaseDate.ToString("yyyy-MM-dd");

        bool isToday = (curDateStr == todayStr);
        string todayLabel = isToday ? "• Сегодня •" : "Сегодня";

        var buttons = new List<List<BotButton>>
        {
            new()
            {
                BotButton.Callback("⬅️ Вчера", $"cmd_homework:{prevDate}"),
                BotButton.Callback(todayLabel, $"cmd_homework:{todayStr}"),
                BotButton.Callback("Завтра ➡️", $"cmd_homework:{nextDate}")
            },
            new()
            {
                BotButton.Callback("🔄 Обновить", $"cmd_homework_refresh:{curDateStr}"),
                BotButton.Callback("🏠 Главное меню", "cmd_back_main")
            }
        };

        return BotScreen.Create(hwText, "banner_homework.png").WithButtons(buttons);
    }

    // ─── Оценки и средний балл ────────────────────────────────────────────────
    public async Task<BotScreen> BuildMarksScreenAsync(string userId, int? quarter = null, bool forceRefresh = false, string platform = "telegram")
    {
        int currentQ = DiaryInteractionService.GetCurrentQuarter();
        int activeQ = quarter ?? currentQ;
        var marksText = await _diaryService.GetMarksStringAsync(userId, activeQ, forceRefresh);

        var quarterRow = new List<BotButton>();
        for (int q = 1; q <= 4; q++)
        {
            string label = (q == activeQ) ? $"• {q} четв •" : $"{q} четв";
            quarterRow.Add(BotButton.Callback(label, $"cmd_marks_q:{q}"));
        }
        string allLabel = (activeQ == 0) ? "• Все •" : "Все";
        quarterRow.Add(BotButton.Callback(allLabel, "cmd_marks_q:0"));

        var buttons = new List<List<BotButton>>
        {
            quarterRow,
            new()
            {
                BotButton.Callback("🎯 Калькулятор оценок", "cmd_calc"),
                BotButton.Callback("🔄 Обновить", $"cmd_marks_refresh:{activeQ}")
            },
            new()
            {
                BotButton.Callback("🏠 Главное меню", "cmd_back_main")
            }
        };

        if (marksText.StartsWith("🔒") || marksText.StartsWith("❌"))
        {
            if (platform == "telegram")
                buttons.Insert(0, new List<BotButton> { BotButton.WebApp("🔐 Войти через Госуслуги", BuildMiniAppUrl(userId, null, force: true)) });
            else
            {
                var maxBotApp = _config["MaxBot:Username"] ?? _config["MaxBotUsername"] ?? "se13960436_bot";
                buttons.Insert(0, new List<BotButton> { BotButton.MaxApp("🔐 Войти через Госуслуги", maxBotApp, userId) });
            }
        }

        return BotScreen.Create(marksText, "banner_marks.png").WithButtons(buttons);
    }

    public async Task<BotScreen> BuildMarksScreenAsync(string userId, bool forceRefresh)
    {
        return await BuildMarksScreenAsync(userId, quarter: null, forceRefresh);
    }

    // ─── Калькулятор оценок ───────────────────────────────────────────────────
    public async Task<BotScreen> BuildGradeCalculatorSubjectsScreenAsync(string userId, bool forceRefresh = false, string platform = "telegram")
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        var isAuthorized = user != null && !string.IsNullOrEmpty(user.AccessToken);

        if (!isAuthorized)
        {
            var authButtons = new List<List<BotButton>>();
            if (platform == "telegram")
                authButtons.Add(new List<BotButton> { BotButton.WebApp("🔐 Войти через Госуслуги", BuildMiniAppUrl(userId, user?.FirstName)) });
            else
            {
                var maxBotApp = _config["MaxBot:Username"] ?? _config["MaxBotUsername"] ?? "se13960436_bot";
                authButtons.Add(new List<BotButton> { BotButton.MaxApp("🔐 Войти через Госуслуги", maxBotApp, userId) });
            }
            authButtons.Add(new List<BotButton> { BotButton.Callback("🏠 Главное меню", "cmd_back_main") });
            return BotScreen.Create("🎯 *Калькулятор оценок*\n\n🔒 Для расчета оценок необходимо войти в аккаунт Госуслуг.", "banner_marks.png").WithButtons(authButtons);
        }

        List<DiaryInteractionService.SubjectGradeSummary>? subjects = null;
        try
        {
            subjects = await _diaryService.GetSubjectGradesSummaryAsync(userId, forceRefresh);
        }
        catch (DiarySessionExpiredException ex)
        {
            var expiredButtons = new List<List<BotButton>>();
            if (platform == "telegram")
                expiredButtons.Add(new List<BotButton> { BotButton.WebApp("🔐 Войти заново", BuildMiniAppUrl(userId, user?.FirstName, force: true)) });
            else
            {
                var maxBotApp = _config["MaxBot:Username"] ?? _config["MaxBotUsername"] ?? "se13960436_bot";
                expiredButtons.Add(new List<BotButton> { BotButton.MaxApp("🔐 Войти заново", maxBotApp, userId) });
            }
            expiredButtons.Add(new List<BotButton> { BotButton.Callback("🏠 Главное меню", "cmd_back_main") });
            return BotScreen.Create($"🎯 *Калькулятор оценок*\n\n🔒 {TelegramMarkdownEscaper.Escape(ex.Message)}", "banner_marks.png").WithButtons(expiredButtons);
        }
        catch (Exception ex)
        {
            var errButtons = new List<List<BotButton>>
            {
                new() { BotButton.Callback("🔄 Попробовать снова", "cmd_calc_refresh") },
                new() { BotButton.Callback("🏠 Главное меню", "cmd_back_main") }
            };
            return BotScreen.Create($"🎯 *Калькулятор оценок*\n\n⚠️ Не удалось загрузить оценки из дневника: {TelegramMarkdownEscaper.Escape(ex.Message)}", "banner_marks.png").WithButtons(errButtons);
        }

        if (subjects == null || subjects.Count == 0)
        {
            var emptyText = "🎯 *Калькулятор оценок*\n\n" +
                            "В электронном журнале пока нет выставленных оценок.\n\n" +
                            "💡 _Как только учителя выставят первые оценки в четверти, здесь появятся ваши предметы для расчета необходимого числа пятерок и четверок._";
            var emptyButtons = new List<List<BotButton>>
            {
                new() { BotButton.Callback("🔄 Обновить данные", "cmd_calc_refresh") },
                new() { BotButton.Callback("🏠 Главное меню", "cmd_back_main") }
            };
            return BotScreen.Create(emptyText, "banner_marks.png").WithButtons(emptyButtons);
        }

        var text = "🎯 *Калькулятор оценок*\n\nВыберите предмет для расчета или запустите симулятор:";
        var buttons = new List<List<BotButton>>();

        if (platform == "telegram")
        {
            buttons.Add(new List<BotButton>
            {
                BotButton.WebApp("🔮 Интерактивный симулятор в Mini App", BuildMiniAppUrl(userId, user?.FirstName, mode: "simulator"))
            });
        }
        else
        {
            var maxBotApp = _config["MaxBot:Username"] ?? _config["MaxBotUsername"] ?? "se13960436_bot";
            buttons.Add(new List<BotButton>
            {
                BotButton.MaxApp("🔮 Интерактивный симулятор в Mini App", maxBotApp, userId)
            });
        }

        for (int i = 0; i < subjects.Count; i += 2)
        {
            var row = new List<BotButton>();
            var s1 = subjects[i];
            row.Add(BotButton.Callback($"{s1.StatusEmoji} {Truncate(s1.SubjectName, 14)}", $"cmd_calc_subj:{i}"));

            if (i + 1 < subjects.Count)
            {
                var s2 = subjects[i + 1];
                row.Add(BotButton.Callback($"{s2.StatusEmoji} {Truncate(s2.SubjectName, 14)}", $"cmd_calc_subj:{i + 1}"));
            }
            buttons.Add(row);
        }

        buttons.Add(new List<BotButton>
        {
            BotButton.Callback("🔄 Обновить", "cmd_calc_refresh"),
            BotButton.Callback("🏠 Главное меню", "cmd_back_main")
        });
        return BotScreen.Create(text, "banner_marks.png").WithButtons(buttons);
    }

    public async Task<BotScreen> BuildSubjectTargetOptionsScreenAsync(string userId, int subjectIndex, string platform = "telegram")
    {
        List<DiaryInteractionService.SubjectGradeSummary>? subjects = null;
        try
        {
            subjects = await _diaryService.GetSubjectGradesSummaryAsync(userId);
        }
        catch
        {
            return await BuildGradeCalculatorSubjectsScreenAsync(userId, platform: platform);
        }

        if (subjects == null || subjectIndex < 0 || subjectIndex >= subjects.Count)
        {
            return await BuildGradeCalculatorSubjectsScreenAsync(userId, platform: platform);
        }

        var s = subjects[subjectIndex];
        string safeSubject = TelegramMarkdownEscaper.Escape(s.SubjectName);
        string gpaStr = s.TotalWeight > 0 ? $"{s.AverageGpa:F2}" : "—";

        var text = $"🎯 *Калькулятор оценок*\n\n" +
                   $"📚 *Предмет:* {safeSubject}\n" +
                   $"📊 *Текущий балл:* `{gpaStr}`\n" +
                   $"🔮 *Прогноз четверти:* *{s.FinalForecast}*\n\n" +
                   "Выберите цель на четверть или смоделируйте невыставленную отметку:";

        var buttons = new List<List<BotButton>>();

        if (platform == "telegram")
        {
            buttons.Add(new List<BotButton>
            {
                BotButton.WebApp("🔮 Симулятор оценок в Mini App", BuildMiniAppUrl(userId, null, mode: "simulator"))
            });
        }
        else
        {
            var maxBotApp = _config["MaxBot:Username"] ?? _config["MaxBotUsername"] ?? "se13960436_bot";
            buttons.Add(new List<BotButton>
            {
                BotButton.MaxApp("🔮 Симулятор оценок в Mini App", maxBotApp, userId)
            });
        }

        buttons.Add(new List<BotButton>
        {
            BotButton.Callback("🟢 Цель 5", $"cmd_calc_target:{subjectIndex}:5"),
            BotButton.Callback("🟡 Цель 4", $"cmd_calc_target:{subjectIndex}:4"),
            BotButton.Callback("🔴 Цель 3", $"cmd_calc_target:{subjectIndex}:3")
        });

        buttons.Add(new List<BotButton>
        {
            BotButton.Callback("🔻 Что если 2?", $"cmd_calc_sim:{subjectIndex}:2:1:1"),
            BotButton.Callback("🔺 Что если 5?", $"cmd_calc_sim:{subjectIndex}:5:1:1")
        });

        buttons.Add(new List<BotButton>
        {
            BotButton.Callback("⬅️ Выбрать другой предмет", "cmd_calc"),
            BotButton.Callback("🏠 Главное меню", "cmd_back_main")
        });

        return BotScreen.Create(text, "banner_marks.png").WithButtons(buttons);
    }

    public async Task<BotScreen> BuildTargetCalculationResultScreenAsync(string userId, int subjectIndex, int targetGrade, string platform = "telegram")
    {
        List<DiaryInteractionService.SubjectGradeSummary>? subjects = null;
        try
        {
            subjects = await _diaryService.GetSubjectGradesSummaryAsync(userId);
        }
        catch
        {
            return await BuildGradeCalculatorSubjectsScreenAsync(userId, platform: platform);
        }

        if (subjects == null || subjectIndex < 0 || subjectIndex >= subjects.Count)
        {
            return await BuildGradeCalculatorSubjectsScreenAsync(userId, platform: platform);
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
                BotButton.Callback("⬅️ К целям предмета", $"cmd_calc_subj:{subjectIndex}"),
                BotButton.Callback("📚 К предметам", "cmd_calc")
            },
            new()
            {
                BotButton.Callback("🏠 Главное меню", "cmd_back_main")
            }
        };

        return BotScreen.Create(text, "banner_marks.png").WithButtons(buttons);
    }

    public async Task<BotScreen> BuildHypotheticalGradeScreenAsync(
        string userId,
        int subjectIndex,
        int grade = 2,
        int weight = 1,
        int count = 1,
        string platform = "telegram")
    {
        List<DiaryInteractionService.SubjectGradeSummary>? subjects = null;
        try
        {
            subjects = await _diaryService.GetSubjectGradesSummaryAsync(userId);
        }
        catch
        {
            return await BuildGradeCalculatorSubjectsScreenAsync(userId, platform: platform);
        }

        if (subjects == null || subjectIndex < 0 || subjectIndex >= subjects.Count)
        {
            return await BuildGradeCalculatorSubjectsScreenAsync(userId, platform: platform);
        }

        var s = subjects[subjectIndex];
        string safeSubject = TelegramMarkdownEscaper.Escape(s.SubjectName);

        var sim = GradeCalculator.SimulateUnpostedGrade(
            subjectIndex,
            s.SubjectName,
            s.WeightedSum,
            s.TotalWeight,
            grade,
            weight,
            count
        );

        var ci = System.Globalization.CultureInfo.InvariantCulture;
        string weightTitle = weight switch
        {
            1 => "ответ на уроке / ДЗ",
            2 => "контрольная работа",
            3 => "ВПР / экзамен",
            _ => "работа"
        };
        string gradeEmoji = grade switch
        {
            5 => "🟢",
            4 => "🟡",
            3 => "🟠",
            2 => "🔴",
            _ => "⚪"
        };
        string countText = count > 1 ? $" × {count} шт." : "";

        string deltaSign = sim.Delta >= 0 ? "+" : "";
        string deltaIcon = sim.Delta > 0 ? "📈" : (sim.Delta < 0 ? "📉" : "—");
        string deltaStr = $"{deltaSign}{sim.Delta.ToString("F2", ci)} {deltaIcon}";

        string forecastChangeStr;
        if (sim.CurrentForecast != sim.NewForecast)
        {
            string icon = sim.ForecastDropped ? "⚠️" : "🎉";
            forecastChangeStr = $"`{sim.CurrentForecast}` ➡️ *{sim.NewForecast}* {icon}";
        }
        else
        {
            forecastChangeStr = $"*{sim.NewForecast}* _(без изменений)_";
        }

        string impactText;
        if (sim.ForecastDropped)
        {
            impactText = $"⚠️ *Внимание!* Из-за этой отметки итоговая оценка за четверть упадёт с *{sim.CurrentForecast}* до *{sim.NewForecast}*!";
        }
        else if (sim.Delta < 0)
        {
            impactText = $"📉 *Средний балл просядет*, но четвертная оценка *{sim.NewForecast}* пока удерживается.";
        }
        else
        {
            impactText = $"🎉 *Балл вырастет!* Новая отметка укрепит четвертную оценку.";
        }

        string recoverySection = "";
        if (sim.NeededFivesW1ToRecover > 0)
        {
            int targetGrade = sim.CurrentForecast > 0 ? sim.CurrentForecast : (grade >= 4 ? grade : 4);
            double th = GradeCalculator.GetTargetThreshold(targetGrade);
            recoverySection = $"\n\n🎯 *Как удержать оценку «{targetGrade}» (порог ≥ {th.ToString("F2", ci)}):*\n" +
                              $"Чтобы компенсировать балл, потребуется получить:\n" +
                              $"• 🟢 *{sim.NeededFivesW1ToRecover}* пятерок за ответы на уроках (вес 1)\n" +
                              $"• 🟢 *{sim.NeededFivesW2ToRecover}* пятёрки за контрольные (вес 2)";
        }
        else if (sim.Delta < 0)
        {
            recoverySection = "\n\n✅ *Запас прочности:* средний балл останется в зоне текущей оценки.";
        }

        var text = $"🔮 *Симулятор невыставленной оценки*\n\n" +
                   $"📚 *Предмет:* {safeSubject}\n" +
                   $"• *Текущий балл:* `{sim.CurrentGpa.ToString("F2", ci)}` (прогноз: *{sim.CurrentForecast}*)\n" +
                   $"• *Ожидаемая оценка:* {gradeEmoji} *{grade}*{countText} (вес *{weight}* — {weightTitle})\n\n" +
                   $"📊 *Результат после выставления:*\n" +
                   $"• *Новый средний балл:* `{sim.NewGpa.ToString("F2", ci)}` ({deltaStr})\n" +
                   $"• *Новый прогноз четверти:* {forecastChangeStr}\n\n" +
                   $"{impactText}" +
                   $"{recoverySection}";

        var buttons = new List<List<BotButton>>();

        if (platform == "telegram")
        {
            buttons.Add(new List<BotButton>
            {
                BotButton.WebApp("🔮 Открыть симулятор в Mini App", BuildMiniAppUrl(userId, null, mode: "simulator"))
            });
        }
        else
        {
            var maxBotApp = _config["MaxBot:Username"] ?? _config["MaxBotUsername"] ?? "se13960436_bot";
            buttons.Add(new List<BotButton>
            {
                BotButton.MaxApp("🔮 Открыть симулятор в Mini App", maxBotApp, userId)
            });
        }

        // Ряд 1: выбор оценки
        buttons.Add(new List<BotButton>
        {
            BotButton.Callback($"{(grade == 2 ? "✓ " : "")}🔴 2", $"cmd_calc_sim:{subjectIndex}:2:{weight}:{count}"),
            BotButton.Callback($"{(grade == 3 ? "✓ " : "")}🟠 3", $"cmd_calc_sim:{subjectIndex}:3:{weight}:{count}"),
            BotButton.Callback($"{(grade == 4 ? "✓ " : "")}🟡 4", $"cmd_calc_sim:{subjectIndex}:4:{weight}:{count}"),
            BotButton.Callback($"{(grade == 5 ? "✓ " : "")}🟢 5", $"cmd_calc_sim:{subjectIndex}:5:{weight}:{count}")
        });

        // Ряд 2: выбор веса (1, 2, 3)
        buttons.Add(new List<BotButton>
        {
            BotButton.Callback($"{(weight == 1 ? "✓ " : "")}Урок (1)", $"cmd_calc_sim:{subjectIndex}:{grade}:1:{count}"),
            BotButton.Callback($"{(weight == 2 ? "✓ " : "")}КР (2)", $"cmd_calc_sim:{subjectIndex}:{grade}:2:{count}"),
            BotButton.Callback($"{(weight == 3 ? "✓ " : "")}ВПР (3)", $"cmd_calc_sim:{subjectIndex}:{grade}:3:{count}")
        });

        // Ряд 3: навигация
        buttons.Add(new List<BotButton>
        {
            BotButton.Callback("⬅️ К предмету", $"cmd_calc_subj:{subjectIndex}"),
            BotButton.Callback("🏠 Главное меню", "cmd_back_main")
        });

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
                BotButton.Callback("⏰ Меню будильника", "cmd_alarm_menu"),
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
        if (user?.Role == "Parent" || !string.IsNullOrEmpty(user?.SelectedStudentId))
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

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user != null)
        {
            await _db.Entry(user).ReloadAsync();
        }
        var screenText = "👨‍👧 *Выберите ребенка для просмотра дневника:*";
        var screenButtons = new List<List<BotButton>>();

        foreach (var c in children)
        {
            string name = $"{c.StudentLastName} {c.StudentFirstName}".Trim();
            if (string.IsNullOrEmpty(name)) name = "Ученик";
            bool isCurrent = !string.IsNullOrEmpty(user?.SelectedStudentId) && user.SelectedStudentId == c.StudentId;
            string label = isCurrent ? $"✅ {name} (выбран)" : $"👶 {name}";
            screenButtons.Add(new List<BotButton> { BotButton.Callback(label, $"cmd_select_child:{c.StudentId}") });
        }

        screenButtons.Add(new List<BotButton>
        {
            BotButton.Callback("👤 Профиль", "cmd_profile"),
            BotButton.Callback("🏠 Главное меню", "cmd_back_main")
        });
        return BotScreen.Create(screenText, "banner_profile.png").WithButtons(screenButtons);
    }

    public async Task<BotCallbackResult> SelectChildAsync(string userId, string studentId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user != null && user.SelectedStudentId == studentId)
        {
            var currentScreen = await BuildChildrenMenuScreenAsync(userId);
            return BotCallbackResult.Ok(currentScreen, "✅ Ребёнок уже выбран");
        }

        bool ok = await _diaryService.SelectChildAsync(userId, studentId);
        if (user != null)
        {
            await _db.Entry(user).ReloadAsync();
        }
        var screen = await BuildChildrenMenuScreenAsync(userId);
        return BotCallbackResult.Ok(screen, ok ? "✅ Ребёнок успешно выбран!" : "❌ Ошибка выбора ребёнка");
    }

    // ─── До звонка ────────────────────────────────────────────────────────────
    public async Task<BotScreen> BuildNowScreenAsync(string userId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        int shift = user?.Shift ?? 1;
        var localTime = DateTime.UtcNow.AddHours(user?.UtcOffsetHours ?? 5);

        var status = _bellSchedule.GetCurrentStatus(localTime, user?.OrgName, shift);
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

        return BotScreen.Create(text, "banner_start.png").WithButtons(buttons);
    }

    public async Task AcceptPrivacyPolicyAsync(string userId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user != null)
        {
            user.HasAcceptedPrivacyPolicy = true;
            user.PrivacyPolicyAcceptedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            await _userSyncService.SyncUserAcrossPlatformsAsync(user);
        }
    }

    // ─── Онбординг: опрос про смену и умный будильник ──────────────────────────
    public BotScreen BuildOnboardingShiftScreen(string userId)
    {
        var text = "🏫 *Настройка бота: Смена обучения (1/2)*\n\n" +
                   "В какую смену вы обычно учитесь?\n" +
                   "Это необходимо для своевременной отправки утреннего расписания и напоминаний:\n\n" +
                   "• ☀️ *1-я смена* — уроки с утра (обычно 08:00 – 14:00)\n" +
                   "• 🌙 *2-я смена* — уроки днем (обычно 13:00 – 19:00)\n\n" +
                   "_Вы сможете изменить смену в любой момент в настройках будильника._";

        var buttons = new List<List<BotButton>>
        {
            new()
            {
                BotButton.Callback("☀️ 1-я смена", "cmd_onboard_shift:1"),
                BotButton.Callback("🌙 2-я смена", "cmd_onboard_shift:2")
            },
            new()
            {
                BotButton.Callback("⏭ Пропустить", "cmd_onboard_shift:skip")
            }
        };

        return BotScreen.Create(text, "banner_start.png").WithButtons(buttons);
    }

    public BotScreen BuildOnboardingAlarmScreen(string userId, int shift)
    {
        string schedTime = shift == 2 ? "12:00" : "07:30";
        string hwTime    = shift == 2 ? "19:00" : "17:00";
        string equipTime = shift == 2 ? "21:00" : "20:30";

        var text = "⏰ *Настройка бота: Умный будильник (2/2)*\n\n" +
                   "Mirage Diary может автоматически помогать вам в течение учебного дня:\n\n" +
                   $"• 🔔 *Утреннее расписание ({schedTime}):* список уроков, кабинеты и время звонков перед выходом в школу.\n" +
                   $"• 📚 *Домашнее задание ({hwTime}):* свежие задания на завтрашний день.\n" +
                   $"• 🎒 *Сбор рюкзака ({equipTime}):* чекер предметов и экипировки (форма, тетради, циркуль).\n\n" +
                   "Выберите подходящий режим работы:";

        var buttons = new List<List<BotButton>>
        {
            new() { BotButton.Callback("⚡ Включить всё (рекомендуется)", "cmd_onboard_alarm:all") },
            new() { BotButton.Callback("🔔 Только расписание и домашка", "cmd_onboard_alarm:essential") },
            new() { BotButton.Callback("🔕 Отключить напоминания", "cmd_onboard_alarm:off") }
        };

        return BotScreen.Create(text, "banner_alarm.png").WithButtons(buttons);
    }

    public async Task<BotScreen> BuildOnboardingFinishScreenAsync(string userId, string platform)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        var text = "🎉 *Настройка успешно завершена!*\n\n" +
                   "Ваш умный будильник и параметры бота сконфигурированы.\n\n" +
                   "Остался последний шаг — войдите через Госуслуги, чтобы бот получил доступ к вашему электронному дневнику и оценкам.";

        var buttons = new List<List<BotButton>>();

        if (platform.Equals("max", StringComparison.OrdinalIgnoreCase))
        {
            var maxBotApp = _config["MaxBot:Username"] ?? _config["MaxBotUsername"] ?? "se13960436_bot";
            buttons.Add(new List<BotButton> { BotButton.MaxApp("🔐 Войти через Госуслуги", maxBotApp, userId) });
        }
        else
        {
            buttons.Add(new List<BotButton> { BotButton.WebApp("🔐 Войти через Госуслуги", BuildMiniAppUrl(userId, user?.FirstName)) });
        }

        buttons.Add(new List<BotButton> { BotButton.Callback("🏠 Перейти в Главное меню", "cmd_back_main") });

        return BotScreen.Create(text, "banner_menu.png").WithButtons(buttons);
    }

    public static void ApplyOnboardingAlarmPreset(UserEntity user, string preset)
    {
        if (preset == "all")
        {
            user.IsAlarmEnabled = true;
            user.IsScheduleReminderEnabled = true;
            user.IsHomeworkReminderEnabled = true;
            user.IsEquipmentReminderEnabled = true;
            if (user.Shift == 2)
            {
                user.ScheduleReminderTime = "12:00";
                user.HomeworkReminderTime = "19:00";
                user.EquipmentReminderTime = "21:00";
            }
            else
            {
                user.ScheduleReminderTime = "07:30";
                user.HomeworkReminderTime = "17:00";
                user.EquipmentReminderTime = "20:30";
            }
        }
        else if (preset == "essential")
        {
            user.IsAlarmEnabled = true;
            user.IsScheduleReminderEnabled = true;
            user.IsHomeworkReminderEnabled = true;
            user.IsEquipmentReminderEnabled = false;
            if (user.Shift == 2)
            {
                user.ScheduleReminderTime = "12:00";
                user.HomeworkReminderTime = "19:00";
            }
            else
            {
                user.ScheduleReminderTime = "07:30";
                user.HomeworkReminderTime = "17:00";
            }
        }
        else // "off"
        {
            user.IsAlarmEnabled = false;
            user.IsScheduleReminderEnabled = false;
            user.IsHomeworkReminderEnabled = false;
            user.IsEquipmentReminderEnabled = false;
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
                BotButton.Callback("❌ Отмена", "cmd_settings")
            }
        };

        return BotScreen.Create(text, "banner_settings.png").WithButtons(buttons);
    }

    public async Task<BotScreen> ExecuteDeleteAccountAsync(string userId, IBotPlatformAdapter? adapter = null, long? chatId = null)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user != null)
        {
            if (!string.IsNullOrEmpty(user.LinkedUserId))
            {
                var peer = await _db.Users.FirstOrDefaultAsync(u => u.Id == user.LinkedUserId);
                if (peer != null && peer.LinkedUserId == user.Id)
                {
                    peer.LinkedUserId = "";
                }
            }
            _db.Users.Remove(user);
            await _db.SaveChangesAsync();
            await _tokenStore.RevokeTokensAsync(userId);
        }
        _captchaService.ClearCaptcha(userId);

        if (adapter != null && chatId.HasValue)
        {
            await adapter.DeleteChatHistoryAsync(chatId.Value);
        }

        var text = "✅ *Ваш аккаунт и персональные данные полностью удалены.*\n\nСпасибо, что пользовались Mirage Diary!";
        var buttons = new List<List<BotButton>>
        {
            new() { BotButton.Callback("🏠 В начало", "cmd_back_main") }
        };

        return BotScreen.Create(text, "banner_menu.png").WithButtons(buttons);
    }

    public async Task HandleUserStopAsync(IBotPlatformAdapter adapter, long chatId, string userId)
    {
        _stateService.ClearState(chatId);
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user != null)
        {
            if (!string.IsNullOrEmpty(user.LinkedUserId))
            {
                var peer = await _db.Users.FirstOrDefaultAsync(u => u.Id == user.LinkedUserId);
                if (peer != null && peer.LinkedUserId == user.Id)
                {
                    peer.LinkedUserId = "";
                }
            }
            _db.Users.Remove(user);
            await _db.SaveChangesAsync();
            await _tokenStore.RevokeTokensAsync(userId);
        }
        _captchaService.ClearCaptcha(userId);
        await adapter.DeleteChatHistoryAsync(chatId);
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

    // ─── Админ-панель и управление ───────────────────────────────────────────
    public async Task<BotScreen> BuildAdminPanelScreenAsync(string userId, string platform = "telegram")
    {
        var isMaintenance = _maintenanceService.IsMaintenanceModeActive();
        var userCount = await _db.Users.CountAsync();
        var htCount = await _db.Users.CountAsync(u => u.Role == "HeadTeacher");
        var adminKey = Environment.GetEnvironmentVariable("ADMIN_KEY") ?? _config["Security:AdminKey"] ?? "";
        var baseUrl = (_config["MiniAppUrl"] ?? "https://miragediary.space").TrimEnd('/');
        var adminWebUrl = $"{baseUrl}/admin?key={Uri.EscapeDataString(adminKey)}";

        var text = $"🛡 *Админ-панель*\n\n" +
                   $"👥 Пользователей в базе: *{userCount}*\n" +
                   $"🏫 Завучей: *{htCount}*\n" +
                   $"🔧 Тех. работы: *{(isMaintenance ? "Включены" : "Выключены")}*\n\n" +
                   $"🔑 *Ключ администратора:*\n`{adminKey}`\n\n" +
                   $"_Нажмите кнопку ниже для входа в веб-консоль:_";

        var buttons = new List<List<BotButton>>
        {
            new() { BotButton.Link("🌐 Войти в админку на сайте", adminWebUrl) },
            new() { BotButton.Callback("🏫 Управление завучами", "cmd_admin_headteachers") },
            new() { BotButton.Callback(isMaintenance ? "🟢 Выключить тех. работы" : "🔧 Включить тех. работы", "cmd_admin_maintenance") },
            new()
            {
                BotButton.Callback("📢 Рассылка", "cmd_admin_broadcast"),
                BotButton.Callback("📊 Статистика", "cmd_admin_stats")
            },
            new() { BotButton.Callback("🏠 Главное меню", "cmd_back_main") }
        };

        return BotScreen.Create(text, "banner_admin.png").WithButtons(buttons);
    }

    public async Task<BotScreen> BuildHeadTeachersMenuScreenAsync(string userId)
    {
        var headTeachers = await _db.Users
            .Where(u => u.Role == "HeadTeacher")
            .ToListAsync();

        var text = "🏫 *Управление завучами*\n\n";
        if (headTeachers.Count == 0)
            text += "Завучей пока нет.";
        else
            text += string.Join("\n", headTeachers.Select(ht =>
                $"• `{ht.Id}` — {(string.IsNullOrEmpty(ht.OrgName) ? "_без школы_" : ht.OrgName)}"));

        var buttons = new List<List<BotButton>>
        {
            new() { BotButton.Callback("➕ Добавить завуча", "cmd_admin_add_ht") }
        };

        foreach (var ht in headTeachers)
        {
            var shortId = ht.Id.Length > 8 ? ht.Id[..8] + "…" : ht.Id;
            buttons.Add(new()
            {
                BotButton.Callback($"🗑 Удалить {shortId} ({ht.OrgName})", $"cmd_admin_remove_ht:{ht.Id}")
            });
        }

        buttons.Add(new() { BotButton.Callback("⬅️ Назад", "cmd_back_admin") });

        return BotScreen.Create(text, "banner_admin.png").WithButtons(buttons);
    }

    public async Task AddHeadTeacherAsync(IBotPlatformAdapter adapter, long adminChatId, string targetInput, string orgName)
    {
        var role = await _roleService.GetUserRoleAsync(adminChatId.ToString());
        if (role != "Admin") return;

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
        await adapter.SendTextMessageAsync(adminChatId,
            $"✅ Пользователь {displayTarget} назначен завучем школы *{safeOrg}*.\n\nВозвращайтесь в меню: /menu");
    }

    public async Task RemoveHeadTeacherAsync(IBotPlatformAdapter adapter, long chatId, string? messageId, string adminUserId, string htId)
    {
        var role = await _roleService.GetUserRoleAsync(adminUserId);
        if (role != "Admin") return;

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == htId);
        if (user != null)
        {
            user.Role = "Student";
            user.OrgName = "";
            await _db.SaveChangesAsync();
        }

        await adapter.RenderScreenAsync(chatId, messageId, await BuildHeadTeachersMenuScreenAsync(adminUserId));
    }

    public BotScreen BuildMaintenanceMenuScreenAsync(string userId)
    {
        var isMaintenance = _maintenanceService.IsMaintenanceModeActive();
        var text = $"🔧 *Технические работы*\n\n" +
                   $"Текущий статус: *{(isMaintenance ? "Включены 🔴" : "Выключены 🟢")}*\n\n" +
                   "При включении пользователи будут получать сообщение о тех. работах вместо ответа бота.";

        var buttons = new List<List<BotButton>>
        {
            new()
            {
                BotButton.Callback("🟢 Выключить", "cmd_admin_maintenance_off"),
                BotButton.Callback("🔴 Включить (30 мин)", "cmd_admin_maintenance_on")
            },
            new() { BotButton.Callback("⬅️ Назад", "cmd_back_admin") }
        };

        return BotScreen.Create(text, "banner_admin.png").WithButtons(buttons);
    }

    public async Task ToggleMaintenanceAsync(IBotPlatformAdapter adapter, long chatId, string? messageId, string adminUserId, bool enable)
    {
        var role = await _roleService.GetUserRoleAsync(adminUserId);
        if (role != "Admin") return;

        if (enable)
            _maintenanceService.ActivateMaintenanceMode(30);
        else
            _maintenanceService.DeactivateMaintenanceMode();

        await adapter.RenderScreenAsync(chatId, messageId, await BuildAdminPanelScreenAsync(adminUserId, adapter.Platform));
    }

    public async Task<BotScreen> BuildStatsScreenAsync(string userId)
    {
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

        var buttons = new List<List<BotButton>>
        {
            new() { BotButton.Callback("⬅️ Назад", "cmd_back_admin") }
        };

        return BotScreen.Create(text, "banner_admin.png").WithButtons(buttons);
    }

    public async Task DoBroadcastAsync(IBotPlatformAdapter adapter, long adminChatId, string broadcastText)
    {
        var role = await _roleService.GetUserRoleAsync(adminChatId.ToString());
        if (role != "Admin") return;

        var users = await _db.Users.Where(u => u.AccessToken != "").ToListAsync();
        int sent = 0, failed = 0;

        await adapter.SendTextMessageAsync(adminChatId,
            $"📢 Начинаю рассылку для *{users.Count}* пользователей...");

        foreach (var user in users)
        {
            string idStr = user.Id.StartsWith("max_") ? user.Id["max_".Length..] : user.Id;
            if (!long.TryParse(idStr, out long targetChatId)) continue;

            try
            {
                await adapter.SendTextMessageAsync(targetChatId,
                    $"📢 *Сообщение от администратора:*\n\n{broadcastText}");
                sent++;
                await Task.Delay(50);
            }
            catch { failed++; }
        }

        await adapter.SendTextMessageAsync(adminChatId,
            $"✅ Рассылка завершена.\n📬 Доставлено: *{sent}*\n⚠️ Ошибок: *{failed}*\n\nМеню: /menu");
    }

    // ─── Главный оркестратор сообщений ────────────────────────────────────────
    public async Task ProcessMessageOrCommandAsync(
        IBotPlatformAdapter adapter,
        long chatId,
        string userId,
        string text,
        string? username = null,
        string? firstName = null,
        string? lastName = null)
    {
        var user = await EnsureUserExistsAsync(userId, username, firstName, lastName, adapter.Platform);

        if (!IsUserVerified(user))
        {
            _stateService.ClearState(chatId);
            var captchaScreen = _captchaService.GenerateCaptchaScreen(userId);
            await adapter.RenderScreenAsync(chatId, null, captchaScreen);
            return;
        }

        if (!user.HasAcceptedPrivacyPolicy)
        {
            _stateService.ClearState(chatId);
            await adapter.RenderScreenAsync(chatId, null, BuildPrivacyPolicyOnboardingScreen());
            return;
        }

        if (!user.HasCompletedOnboarding && string.IsNullOrEmpty(user.AccessToken))
        {
            _stateService.ClearState(chatId);
            await adapter.RenderScreenAsync(chatId, null, BuildOnboardingShiftScreen(userId));
            return;
        }

        if (text.StartsWith("/wizard") || text.StartsWith("/onboard"))
        {
            _stateService.ClearState(chatId);
            await adapter.RenderScreenAsync(chatId, null, BuildOnboardingShiftScreen(userId));
            return;
        }

        if (text.StartsWith("/start") || text.StartsWith("/menu") || IsCancelCommand(text))
        {
            _stateService.ClearState(chatId);
            await ShowMainMenuAsync(adapter, chatId, userId);
            return;
        }

        if (text.StartsWith("/delete_account"))
        {
            _stateService.ClearState(chatId);
            await adapter.RenderScreenAsync(chatId, null, BuildDeleteAccountConfirmationScreen());
            return;
        }

        if (text.StartsWith("/stop") || text.StartsWith("/стоп") || text.Equals("стоп", StringComparison.OrdinalIgnoreCase))
        {
            await HandleUserStopAsync(adapter, chatId, userId);
            await adapter.SendTextMessageAsync(chatId, "👋 *Работа с ботом остановлена, ваши персональные данные и история сообщений удалены.*\n\nЧтобы начать заново, отправьте команду /start.");
            return;
        }

        if (text.StartsWith("/logout") || text.StartsWith("/exit") || text.StartsWith("/выйти"))
        {
            _stateService.ClearState(chatId);
            await adapter.RenderScreenAsync(chatId, null, BuildLogoutConfirmationScreen());
            return;
        }

        var session = _stateService.GetSession(chatId);
        if (session.State != UserState.Normal)
        {
            await HandleStateInputAsync(adapter, chatId, userId, text, session);
            return;
        }

        if (text.StartsWith("/now") || text.StartsWith("/bell") || text.StartsWith("/звонок"))
        {
            await adapter.RenderScreenAsync(chatId, null, await BuildNowScreenAsync(userId));
        }
        else if (text.StartsWith("/premium") || text.StartsWith("/sub") || text.StartsWith("/подписка"))
        {
            await adapter.SendTextMessageAsync(chatId,
                "⭐ *MirageDiary*\n\n" +
                "Раздел платной подписки закрыт — все функции бота (расписание, оценки, калькулятор, домашка и уведомления) полностью бесплатны! 🚀");
        }
        else if (text.StartsWith("/link") || text.StartsWith("/sync") || text.StartsWith("/связать"))
        {
            await HandleLinkCommandAsync(adapter, chatId, userId, text);
        }
        else if (text.StartsWith("/alarm") || text.StartsWith("/будильник"))
        {
            await adapter.RenderScreenAsync(chatId, null, await BuildAlarmCheckScreenAsync(userId));
        }
        else if (text.StartsWith("/profile") || text.StartsWith("/me") || text.StartsWith("/профиль"))
        {
            await adapter.RenderScreenAsync(chatId, null, await BuildProfileScreenAsync(userId));
        }
        else if (text.StartsWith("/homework") || text.StartsWith("/дз") || text.StartsWith("/домашка"))
        {
            var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            string dateArg = parts.Length > 1 ? parts[1].Trim() : "";
            await adapter.RenderScreenAsync(chatId, null, await BuildHomeworkScreenAsync(userId, dateArg));
        }
        else if (text.StartsWith("/schedule") || text.StartsWith("/расписание"))
        {
            var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            string dateArg = parts.Length > 1 ? parts[1].Trim() : "";
            await adapter.RenderScreenAsync(chatId, null, await BuildScheduleScreenAsync(userId, dateArg));
        }
        else if (text.StartsWith("/marks") || text.StartsWith("/grades") || text.StartsWith("/оценки"))
        {
            await adapter.RenderScreenAsync(chatId, null, await BuildMarksScreenAsync(userId, platform: adapter.Platform));
        }
        else if (text.StartsWith("/calc") || text.StartsWith("/calculator") || text.StartsWith("/калькулятор"))
        {
            await adapter.RenderScreenAsync(chatId, null, await BuildGradeCalculatorSubjectsScreenAsync(userId, platform: adapter.Platform));
        }
        else if (text.StartsWith("/children") || text.StartsWith("/kids") || text.StartsWith("/дети"))
        {
            await adapter.RenderScreenAsync(chatId, null, await BuildChildrenMenuScreenAsync(userId));
        }
        else if (text.StartsWith("/settings") || text.StartsWith("/настройки"))
        {
            await adapter.RenderScreenAsync(chatId, null, await BuildSettingsMenuScreenAsync(userId, adapter.Platform));
        }
        else if (text.StartsWith("/grades_off") || text.StartsWith("/marks_off") || text.StartsWith("/оценки_выкл"))
        {
            var u = await _db.Users.FirstOrDefaultAsync(x => x.Id == userId);
            if (u != null)
            {
                u.IsGradeWatcherEnabled = false;
                await _db.SaveChangesAsync();
                await _userSyncService.SyncUserAcrossPlatformsAsync(u);
            }
            await adapter.SendTextMessageAsync(chatId, "🔕 *Оповещения о новых оценках выключены.*\n\nВы всегда можете включить их обратно в /settings или командой /grades_on.");
        }
        else if (text.StartsWith("/grades_on") || text.StartsWith("/marks_on") || text.StartsWith("/оценки_вкл"))
        {
            var u = await _db.Users.FirstOrDefaultAsync(x => x.Id == userId);
            if (u != null)
            {
                u.IsGradeWatcherEnabled = true;
                await _db.SaveChangesAsync();
                await _userSyncService.SyncUserAcrossPlatformsAsync(u);
            }
            await adapter.SendTextMessageAsync(chatId, "🔔 *Оповещения о новых оценках включены!*\n\nБот мгновенно уведомит вас при появлении свежих отметок в журнале.");
        }
        else if (text.StartsWith("/notify_tg") || text.StartsWith("/уведомления_тг"))
        {
            await SetNotificationChannelPreferenceAsync(userId, "telegram", adapter.Platform);
            await adapter.SendTextMessageAsync(chatId, "✈️ *Канал уведомлений:* Все оповещения будут приходить *только в Telegram*.");
        }
        else if (text.StartsWith("/notify_max") || text.StartsWith("/уведомления_макс"))
        {
            await SetNotificationChannelPreferenceAsync(userId, "max", adapter.Platform);
            await adapter.SendTextMessageAsync(chatId, "💬 *Канал уведомлений:* Все оповещения будут приходить *только в MAX*.");
        }
        else if (text.StartsWith("/notify_all") || text.StartsWith("/уведомления_все"))
        {
            await SetNotificationChannelPreferenceAsync(userId, "all", adapter.Platform);
            await adapter.SendTextMessageAsync(chatId, "🔄 *Канал уведомлений:* Оповещения будут приходить *в оба мессенджера (Telegram + MAX)*.");
        }
        else if (text == "/" || text.StartsWith("/help") || text.StartsWith("/помощь") || text.StartsWith("/команды"))
        {
            await adapter.RenderScreenAsync(chatId, null, BuildHelpScreen());
        }
        else if (text.StartsWith("/admin"))
        {
            var role = await _roleService.GetUserRoleAsync(userId);
            if (role == "Admin")
            {
                await adapter.RenderScreenAsync(chatId, null, await BuildAdminPanelScreenAsync(userId, adapter.Platform));
            }
        }
        else
        {
            await ShowMainMenuAsync(adapter, chatId, userId);
        }
    }

    public async Task ShowMainMenuAsync(IBotPlatformAdapter adapter, long chatId, string userId, string? messageId = null)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (!IsUserVerified(user))
        {
            var captchaScreen = _captchaService.GenerateCaptchaScreen(userId);
            await adapter.RenderScreenAsync(chatId, messageId, captchaScreen);
            return;
        }

        if (user == null || !user.HasAcceptedPrivacyPolicy)
        {
            var onboardingScreen = BuildPrivacyPolicyOnboardingScreen();
            await adapter.RenderScreenAsync(chatId, messageId, onboardingScreen);
            return;
        }

        if (!user.HasCompletedOnboarding && string.IsNullOrEmpty(user.AccessToken))
        {
            var shiftScreen = BuildOnboardingShiftScreen(userId);
            await adapter.RenderScreenAsync(chatId, messageId, shiftScreen);
            return;
        }

        if (!await adapter.EnsureChannelSubscribedAsync(chatId, userId))
        {
            return;
        }

        var screen = await BuildMainMenuScreenAsync(userId, adapter.Platform);
        await adapter.RenderScreenAsync(chatId, messageId, screen);
    }

    // ─── Главный оркестратор обратных вызовов (Callbacks) ─────────────────────
    public async Task ProcessCallbackAsync(
        IBotPlatformAdapter adapter,
        long chatId,
        string? messageId,
        string userId,
        string callbackId,
        string data)
    {
        try
        {
            if (data != "cmd_profile_refresh" && !data.StartsWith("cmd_select_child:") && !data.StartsWith("select_child_") && !data.StartsWith("cmd_set_notify_channel:"))
            {
                await adapter.AnswerCallbackAsync(callbackId);
            }

            if (!data.StartsWith("cmd_admin_"))
            {
                _stateService.ClearState(chatId);
            }

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (!IsUserVerified(user))
            {
                if (data.StartsWith("cmd_captcha:"))
                {
                    var answer = data["cmd_captcha:".Length..];
                    bool valid = _captchaService.ValidateCaptcha(userId, answer);
                    if (valid)
                    {
                        if (user != null)
                        {
                            user.HasPassedCaptcha = true;
                            await _db.SaveChangesAsync();
                            await _userSyncService.SyncUserAcrossPlatformsAsync(user);
                        }
                        await adapter.AnswerCallbackAsync(callbackId, "✅ Проверка успешно пройдена!", false);

                        if (user == null || !user.HasAcceptedPrivacyPolicy)
                        {
                            await adapter.RenderScreenAsync(chatId, messageId, BuildPrivacyPolicyOnboardingScreen());
                        }
                        else if (!user.HasCompletedOnboarding)
                        {
                            await adapter.RenderScreenAsync(chatId, messageId, BuildOnboardingShiftScreen(userId));
                        }
                        else
                        {
                            await ShowMainMenuAsync(adapter, chatId, userId, messageId);
                        }
                        return;
                    }
                    else
                    {
                        await adapter.AnswerCallbackAsync(callbackId, "❌ Неверно! Попробуйте ещё раз.", true);
                        var newCaptcha = _captchaService.GenerateCaptchaScreen(userId);
                        await adapter.RenderScreenAsync(chatId, messageId, newCaptcha);
                        return;
                    }
                }
                else
                {
                    var captchaScreen = _captchaService.GenerateCaptchaScreen(userId);
                    await adapter.RenderScreenAsync(chatId, messageId, captchaScreen);
                    return;
                }
            }

            switch (data)
            {
            case "cmd_back_main":
            case "cmd_back_main_new":
                await ShowMainMenuAsync(adapter, chatId, userId, messageId);
                break;

            case "cmd_now":
                await adapter.RenderScreenAsync(chatId, messageId, await BuildNowScreenAsync(userId));
                break;

            case "cmd_subscription":
                await ShowMainMenuAsync(adapter, chatId, userId, messageId);
                break;

            case "cmd_accept_privacy":
                await AcceptPrivacyPolicyAsync(userId);
                var refreshedUser = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
                if (refreshedUser != null && !refreshedUser.HasCompletedOnboarding)
                {
                    await adapter.RenderScreenAsync(chatId, messageId, BuildOnboardingShiftScreen(userId));
                }
                else
                {
                    await ShowMainMenuAsync(adapter, chatId, userId, messageId);
                }
                break;

            case { } s when s.StartsWith("cmd_onboard_shift:"):
                var shiftArg = s["cmd_onboard_shift:".Length..];
                var uShift = await _db.Users.FirstOrDefaultAsync(x => x.Id == userId);
                if (uShift != null)
                {
                    if (shiftArg == "2") uShift.Shift = 2;
                    else if (shiftArg == "1") uShift.Shift = 1;
                    await _db.SaveChangesAsync();
                    await _userSyncService.SyncUserAcrossPlatformsAsync(uShift);
                }
                var onboardAlarmScreen = BuildOnboardingAlarmScreen(userId, uShift?.Shift ?? 1);
                await adapter.RenderScreenAsync(chatId, messageId, onboardAlarmScreen);
                break;

            case { } s when s.StartsWith("cmd_onboard_alarm:"):
                var alarmMode = s["cmd_onboard_alarm:".Length..];
                var uAlarm = await _db.Users.FirstOrDefaultAsync(x => x.Id == userId);
                if (uAlarm != null)
                {
                    ApplyOnboardingAlarmPreset(uAlarm, alarmMode);
                    uAlarm.HasCompletedOnboarding = true;
                    await _db.SaveChangesAsync();
                    await _userSyncService.SyncUserAcrossPlatformsAsync(uAlarm);
                }
                var finishScreen = await BuildOnboardingFinishScreenAsync(userId, adapter.Platform);
                await adapter.RenderScreenAsync(chatId, messageId, finishScreen);
                break;

            case "cmd_onboard_restart":
                await adapter.RenderScreenAsync(chatId, messageId, BuildOnboardingShiftScreen(userId));
                break;

            case "cmd_alarm_toggle_shift":
                var uToggleShift = await _db.Users.FirstOrDefaultAsync(x => x.Id == userId);
                if (uToggleShift != null)
                {
                    uToggleShift.Shift = uToggleShift.Shift == 1 ? 2 : 1;
                    await _db.SaveChangesAsync();
                    await _userSyncService.SyncUserAcrossPlatformsAsync(uToggleShift);
                }
                await adapter.RenderScreenAsync(chatId, messageId, await BuildAlarmMenuScreenAsync(userId));
                break;

            case "cmd_check_channel_sub":
                bool isSubbed = await adapter.EnsureChannelSubscribedAsync(chatId, userId);
                if (isSubbed)
                {
                    await adapter.AnswerCallbackAsync(callbackId, "✅ Подписка подтверждена!", false);
                    await ShowMainMenuAsync(adapter, chatId, userId, messageId);
                }
                else
                {
                    await adapter.AnswerCallbackAsync(callbackId, "❌ Вы ещё не подписались на канал. Пожалуйста, подпишитесь и повторите проверку!", true);
                }
                break;

            case "cmd_sub_activate_beta":
                var betaRes = await ActivateBetaSubscriptionAsync(userId);
                if (betaRes.AlertText != null)
                    await adapter.AnswerCallbackAsync(callbackId, betaRes.AlertText, betaRes.ShowAlert);
                if (betaRes.Screen != null)
                    await adapter.RenderScreenAsync(chatId, messageId, betaRes.Screen);
                break;

            case { } s when s.StartsWith("cmd_set_shift:"):
                if (int.TryParse(s["cmd_set_shift:".Length..], out int newShift))
                {
                    await SetShiftAsync(userId, newShift);
                    await adapter.RenderScreenAsync(chatId, messageId, await BuildNowScreenAsync(userId));
                }
                break;

            // ── Домашнее задание ──
            case "cmd_homework_refresh":
                await adapter.RenderScreenAsync(chatId, messageId, await BuildHomeworkScreenAsync(userId, null, forceRefresh: true));
                break;

            case { } s when s.StartsWith("cmd_homework_refresh:"):
                var hwRefDate = s["cmd_homework_refresh:".Length..];
                await adapter.RenderScreenAsync(chatId, messageId, await BuildHomeworkScreenAsync(userId, hwRefDate, forceRefresh: true));
                break;

            case { } s when s.StartsWith("cmd_homework:"):
                var dateParam = s["cmd_homework:".Length..];
                await adapter.RenderScreenAsync(chatId, messageId, await BuildHomeworkScreenAsync(userId, dateParam));
                break;

            case "cmd_homework":
                await adapter.RenderScreenAsync(chatId, messageId, await BuildHomeworkScreenAsync(userId));
                break;

            // ── Расписание ──
            case "cmd_schedule_refresh":
                await adapter.RenderScreenAsync(chatId, messageId, await BuildScheduleScreenAsync(userId, null, forceRefresh: true));
                break;

            case { } s when s.StartsWith("cmd_schedule_refresh:"):
                var schedRefDate = s["cmd_schedule_refresh:".Length..];
                await adapter.RenderScreenAsync(chatId, messageId, await BuildScheduleScreenAsync(userId, schedRefDate, forceRefresh: true));
                break;

            case { } s when s.StartsWith("cmd_schedule:"):
                var schedDate = s["cmd_schedule:".Length..];
                await adapter.RenderScreenAsync(chatId, messageId, await BuildScheduleScreenAsync(userId, schedDate));
                break;

            case "cmd_schedule":
                await adapter.RenderScreenAsync(chatId, messageId, await BuildScheduleScreenAsync(userId));
                break;

            // ── Оценки и калькулятор ──
            case { } s when s.StartsWith("cmd_marks_refresh:"):
                var qRefStr = s["cmd_marks_refresh:".Length..];
                int? qRef = int.TryParse(qRefStr, out int qr) ? qr : null;
                await adapter.RenderScreenAsync(chatId, messageId, await BuildMarksScreenAsync(userId, qRef, forceRefresh: true));
                break;

            case "cmd_marks_refresh":
            case "marks_refresh":
                await adapter.RenderScreenAsync(chatId, messageId, await BuildMarksScreenAsync(userId, quarter: null, forceRefresh: true, platform: adapter.Platform));
                break;

            case { } s when s.StartsWith("cmd_marks_q:"):
                var qSelStr = s["cmd_marks_q:".Length..];
                int? qSel = int.TryParse(qSelStr, out int qs) ? qs : null;
                await adapter.RenderScreenAsync(chatId, messageId, await BuildMarksScreenAsync(userId, qSel, platform: adapter.Platform));
                break;

            case { } s when s.StartsWith("cmd_marks:"):
                var qStr = s["cmd_marks:".Length..];
                int? qVal = int.TryParse(qStr, out int qv) ? qv : null;
                await adapter.RenderScreenAsync(chatId, messageId, await BuildMarksScreenAsync(userId, qVal, platform: adapter.Platform));
                break;

            case "cmd_marks":
                await adapter.RenderScreenAsync(chatId, messageId, await BuildMarksScreenAsync(userId, platform: adapter.Platform));
                break;

            case "cmd_calc_refresh":
                await adapter.RenderScreenAsync(chatId, messageId, await BuildGradeCalculatorSubjectsScreenAsync(userId, forceRefresh: true, platform: adapter.Platform));
                break;

            case "cmd_calc":
                await adapter.RenderScreenAsync(chatId, messageId, await BuildGradeCalculatorSubjectsScreenAsync(userId, platform: adapter.Platform));
                break;

            case { } s when s.StartsWith("cmd_calc_subj:") || s.StartsWith("calc_subj_"):
                string sIdxStr = s.StartsWith("cmd_calc_subj:") ? s["cmd_calc_subj:".Length..] : s["calc_subj_".Length..];
                if (int.TryParse(sIdxStr, out int subjIdx))
                {
                    await adapter.RenderScreenAsync(chatId, messageId, await BuildSubjectTargetOptionsScreenAsync(userId, subjIdx, platform: adapter.Platform));
                }
                break;

            case { } s when s.StartsWith("cmd_calc_target:") || s.StartsWith("calc_target_"):
                string tStr = s.StartsWith("cmd_calc_target:") ? s["cmd_calc_target:".Length..] : s["calc_target_".Length..];
                char delim = s.StartsWith("cmd_calc_target:") ? ':' : '_';
                var parts = tStr.Split(delim);
                if (parts.Length == 2 && int.TryParse(parts[0], out int sIdx) && int.TryParse(parts[1], out int targetG))
                {
                    await adapter.RenderScreenAsync(chatId, messageId, await BuildTargetCalculationResultScreenAsync(userId, sIdx, targetG, platform: adapter.Platform));
                }
                break;

            case { } s when s.StartsWith("cmd_calc_sim:") || s.StartsWith("calc_sim_"):
                string simStr = s.StartsWith("cmd_calc_sim:") ? s["cmd_calc_sim:".Length..] : s["calc_sim_".Length..];
                var simParts = simStr.Split(new[] { ':', '_' }, StringSplitOptions.RemoveEmptyEntries);
                if (simParts.Length >= 1 && int.TryParse(simParts[0], out int simSubjIdx))
                {
                    int simGrade = simParts.Length > 1 && int.TryParse(simParts[1], out int g) ? g : 2;
                    int simWeight = simParts.Length > 2 && int.TryParse(simParts[2], out int w) ? w : 1;
                    int simCount = simParts.Length > 3 && int.TryParse(simParts[3], out int c) ? c : 1;

                    await adapter.RenderScreenAsync(chatId, messageId, await BuildHypotheticalGradeScreenAsync(
                        userId, simSubjIdx, simGrade, simWeight, simCount, platform: adapter.Platform));
                }
                break;

            // ── Профиль и переключение детей ──
            case "cmd_profile_refresh":
                if (_profileRefreshCooldown.TryGetValue(userId, out var lastRefreshTime))
                {
                    var elapsed = DateTime.UtcNow - lastRefreshTime;
                    var remaining = TimeSpan.FromSeconds(20) - elapsed;
                    if (remaining > TimeSpan.Zero)
                    {
                        await adapter.AnswerCallbackAsync(callbackId, $"⏳ Подождите {Math.Ceiling(remaining.TotalSeconds)} сек. перед следующим обновлением!", showAlert: true);
                        break;
                    }
                }
                _profileRefreshCooldown[userId] = DateTime.UtcNow;
                await adapter.AnswerCallbackAsync(callbackId, "🔄 Обновляем данные профиля...", showAlert: false);
                await adapter.RenderScreenAsync(chatId, messageId, await BuildProfileScreenAsync(userId, forceRefresh: true));
                break;

            case "cmd_profile":
                await adapter.RenderScreenAsync(chatId, messageId, await BuildProfileScreenAsync(userId));
                break;

            case "cmd_children":
            case "cmd_children_menu":
                await adapter.RenderScreenAsync(chatId, messageId, await BuildChildrenMenuScreenAsync(userId));
                break;

            case { } s when s.StartsWith("cmd_select_child:"):
                var childId = s["cmd_select_child:".Length..];
                var selRes = await SelectChildAsync(userId, childId);
                await adapter.AnswerCallbackAsync(callbackId, selRes.AlertText ?? "✅ Ребенок выбран!", showAlert: false);
                await adapter.RenderScreenAsync(chatId, messageId, selRes.Screen);
                break;

            case { } s when s.StartsWith("select_child_"):
                var scId = s["select_child_".Length..];
                var scRes = await SelectChildAsync(userId, scId);
                await adapter.AnswerCallbackAsync(callbackId, scRes.AlertText ?? "✅ Ребенок выбран!", showAlert: false);
                await adapter.RenderScreenAsync(chatId, messageId, scRes.Screen);
                break;

            // ── Авторизация и аккаунт ──
            case "cmd_logout":
                await adapter.RenderScreenAsync(chatId, messageId, BuildLogoutConfirmationScreen());
                break;

            case "cmd_confirm_logout":
                await adapter.RenderScreenAsync(chatId, messageId, await PerformLogoutAsync(userId));
                break;

            case "cmd_help":
                await adapter.RenderScreenAsync(chatId, messageId, BuildHelpScreen());
                break;

            case "cmd_settings":
                await adapter.RenderScreenAsync(chatId, messageId, await BuildSettingsMenuScreenAsync(userId, adapter.Platform));
                break;

            case "cmd_settings_grades_toggle":
                var sGrRes = await ToggleSettingsGradeWatcherAsync(userId, adapter.Platform);
                if (sGrRes.Screen != null)
                    await adapter.RenderScreenAsync(chatId, messageId, sGrRes.Screen);
                else
                    await adapter.RenderScreenAsync(chatId, messageId, await BuildSettingsMenuScreenAsync(userId, adapter.Platform));
                break;

            case "cmd_settings_notify_channel":
                await adapter.RenderScreenAsync(chatId, messageId, await BuildNotificationChannelSelectScreenAsync(userId, adapter.Platform));
                break;

            case { } s when s.StartsWith("cmd_set_notify_channel:"):
                string channelPref = s["cmd_set_notify_channel:".Length..];
                var chRes = await SetNotificationChannelPreferenceAsync(userId, channelPref, adapter.Platform);
                if (chRes.Screen != null)
                    await adapter.RenderScreenAsync(chatId, messageId, chRes.Screen);
                await adapter.AnswerCallbackAsync(callbackId, chRes.AlertText, showAlert: false);
                break;

            case "cmd_alarm":
            case "cmd_alarm_menu":
                await adapter.RenderScreenAsync(chatId, messageId, await BuildAlarmMenuScreenAsync(userId));
                break;

            case "cmd_alarm_toggle_master":
                var masterRes = await ToggleMasterAlarmAsync(userId);
                if (masterRes.Screen != null)
                    await adapter.RenderScreenAsync(chatId, messageId, masterRes.Screen);
                else
                    await adapter.RenderScreenAsync(chatId, messageId, await BuildAlarmMenuScreenAsync(userId));
                break;

            case "cmd_alarm_grades_toggle":
                var grRes = await ToggleGradeWatcherReminderAsync(userId);
                if (grRes.Screen != null)
                    await adapter.RenderScreenAsync(chatId, messageId, grRes.Screen);
                else
                    await adapter.RenderScreenAsync(chatId, messageId, await BuildAlarmMenuScreenAsync(userId));
                break;

            case "cmd_alarm_check":
            case "cmd_alarm_check_now":
                await adapter.RenderScreenAsync(chatId, null, await BuildAlarmCheckScreenAsync(userId));
                break;

            case "cmd_delete_account":
                await adapter.RenderScreenAsync(chatId, messageId, BuildDeleteAccountConfirmationScreen());
                break;

            case "cmd_delete_account_confirm":
                var delScreen = await ExecuteDeleteAccountAsync(userId, adapter, chatId);
                await adapter.RenderScreenAsync(chatId, null, delScreen);
                break;

            case "cmd_link_account":
                await adapter.RenderScreenAsync(chatId, messageId, await BuildLinkAccountScreenAsync(userId));
                break;

            // ── Напоминания ──
            case "cmd_alarm_sched":
                await adapter.RenderScreenAsync(chatId, messageId, await BuildScheduleReminderMenuScreenAsync(userId));
                break;

            case "cmd_alarm_sched_toggle":
                var schedToggle = await ToggleScheduleReminderAsync(userId);
                if (schedToggle.Screen != null)
                    await adapter.RenderScreenAsync(chatId, messageId, schedToggle.Screen);
                break;

            case { } s when s.StartsWith("cmd_alarm_sched_time:"):
                var sTime = s["cmd_alarm_sched_time:".Length..];
                var sRes = await SetScheduleReminderTimeAsync(userId, sTime);
                if (sRes.Screen != null)
                    await adapter.RenderScreenAsync(chatId, messageId, sRes.Screen);
                break;

            case "cmd_alarm_sched_custom":
                _stateService.UpdateState(chatId, UserState.WaitingForScheduleReminderTime);
                var schedPrompt = BotScreen.Create(
                    "📅 *Напоминание о расписании*\n\nВведите время отправки в формате *ЧЧ:ММ* (например, `07:15` или `08:00`):\n_(или напишите «Отмена»)_")
                    .WithButtons(new List<List<BotButton>> { new() { BotButton.Callback("❌ Отмена", "cmd_alarm_sched") } });
                await adapter.RenderScreenAsync(chatId, messageId, schedPrompt);
                break;

            case "cmd_alarm_hw":
                await adapter.RenderScreenAsync(chatId, messageId, await BuildHomeworkReminderMenuScreenAsync(userId));
                break;

            case "cmd_alarm_hw_toggle":
                var hwToggle = await ToggleHomeworkReminderAsync(userId);
                if (hwToggle.Screen != null)
                    await adapter.RenderScreenAsync(chatId, messageId, hwToggle.Screen);
                break;

            case { } s when s.StartsWith("cmd_alarm_hw_time:"):
                var hTime = s["cmd_alarm_hw_time:".Length..];
                var hRes = await SetHomeworkReminderTimeAsync(userId, hTime);
                if (hRes.Screen != null)
                    await adapter.RenderScreenAsync(chatId, messageId, hRes.Screen);
                break;

            case "cmd_alarm_hw_custom":
                _stateService.UpdateState(chatId, UserState.WaitingForHomeworkReminderTime);
                var hwPrompt = BotScreen.Create(
                    "📚 *Напоминание о домашнем задании*\n\nВведите время отправки в формате *ЧЧ:ММ* (например, `16:30` или `18:00`):\n_(или напишите «Отмена»)_")
                    .WithButtons(new List<List<BotButton>> { new() { BotButton.Callback("❌ Отмена", "cmd_alarm_hw") } });
                await adapter.RenderScreenAsync(chatId, messageId, hwPrompt);
                break;

            case "cmd_alarm_equip":
                await adapter.RenderScreenAsync(chatId, messageId, await BuildEquipmentReminderMenuScreenAsync(userId));
                break;

            case "cmd_alarm_equip_toggle":
                var eqToggle = await ToggleEquipmentReminderAsync(userId);
                if (eqToggle.Screen != null)
                    await adapter.RenderScreenAsync(chatId, messageId, eqToggle.Screen);
                break;

            case { } s when s.StartsWith("cmd_alarm_equip_time:"):
                var eTime = s["cmd_alarm_equip_time:".Length..];
                var eRes = await SetEquipmentReminderTimeAsync(userId, eTime);
                if (eRes.Screen != null)
                    await adapter.RenderScreenAsync(chatId, messageId, eRes.Screen);
                break;

            case "cmd_alarm_equip_custom":
                _stateService.UpdateState(chatId, UserState.WaitingForEquipmentReminderTime);
                var eqPrompt = BotScreen.Create(
                    "🎒 *Умный будильник (сбор рюкзака)*\n\nВведите время напоминания в формате *ЧЧ:ММ* (например, `20:15` или `21:00`):\n_(или напишите «Отмена»)_")
                    .WithButtons(new List<List<BotButton>> { new() { BotButton.Callback("❌ Отмена", "cmd_alarm_equip") } });
                await adapter.RenderScreenAsync(chatId, messageId, eqPrompt);
                break;

            // ── Админка ──
            case "cmd_admin_panel":
            case "cmd_back_admin":
                if (await _roleService.GetUserRoleAsync(userId) == "Admin")
                    await adapter.RenderScreenAsync(chatId, messageId, await BuildAdminPanelScreenAsync(userId, adapter.Platform));
                break;

            case "cmd_admin_headteachers":
                if (await _roleService.GetUserRoleAsync(userId) == "Admin")
                    await adapter.RenderScreenAsync(chatId, messageId, await BuildHeadTeachersMenuScreenAsync(userId));
                break;

            case "cmd_admin_add_ht":
                _stateService.UpdateState(chatId, UserState.WaitingForHeadTeacherId);
                var addHtPrompt = BotScreen.Create(
                    "👤 *Добавление завуча*\n\nВведите ID, @username или имя пользователя:\n_(или напишите «Отмена»)_", "banner_admin.png")
                    .WithButtons(new List<List<BotButton>> { new() { BotButton.Callback("❌ Отмена", "cmd_admin_headteachers") } });
                await adapter.RenderScreenAsync(chatId, messageId, addHtPrompt);
                break;

            case { } s when s.StartsWith("cmd_admin_remove_ht:"):
                var removeHtId = s["cmd_admin_remove_ht:".Length..];
                await RemoveHeadTeacherAsync(adapter, chatId, messageId, userId, removeHtId);
                break;

            case "cmd_admin_maintenance":
                await adapter.RenderScreenAsync(chatId, messageId, BuildMaintenanceMenuScreenAsync(userId));
                break;

            case "cmd_admin_maintenance_on":
                await ToggleMaintenanceAsync(adapter, chatId, messageId, userId, true);
                break;

            case "cmd_admin_maintenance_off":
                await ToggleMaintenanceAsync(adapter, chatId, messageId, userId, false);
                break;

            case "cmd_admin_broadcast":
                if (await _roleService.GetUserRoleAsync(userId) == "Admin")
                {
                    _stateService.UpdateState(chatId, UserState.WaitingForBroadcastText);
                    var bcPrompt = BotScreen.Create(
                        "📢 *Рассылка*\n\nВведите текст сообщения для всех пользователей бота:\n_(или напишите «Отмена»)_", "banner_admin.png")
                        .WithButtons(new List<List<BotButton>> { new() { BotButton.Callback("❌ Отмена", "cmd_admin_panel") } });
                    await adapter.RenderScreenAsync(chatId, messageId, bcPrompt);
                }
                break;

            case "cmd_admin_stats":
                if (await _roleService.GetUserRoleAsync(userId) == "Admin")
                    await adapter.RenderScreenAsync(chatId, messageId, await BuildStatsScreenAsync(userId));
                break;
        }
        }
        catch (DiarySessionExpiredException ex)
        {
            var webAppUrl = _config["MiniAppUrl"] ?? "https://miragediary.space";
            var buttons = new List<List<BotButton>>();
            if (adapter.Platform == "telegram")
                buttons.Add(new List<BotButton> { BotButton.WebApp("🔐 Войти заново", BuildMiniAppUrl(userId, null, force: true)) });
            else
            {
                var maxBotApp = _config["MaxBot:Username"] ?? _config["MaxBotUsername"] ?? "se13960436_bot";
                buttons.Add(new List<BotButton> { BotButton.MaxApp("🔐 Войти заново", maxBotApp, userId) });
            }
            buttons.Add(new List<BotButton> { BotButton.Callback("🏠 Главное меню", "cmd_back_main") });
            await adapter.RenderScreenAsync(chatId, messageId, BotScreen.Create($"🔒 *Сессия Госуслуг устарела*\n\n{TelegramMarkdownEscaper.Escape(ex.Message)}", "banner_menu.png").WithButtons(buttons));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DiaryBotWorkflowEngine] Callback error on '{data}': {ex.Message}");
            var buttons = new List<List<BotButton>>
            {
                new() { BotButton.Callback("🏠 Главное меню", "cmd_back_main") }
            };
            await adapter.RenderScreenAsync(chatId, messageId, BotScreen.Create("⚠️ *Произошла ошибка при выполнении команды*\n\nПожалуйста, попробуйте повторить действие через несколько секунд.", "banner_menu.png").WithButtons(buttons));
        }
    }

    // ─── Загрузка файлов (расписания) ─────────────────────────────────────────
    public async Task HandleFileUploadAsync(IBotPlatformAdapter adapter, long chatId, string userId, string fileId, string fileType)
    {
        var role = await _roleService.GetUserRoleAsync(userId);
        if (role != "HeadTeacher" && role != "Admin")
        {
            await adapter.SendTextMessageAsync(chatId, "❌ Только завучи могут загружать расписание.");
            return;
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null || string.IsNullOrEmpty(user.OrgName))
        {
            await adapter.SendTextMessageAsync(chatId, "❌ Вы не привязаны ни к какой школе.");
            return;
        }

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
        await adapter.SendTextMessageAsync(chatId,
            $"✅ Расписание для школы *{safeOrg}* успешно сохранено!\n\n" +
            $"Теперь ученики смогут получить его через кнопку «📅 Расписание».");
    }

    // ─── Обработка текстового ввода в состояниях ──────────────────────────────
    public async Task HandleStateInputAsync(IBotPlatformAdapter adapter, long chatId, string userId, string text, UserSession session)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            await adapter.SendTextMessageAsync(chatId, "⚠️ Введите значение или напишите «Отмена»:");
            return;
        }

        switch (session.State)
        {
            case UserState.WaitingForHeadTeacherId:
                if (IsCancelCommand(text))
                {
                    _stateService.ClearState(chatId);
                    await adapter.SendTextMessageAsync(chatId, "❌ Назначение завуча отменено.");
                    return;
                }
                _stateService.UpdateState(chatId, UserState.WaitingForHeadTeacherOrg, text);
                await adapter.SendTextMessageAsync(chatId,
                    "🏫 Теперь введите название школы, к которой привязать этого завуча:\n_(или напишите «Отмена»)_");
                break;

            case UserState.WaitingForHeadTeacherOrg:
                if (IsCancelCommand(text))
                {
                    _stateService.ClearState(chatId);
                    await adapter.SendTextMessageAsync(chatId, "❌ Назначение завуча отменено.");
                    return;
                }
                await AddHeadTeacherAsync(adapter, chatId, session.TempData ?? "", text);
                _stateService.ClearState(chatId);
                break;

            case UserState.WaitingForBroadcastText:
                if (IsCancelCommand(text))
                {
                    _stateService.ClearState(chatId);
                    await adapter.SendTextMessageAsync(chatId, "❌ Рассылка отменена.");
                    return;
                }
                await DoBroadcastAsync(adapter, chatId, text);
                _stateService.ClearState(chatId);
                break;

            case UserState.WaitingForScheduleReminderTime:
                if (IsCancelCommand(text))
                {
                    _stateService.ClearState(chatId);
                    await adapter.SendTextMessageAsync(chatId, "❌ Ввод времени отменён.");
                    return;
                }
                if (TryParseTime(text, out var schedTime))
                {
                    var user = await EnsureUserExistsAsync(userId);
                    user.ScheduleReminderTime = schedTime;
                    user.IsScheduleReminderEnabled = true;
                    await _db.SaveChangesAsync();
                    _stateService.ClearState(chatId);
                    await adapter.SendTextMessageAsync(chatId, $"✅ Время напоминания о расписании установлено на *{schedTime}*!\n\nКаждое утро в это время вы будете получать список уроков.");
                }
                else
                {
                    await adapter.SendTextMessageAsync(chatId, "❌ Неверный формат времени. Введите время в формате *ЧЧ:ММ* (например: `07:30` или `8:00`) или напишите «Отмена»:");
                }
                break;

            case UserState.WaitingForHomeworkReminderTime:
                if (IsCancelCommand(text))
                {
                    _stateService.ClearState(chatId);
                    await adapter.SendTextMessageAsync(chatId, "❌ Ввод времени отменён.");
                    return;
                }
                if (TryParseTime(text, out var hwTime))
                {
                    var user = await EnsureUserExistsAsync(userId);
                    user.HomeworkReminderTime = hwTime;
                    user.IsHomeworkReminderEnabled = true;
                    await _db.SaveChangesAsync();
                    _stateService.ClearState(chatId);
                    await adapter.SendTextMessageAsync(chatId, $"✅ Время напоминания о домашке установлено на *{hwTime}*!\n\nКаждый день в это время бот пришлет список заданий на завтра.");
                }
                else
                {
                    await adapter.SendTextMessageAsync(chatId, "❌ Неверный формат времени. Введите время в формате *ЧЧ:ММ* (например: `17:00` или `18:30`) или напишите «Отмена»:");
                }
                break;

            case UserState.WaitingForEquipmentReminderTime:
                if (IsCancelCommand(text))
                {
                    _stateService.ClearState(chatId);
                    await adapter.SendTextMessageAsync(chatId, "❌ Ввод времени отменён.");
                    return;
                }
                if (TryParseTime(text, out var equipTime))
                {
                    var user = await EnsureUserExistsAsync(userId);
                    user.EquipmentReminderTime = equipTime;
                    user.IsEquipmentReminderEnabled = true;
                    await _db.SaveChangesAsync();
                    _stateService.ClearState(chatId);
                    await adapter.SendTextMessageAsync(chatId, $"✅ Время сбора рюкзака установлено на *{equipTime}*!\n\nВечером бот напомнит, какие вещи и инструменты положить в рюкзак.");
                }
                else
                {
                    await adapter.SendTextMessageAsync(chatId, "❌ Неверный формат времени. Введите время в формате *ЧЧ:ММ* (например: `20:30` или `21:00`) или напишите «Отмена»:");
                }
                break;
        }
    }

    // ─── Обработка команды связки аккаунтов ───────────────────────────────────
    public async Task HandleLinkCommandAsync(IBotPlatformAdapter adapter, long chatId, string userId, string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            await adapter.RenderScreenAsync(chatId, null, await BuildLinkAccountScreenAsync(userId));
            return;
        }

        string code = parts[1].Trim();
        var (success, message, peer) = await _userSyncService.TryLinkAccountsByCodeAsync(userId, code);
        if (success)
        {
            string peerInfo = peer != null && !string.IsNullOrEmpty(peer.FirstName) ? $" ({TelegramMarkdownEscaper.Escape(peer.FirstName)})" : "";
            await adapter.SendTextMessageAsync(chatId,
                $"✅ *Аккаунты успешно связаны!*{peerInfo}\n\n" +
                $"Все токены, класс, расписание и настройки синхронизированы с вашим аккаунтом на другой платформе.");
        }
        else
        {
            string safeMsg = TelegramMarkdownEscaper.Escape(message);
            await adapter.SendTextMessageAsync(chatId, $"❌ *Ошибка связки:* {safeMsg}");
        }
    }

    private static bool IsCancelCommand(string text)
    {
        var t = text.Trim().ToLowerInvariant();
        return t == "отмена" || t == "cancel" || t == "/cancel" || t == "назад" || t == "стоп";
    }

    public static bool TryParseTime(string text, out string formatted)
    {
        formatted = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split(':');
        if (parts.Length != 2) return false;
        if (int.TryParse(parts[0], out int h) && int.TryParse(parts[1], out int m))
        {
            if (h is >= 0 and <= 23 && m is >= 0 and <= 59)
            {
                formatted = $"{h:D2}:{m:D2}";
                return true;
            }
        }
        return false;
    }

    private static string Truncate(string str, int maxLength)
    {
        if (string.IsNullOrEmpty(str)) return string.Empty;
        return str.Length <= maxLength ? str : str[..maxLength] + "…";
    }
}

