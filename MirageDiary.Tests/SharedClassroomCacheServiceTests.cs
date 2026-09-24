using System;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MirageDiary.Server.Data;
using Xunit;

namespace MirageDiary.Tests;

public class SharedClassroomCacheServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly SharedClassroomCacheService _cacheService;

    public SharedClassroomCacheServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();

        _cacheService = new SharedClassroomCacheService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public void GenerateKey_FormatsProperCompoundKey()
    {
        string key = _cacheService.GenerateKey("СОШ 113", "10-А", "2026-09-24");
        Assert.Equal("СОШ 113_10-А_2026-09-24", key);
    }

    [Fact]
    public async Task SaveAndGet_RoundTripsCachedScheduleData()
    {
        string key = "СОШ 113_9-Б_2026-09-25";
        string json = "{\"lessons\":[{\"name\":\"Математика\",\"room\":\"305\"}]}";

        await _cacheService.SaveCachedDataAsync(key, json);
        var cached = await _cacheService.GetCachedDataAsync(key);

        Assert.Equal(json, cached);
    }

    [Fact]
    public async Task Save_OverwritesExistingKey_UpdatesData()
    {
        string key = "school_10_2026-09-24";
        await _cacheService.SaveCachedDataAsync(key, "v1");
        await _cacheService.SaveCachedDataAsync(key, "v2");

        var cached = await _cacheService.GetCachedDataAsync(key);
        Assert.Equal("v2", cached);
    }

    [Fact]
    public async Task Get_ExpiredCache_ReturnsNull()
    {
        string key = "expired_key";
        await _cacheService.SaveCachedDataAsync(key, "data");

        // Set UpdatedAt in past
        var entry = await _db.Cache.FirstAsync(c => c.CacheKey == key);
        entry.UpdatedAt = DateTime.UtcNow.AddMinutes(-30);
        await _db.SaveChangesAsync();

        var cached = await _cacheService.GetCachedDataAsync(key, TimeSpan.FromMinutes(10));
        Assert.Null(cached);
    }
}
