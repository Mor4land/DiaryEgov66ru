using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MirageDiary.Server.Data;
using MirageDiary.Server.Models;

namespace MirageDiary.Server.Services;

public class P2PCacheService
{
    private readonly AppDbContext _context;

    public P2PCacheService(AppDbContext context)
    {
        _context = context;
    }

    public string GenerateKey(string orgName, string className, string date)
    {
        return $"{orgName}_{className}_{date}";
    }

    public async Task<string> GetCachedDataAsync(string cacheKey)
    {
        var cacheEntry = await _context.Cache.FirstOrDefaultAsync(c => c.CacheKey == cacheKey);
        
        // Cache valid for 1 day basically, or we can just return it if it exists.
        if (cacheEntry != null)
        {
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
