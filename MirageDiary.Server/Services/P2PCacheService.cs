using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MirageDiary.Server.Data;
using MirageDiary.Server.Models;

/// <summary>
/// Серверный кэш школьных данных (расписание, ДЗ) по связке Школа-Класс-Дата.
/// Позволяет не долбить API дневника повторными запросами от разных учеников одного класса.
/// (Ранее ошибочно именовался 'P2P', фактически является локальным агрегирующим кэшем класса в SQLite).
/// </summary>
public class SharedClassroomCacheService
{
    private readonly AppDbContext _context;

    public SharedClassroomCacheService(AppDbContext context)
    {
        _context = context;
    }

    public string GenerateKey(string orgName, string className, string date)
    {
        return $"{orgName}_{className}_{date}";
    }

    public async Task<string?> GetCachedDataAsync(string cacheKey, TimeSpan? maxAge = null)
    {
        var cacheEntry = await _context.Cache.FirstOrDefaultAsync(c => c.CacheKey == cacheKey);
        
        if (cacheEntry != null)
        {
            if (maxAge.HasValue && (DateTime.UtcNow - cacheEntry.UpdatedAt) > maxAge.Value)
            {
                return null;
            }
            return cacheEntry.JsonData;
        }

        return null;
    }

    public async Task SaveCachedDataAsync(string cacheKey, string jsonData)
    {
        var cacheEntry = await _context.Cache.FirstOrDefaultAsync(c => c.CacheKey == cacheKey);
        if (cacheEntry == null)
        {
            cacheEntry = new CachedData
            {
                CacheKey = cacheKey,
                JsonData = jsonData,
                UpdatedAt = DateTime.UtcNow
            };
            _context.Cache.Add(cacheEntry);
        }
        else
        {
            cacheEntry.JsonData = jsonData;
            cacheEntry.UpdatedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync();
    }
}

[Obsolete("Use SharedClassroomCacheService instead. Local SQLite class cache is not P2P.")]
public class P2PCacheService : SharedClassroomCacheService
{
    public P2PCacheService(AppDbContext context) : base(context) { }
}
