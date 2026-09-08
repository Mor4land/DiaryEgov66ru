using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace MirageDiary.Server.Services;

/// <summary>
/// Состояние компонента, о котором сервис может отчитаться честно.
/// Unknown означает "компонент ещё ни разу не отчитался" — это не то же самое, что "работает".
/// </summary>
public enum HealthState
{
    Unknown,
    Healthy,
    Degraded,
    Down,
    NotConfigured
}

/// <summary>
/// Регистр состояния компонентов сервиса: боты, база, внешний API дневника.
/// Заполняется только настоящими отчётами от самих компонентов. Ничего не досочиняет:
/// если компонент не отчитывался, состояние остаётся Unknown, а не Healthy.
/// </summary>
public class ServiceHealthService
{
    private readonly ConcurrentDictionary<string, ComponentHealth> _components = new();

    public class ComponentHealth
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public HealthState State { get; set; } = HealthState.Unknown;

        /// <summary>Момент, с которого компонент непрерывно работает (для long polling — вход в цикл приёма).</summary>
        public DateTime? RunningSinceUtc { get; set; }
        public DateTime? LastSuccessUtc { get; set; }
        public DateTime? LastFailureUtc { get; set; }
        public string? LastError { get; set; }

        public long SuccessCount;
        public long FailureCount;
    }

    private ComponentHealth Get(string key, string name) =>
        _components.GetOrAdd(key, _ => new ComponentHealth { Key = key, Name = name });

    /// <summary>
    /// Компонент с длительным циклом (long polling) успешно вошёл в цикл приёма.
    /// Для таких компонентов это и есть признак жизни: обновления приходят только когда пишут пользователи,
    /// поэтому тишина в чате не означает, что бот упал.
    /// </summary>
    public void ReportRunning(string key, string name)
    {
        var c = Get(key, name);
        lock (c)
        {
            c.Name = name;
            c.State = HealthState.Healthy;
            c.RunningSinceUtc ??= DateTime.UtcNow;
            c.LastError = null;
        }
    }

    /// <summary>Разовая успешная операция (запрос к внешнему API, ответ базы).</summary>
    public void ReportSuccess(string key, string name)
    {
        var c = Get(key, name);
        lock (c)
        {
            c.Name = name;
            c.State = HealthState.Healthy;
            c.LastSuccessUtc = DateTime.UtcNow;
            c.SuccessCount++;
            c.LastError = null;
        }
    }

    /// <summary>Операция не удалась. Компонент помечается как упавший до следующего успеха.</summary>
    public void ReportFailure(string key, string name, string error, bool fatal = true)
    {
        var c = Get(key, name);
        lock (c)
        {
            c.Name = name;
            c.State = fatal ? HealthState.Down : HealthState.Degraded;
            c.LastFailureUtc = DateTime.UtcNow;
            c.LastError = Truncate(error, 300);
            c.FailureCount++;
            if (fatal) c.RunningSinceUtc = null;
        }
    }

    /// <summary>Компонент не настроен и не запускался — это не сбой.</summary>
    public void ReportNotConfigured(string key, string name)
    {
        var c = Get(key, name);
        lock (c)
        {
            c.Name = name;
            c.State = HealthState.NotConfigured;
            c.RunningSinceUtc = null;
            c.LastError = null;
        }
    }

    /// <summary>Регистрирует компонент, о котором ещё нет данных, чтобы он был виден в консоли.</summary>
    public void Register(string key, string name) => Get(key, name);

    public IReadOnlyList<ComponentHealth> Snapshot() =>
        _components.Values.OrderBy(c => c.Key, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Худшее состояние среди настроенных компонентов. Ненастроенные не портят общую картину,
    /// а компоненты без данных дают Unknown — консоль обязана сказать "нет данных", а не "всё хорошо".
    /// </summary>
    public HealthState Overall()
    {
        var states = _components.Values
            .Select(c => c.State)
            .Where(s => s != HealthState.NotConfigured)
            .ToList();

        if (states.Count == 0) return HealthState.Unknown;
        if (states.Contains(HealthState.Down)) return HealthState.Down;
        if (states.Contains(HealthState.Degraded)) return HealthState.Degraded;
        if (states.Contains(HealthState.Unknown)) return HealthState.Unknown;
        return HealthState.Healthy;
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value.Substring(0, max) + "…";
}
