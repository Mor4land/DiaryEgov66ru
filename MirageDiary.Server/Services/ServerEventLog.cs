using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace MirageDiary.Server.Services;

public enum ServerEventLevel
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>
/// Кольцевой буфер настоящих событий сервиса в памяти процесса.
/// Заменяет захардкоженный список событий, который раньше отдавала админ-консоль.
/// История живёт только в текущем запуске — консоль обязана это показывать честно.
/// </summary>
public class ServerEventLog
{
    private const int Capacity = 200;
    private readonly ConcurrentQueue<ServerEvent> _events = new();
    public DateTime StartedAtUtc { get; } = DateTime.UtcNow;

    public record ServerEvent(
        DateTime TimestampUtc,
        ServerEventLevel Level,
        string Source,
        string Message,
        string? Actor);

    public void Add(ServerEventLevel level, string source, string message, string? actor = null)
    {
        _events.Enqueue(new ServerEvent(DateTime.UtcNow, level, source, message, actor));
        while (_events.Count > Capacity && _events.TryDequeue(out _)) { }
    }

    public void Info(string source, string message, string? actor = null) => Add(ServerEventLevel.Info, source, message, actor);
    public void Success(string source, string message, string? actor = null) => Add(ServerEventLevel.Success, source, message, actor);
    public void Warning(string source, string message, string? actor = null) => Add(ServerEventLevel.Warning, source, message, actor);
    public void Error(string source, string message, string? actor = null) => Add(ServerEventLevel.Error, source, message, actor);

    /// <summary>Последние события, новые первыми.</summary>
    public IReadOnlyList<ServerEvent> GetRecent(int count = 30) =>
        _events.Reverse().Take(Math.Clamp(count, 1, Capacity)).ToList();

    public int Count => _events.Count;
}
