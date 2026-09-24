using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Configuration;
using MirageDiary.Server.Models;
using MirageDiary.Server.Services;

namespace MirageDiary.Server.Data;

public class AppDbContext : DbContext
{
    private readonly ITokenEncryptionService? _encryption;

    public DbSet<UserEntity> Users { get; set; }
    public DbSet<CachedData> Cache { get; set; }
    public DbSet<ScheduleEntity> Schedules { get; set; }

    public AppDbContext(DbContextOptions<AppDbContext> options, ITokenEncryptionService? encryption = null) : base(options)
    {
        _encryption = encryption;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserEntity>().HasKey(u => u.Id);
        modelBuilder.Entity<CachedData>().HasKey(c => c.CacheKey);
        modelBuilder.Entity<ScheduleEntity>().HasKey(s => s.OrgName);

        // Fallback encryption service in case context created without DI (e.g. CLI tools)
        var encryption = _encryption ?? new TokenEncryptionService(new ConfigurationBuilder().Build());

        var piiConverter = new ValueConverter<string, string>(
            v => string.IsNullOrEmpty(v) ? v : encryption.Encrypt(v),
            v => string.IsNullOrEmpty(v) ? v : encryption.Decrypt(v)
        );

        modelBuilder.Entity<UserEntity>().Property(u => u.FirstName).HasConversion(piiConverter);
        modelBuilder.Entity<UserEntity>().Property(u => u.LastName).HasConversion(piiConverter);
        modelBuilder.Entity<UserEntity>().Property(u => u.OrgName).HasConversion(piiConverter);
        modelBuilder.Entity<UserEntity>().Property(u => u.ClassName).HasConversion(piiConverter);
        modelBuilder.Entity<UserEntity>().Property(u => u.AccessToken).HasConversion(piiConverter);
        modelBuilder.Entity<UserEntity>().Property(u => u.RefreshToken).HasConversion(piiConverter);
    }
}
