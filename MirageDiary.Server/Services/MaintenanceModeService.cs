using System;

namespace MirageDiary.Server.Services;

public class MaintenanceModeService
{
    private DateTime? _maintenanceEndTime = null;
    private readonly object _lock = new object();

    public bool IsMaintenanceModeActive()
    {
        lock (_lock)
        {
            if (_maintenanceEndTime.HasValue && DateTime.UtcNow < _maintenanceEndTime.Value)
            {
                return true;
            }
            // Maintenance expired or not set
            _maintenanceEndTime = null;
            return false;
        }
    }

    public void ActivateMaintenanceMode(int minutes = 15)
    {
        lock (_lock)
        {
            _maintenanceEndTime = DateTime.UtcNow.AddMinutes(minutes);
            Console.WriteLine($"[MAINTENANCE] Activated until {_maintenanceEndTime.Value} UTC");
        }
    }

    public void DeactivateMaintenanceMode()
    {
        lock (_lock)
        {
            _maintenanceEndTime = null;
            Console.WriteLine("[MAINTENANCE] Deactivated.");
        }
    }
}
