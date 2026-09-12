using System;
using Microsoft.Extensions.Configuration;

namespace MirageDiary.Server.Services;

public static class DiaryMaintenanceSettings
{
    public static bool IsDiaryInMaintenance(IConfiguration config)
    {
        var rawUntil = config["DiarySettings:MaintenanceUntilUtc"];
        if (DateTime.TryParse(rawUntil, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var untilUtc))
        {
            if (DateTime.UtcNow < untilUtc) return true;
        }

        var notifEnabled = config.GetValue<bool?>("DiarySettings:NotificationsEnabled");
        if (notifEnabled == false) return true;

        return false;
    }

    public static bool IsTokenRefreshDisabled(IConfiguration config)
    {
        var refreshEnabled = config.GetValue<bool?>("DiarySettings:BackgroundTokenRefreshEnabled");
        if (refreshEnabled == false) return true;

        return IsDiaryInMaintenance(config);
    }
}
