using Microsoft.EntityFrameworkCore;
using MirageDiary.Server.Models;

namespace MirageDiary.Server.Data;

public class AppDbContext : DbContext
{
    public DbSet<UserEntity> Users { get; set; }
    public DbSet<CachedData> Cache { get; set; }
    public DbSet<ScheduleEntity> Schedules { get; set; }

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserEntity>().HasKey(u => u.Id);
        modelBuilder.Entity<CachedData>().HasKey(c => c.CacheKey);
        modelBuilder.Entity<ScheduleEntity>().HasKey(s => s.OrgName);
    }
}
