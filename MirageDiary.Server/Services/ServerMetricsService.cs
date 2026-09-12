using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace MirageDiary.Server.Services;

/// <summary>
/// Счётчики трафика в памяти процесса: попадания в кэш класса против прямых вызовов API дневника.
/// Растут только от настоящих запросов. История начинается с момента старта процесса
/// и обнуляется при рестарте — консоль показывает это как есть, ничего не досочиняя.
/// </summary>
public class ServerMetricsService
{
    /// <summary>Часовой пояс аудитории: Екатеринбург, UTC+5.</summary>
    private static readonly TimeSpan LocalOffset = TimeSpan.FromHours(5);

    private readonly ConcurrentDictionary<long, MetricBucket> _hourlyBuckets = new();
    private long _totalCacheHits;
    private long _totalExternalCalls;
    private long _totalTelegramCommands;

    /// <summary>Момент старта счётчиков. До него данных нет и быть не может.</summary>
    public DateTime CountersStartedAtUtc { get; } = DateTime.UtcNow;

    public long TotalTelegramCommands => System.Threading.Interlocked.Read(ref _totalTelegramCommands);

    public class MetricBucket
    {
        public long CacheHits;
        public long ExternalCalls;
        public long TelegramMessages;
    }

    private static DateTime LocalNow => DateTime.UtcNow + LocalOffset;

    private static long GetHourKey(DateTime dt)
    {
        // YYYYMMDDHH
        return (dt.Year * 1000000L) + (dt.Month * 10000L) + (dt.Day * 100L) + dt.Hour;
    }

    private MetricBucket CurrentBucket() =>
        _hourlyBuckets.GetOrAdd(GetHourKey(LocalNow), _ => new MetricBucket());

    public void RecordCacheHit()
    {
        System.Threading.Interlocked.Increment(ref _totalCacheHits);
        System.Threading.Interlocked.Increment(ref CurrentBucket().CacheHits);
    }

    public void RecordExternalCall()
    {
        System.Threading.Interlocked.Increment(ref _totalExternalCalls);
        System.Threading.Interlocked.Increment(ref CurrentBucket().ExternalCalls);
    }

    public void RecordTelegramMessage()
    {
        System.Threading.Interlocked.Increment(ref _totalTelegramCommands);
        System.Threading.Interlocked.Increment(ref CurrentBucket().TelegramMessages);
    }

    public TrafficTimelineDto GetTimeline(int hours = 12)
    {
        var now = LocalNow;
        var labels = new List<string>();
        var cacheHits = new List<long?>();
        var externalCalls = new List<long?>();

        // Часы до старта счётчиков остаются null: в них не «ноль запросов», в них нет данных.
        var startedLocal = CountersStartedAtUtc + LocalOffset;
        var coveredFrom = new DateTime(startedLocal.Year, startedLocal.Month, startedLocal.Day, startedLocal.Hour, 0, 0);

        for (int i = hours - 1; i >= 0; i--)
        {
            var t = now.AddHours(-i);
            var hourStart = new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0);
            labels.Add(t.ToString("HH:00"));

            if (hourStart < coveredFrom)
            {
                cacheHits.Add(null);
                externalCalls.Add(null);
                continue;
            }

            if (_hourlyBuckets.TryGetValue(GetHourKey(t), out var b))
            {
                cacheHits.Add(b.CacheHits);
                externalCalls.Add(b.ExternalCalls);
            }
            else
            {
                cacheHits.Add(0);
                externalCalls.Add(0);
            }
        }

        long sumHits = System.Threading.Interlocked.Read(ref _totalCacheHits);
        long sumCalls = System.Threading.Interlocked.Read(ref _totalExternalCalls);
        long total = sumHits + sumCalls;

        return new TrafficTimelineDto
        {
            Labels = labels,
            CacheHits = cacheHits,
            ExternalCalls = externalCalls,
            TotalCacheHits = sumHits,
            TotalExternalCalls = sumCalls,
            // Пока не было ни одного запроса, доля попаданий не определена. Не 100%, не 0% — её нет.
            HitRatePercent = total > 0 ? Math.Round((double)sumHits / total * 100.0, 1) : (double?)null,
            HasData = total > 0,
            CountersStartedAtUtc = CountersStartedAtUtc.ToString("yyyy-MM-dd HH:mm:ss"),
            CountersUptimeHours = Math.Round((DateTime.UtcNow - CountersStartedAtUtc).TotalHours, 1)
        };
    }
}

public class TrafficTimelineDto
{
    public List<string> Labels { get; set; } = new();

    /// <summary>Значения по часам. null означает «час до старта счётчиков», данных за него нет.</summary>
    public List<long?> CacheHits { get; set; } = new();
    public List<long?> ExternalCalls { get; set; } = new();

    public long TotalCacheHits { get; set; }
    public long TotalExternalCalls { get; set; }

    /// <summary>null, пока не было ни одного запроса.</summary>
    public double? HitRatePercent { get; set; }

    public bool HasData { get; set; }
    public string CountersStartedAtUtc { get; set; } = "";
    public double CountersUptimeHours { get; set; }
}
