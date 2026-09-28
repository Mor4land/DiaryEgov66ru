using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MirageDiary.Server.Data;
using MirageDiary.Server.Services;

namespace MirageDiary.Server.Data;

public static class DatabaseInitializer
{
    public static async Task InitializeDatabaseAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseInitializer");
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        try
        {
            // Take pre-migration atomic backup snapshot
            var backupService = scope.ServiceProvider.GetRequiredService<DatabaseBackupService>();
            await backupService.CreateBackupAsync("pre_init");

            var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
            {
                await conn.OpenAsync();
            }

            using var cmd = conn.CreateCommand();

            // 0. High-concurrency database pragmas: WAL mode enables simultaneous reads & writes
            cmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = 5000;";
            await cmd.ExecuteNonQueryAsync();
            logger.LogInformation("[DatabaseInitializer] ⚡ SQLite WAL mode and busy_timeout (5000ms) configured.");

            // 1. Create Users table if not exists
            cmd.CommandText = @"CREATE TABLE IF NOT EXISTS ""Users"" (
                ""Id"" TEXT NOT NULL CONSTRAINT ""PK_Users"" PRIMARY KEY,
                ""AccessToken"" TEXT NOT NULL DEFAULT '',
                ""RefreshToken"" TEXT NOT NULL DEFAULT '',
                ""StudentId"" TEXT NOT NULL DEFAULT '',
                ""Oid"" TEXT NOT NULL DEFAULT '',
                ""SessionId"" TEXT NOT NULL DEFAULT '',
                ""DeviceId"" TEXT NOT NULL DEFAULT '',
                ""ClassName"" TEXT NOT NULL DEFAULT '',
                ""OrgName"" TEXT NOT NULL DEFAULT '',
                ""Role"" TEXT NOT NULL DEFAULT 'Student',
                ""IsAlarmEnabled"" INTEGER NOT NULL DEFAULT 1,
                ""IsScheduleReminderEnabled"" INTEGER NOT NULL DEFAULT 1,
                ""ScheduleReminderTime"" TEXT NOT NULL DEFAULT '07:30',
                ""LastScheduleReminderDate"" TEXT NOT NULL DEFAULT '',
                ""IsHomeworkReminderEnabled"" INTEGER NOT NULL DEFAULT 1,
                ""HomeworkReminderTime"" TEXT NOT NULL DEFAULT '17:00',
                ""LastHomeworkReminderDate"" TEXT NOT NULL DEFAULT '',
                ""IsEquipmentReminderEnabled"" INTEGER NOT NULL DEFAULT 1,
                ""EquipmentReminderTime"" TEXT NOT NULL DEFAULT '20:30',
                ""LastEquipmentReminderDate"" TEXT NOT NULL DEFAULT '',
                ""IsGradeWatcherEnabled"" INTEGER NOT NULL DEFAULT 1,
                ""UtcOffsetHours"" INTEGER NOT NULL DEFAULT 5,
                ""SelectedStudentId"" TEXT NOT NULL DEFAULT '',
                ""IsSubscribed"" INTEGER NOT NULL DEFAULT 0,
                ""SubscriptionExpiresAtUtc"" TEXT NULL,
                ""LastMarksHash"" TEXT NOT NULL DEFAULT '',
                ""Shift"" INTEGER NOT NULL DEFAULT 1,
                ""HasAcceptedPrivacyPolicy"" INTEGER NOT NULL DEFAULT 0,
                ""PrivacyPolicyAcceptedAtUtc"" TEXT NULL,
                ""HasPassedCaptcha"" INTEGER NOT NULL DEFAULT 0,
                ""HasCompletedOnboarding"" INTEGER NOT NULL DEFAULT 0,
                ""Username"" TEXT NOT NULL DEFAULT '',
                ""FirstName"" TEXT NOT NULL DEFAULT '',
                ""LastName"" TEXT NOT NULL DEFAULT '',
                ""Platform"" TEXT NOT NULL DEFAULT 'telegram',
                ""LinkedUserId"" TEXT NOT NULL DEFAULT '',
                ""NotificationChannel"" TEXT NOT NULL DEFAULT '',
                ""UpdatedAt"" TEXT NULL
            );";
            await cmd.ExecuteNonQueryAsync();

            // 2. Create Cache table if not exists
            cmd.CommandText = @"CREATE TABLE IF NOT EXISTS ""Cache"" (
                ""CacheKey"" TEXT NOT NULL CONSTRAINT ""PK_Cache"" PRIMARY KEY,
                ""JsonData"" TEXT NOT NULL DEFAULT '',
                ""UpdatedAt"" TEXT NOT NULL DEFAULT ''
            );";
            await cmd.ExecuteNonQueryAsync();

            // 2. Create Schedules table if not exists
            cmd.CommandText = @"CREATE TABLE IF NOT EXISTS ""Schedules"" (
                ""OrgName"" TEXT NOT NULL CONSTRAINT ""PK_Schedules"" PRIMARY KEY,
                ""FileId"" TEXT NOT NULL DEFAULT '',
                ""FileType"" TEXT NOT NULL DEFAULT '',
                ""UploadedAt"" TEXT NOT NULL DEFAULT ''
            );";
            await cmd.ExecuteNonQueryAsync();

            // 2.5. Create performance indexes (drop index on non-deterministic encrypted columns)
            cmd.CommandText = @"
                DROP INDEX IF EXISTS ""IX_Users_OrgName_ClassName"";
                CREATE INDEX IF NOT EXISTS ""IX_Users_Role"" ON ""Users"" (""Role"");
                CREATE INDEX IF NOT EXISTS ""IX_Cache_UpdatedAt"" ON ""Cache"" (""UpdatedAt"");
                CREATE INDEX IF NOT EXISTS ""IX_Users_Username"" ON ""Users"" (""Username"");
                CREATE INDEX IF NOT EXISTS ""IX_Users_Oid"" ON ""Users"" (""Oid"");
                CREATE INDEX IF NOT EXISTS ""IX_Users_StudentId"" ON ""Users"" (""StudentId"");
            ";
            await cmd.ExecuteNonQueryAsync();

            // 2.6. Proactive SQLite index health check & automated self-healing
            try
            {
                cmd.CommandText = "PRAGMA quick_check;";
                var checkResult = (string?)await cmd.ExecuteScalarAsync();
                if (checkResult != "ok")
                {
                    logger.LogWarning($"[DatabaseInitializer] SQLite integrity warning: {checkResult}. Running automatic REINDEX...");
                    cmd.CommandText = "REINDEX;";
                    await cmd.ExecuteNonQueryAsync();
                    logger.LogInformation("[DatabaseInitializer] SQLite REINDEX completed successfully.");
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[DatabaseInitializer] SQLite quick_check encountered an error; attempting recovery via REINDEX...");
                try
                {
                    cmd.CommandText = "REINDEX;";
                    await cmd.ExecuteNonQueryAsync();
                }
                catch { }
            }

            // 3. Migrate Users table schema safely
            var userCols = new (string Name, string Def)[]
            {
                ("IsAlarmEnabled", "INTEGER NOT NULL DEFAULT 1"),
                ("IsScheduleReminderEnabled", "INTEGER NOT NULL DEFAULT 1"),
                ("ScheduleReminderTime", "TEXT NOT NULL DEFAULT '07:30'"),
                ("LastScheduleReminderDate", "TEXT NOT NULL DEFAULT ''"),
                ("IsHomeworkReminderEnabled", "INTEGER NOT NULL DEFAULT 1"),
                ("HomeworkReminderTime", "TEXT NOT NULL DEFAULT '17:00'"),
                ("LastHomeworkReminderDate", "TEXT NOT NULL DEFAULT ''"),
                ("IsEquipmentReminderEnabled", "INTEGER NOT NULL DEFAULT 1"),
                ("EquipmentReminderTime", "TEXT NOT NULL DEFAULT '20:30'"),
                ("LastEquipmentReminderDate", "TEXT NOT NULL DEFAULT ''"),
                ("IsGradeWatcherEnabled", "INTEGER NOT NULL DEFAULT 1"),
                ("UtcOffsetHours", "INTEGER NOT NULL DEFAULT 5"),
                ("Oid", "TEXT NOT NULL DEFAULT ''"),
                ("SessionId", "TEXT NOT NULL DEFAULT ''"),
                ("DeviceId", "TEXT NOT NULL DEFAULT ''"),
                ("IsSubscribed", "INTEGER NOT NULL DEFAULT 0"),
                ("SubscriptionExpiresAtUtc", "TEXT NULL"),
                ("LastMarksHash", "TEXT NOT NULL DEFAULT ''"),
                ("Shift", "INTEGER NOT NULL DEFAULT 1"),
                ("HasAcceptedPrivacyPolicy", "INTEGER NOT NULL DEFAULT 0"),
                ("PrivacyPolicyAcceptedAtUtc", "TEXT NULL"),
                ("HasPassedCaptcha", "INTEGER NOT NULL DEFAULT 0"),
                ("HasCompletedOnboarding", "INTEGER NOT NULL DEFAULT 0"),
                ("Username", "TEXT NOT NULL DEFAULT ''"),
                ("FirstName", "TEXT NOT NULL DEFAULT ''"),
                ("LastName", "TEXT NOT NULL DEFAULT ''"),
                ("Platform", "TEXT NOT NULL DEFAULT 'telegram'"),
                ("LinkedUserId", "TEXT NOT NULL DEFAULT ''"),
                ("NotificationChannel", "TEXT NOT NULL DEFAULT ''"),
                ("SelectedStudentId", "TEXT NOT NULL DEFAULT ''"),
                ("UpdatedAt", "TEXT NULL")
            };

            foreach (var col in userCols)
            {
                cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('Users') WHERE name='{col.Name}';";
                var exists = (long)(await cmd.ExecuteScalarAsync() ?? 0L);
                if (exists == 0)
                {
                    cmd.CommandText = $"ALTER TABLE Users ADD COLUMN {col.Name} {col.Def};";
                    await cmd.ExecuteNonQueryAsync();
                    logger.LogInformation("[DatabaseInitializer] Added column '{Column}' to Users table.", col.Name);
                }
            }

            // Ensure existing active users who already consented or logged in bypass captcha and onboarding
            cmd.CommandText = @"UPDATE ""Users"" SET ""HasPassedCaptcha"" = 1, ""HasCompletedOnboarding"" = 1 WHERE ""HasAcceptedPrivacyPolicy"" = 1 OR (""AccessToken"" IS NOT NULL AND ""AccessToken"" != '');";
            await cmd.ExecuteNonQueryAsync();

            // Ensure UpdatedAt is populated for all existing rows (give them 30-day grace period from today)
            cmd.CommandText = @"UPDATE ""Users"" SET ""UpdatedAt"" = strftime('%Y-%m-%d %H:%M:%fZ', 'now') WHERE ""UpdatedAt"" IS NULL;";
            await cmd.ExecuteNonQueryAsync();

            // Index on UpdatedAt
            cmd.CommandText = @"CREATE INDEX IF NOT EXISTS ""IX_Users_UpdatedAt"" ON ""Users"" (""UpdatedAt"");";
            await cmd.ExecuteNonQueryAsync();

            // 4. Auto-encrypt or upgrade tokens and PII in DB to envelope encryption (enc:v2)
            var encryption = scope.ServiceProvider.GetRequiredService<ITokenEncryptionService>();

            var unencryptedUserIds = new HashSet<string>();
            using (var checkCmd = conn.CreateCommand())
            {
                checkCmd.CommandText = @"SELECT ""Id"", ""FirstName"", ""LastName"", ""OrgName"", ""ClassName"" FROM ""Users"";";
                using var reader = await checkCmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    string id = reader.GetString(0);
                    string? fn = reader.IsDBNull(1) ? null : reader.GetString(1);
                    string? ln = reader.IsDBNull(2) ? null : reader.GetString(2);
                    string? org = reader.IsDBNull(3) ? null : reader.GetString(3);
                    string? cls = reader.IsDBNull(4) ? null : reader.GetString(4);

                    bool needsMigration =
                        (!string.IsNullOrEmpty(fn) && !fn.StartsWith("enc:v2:")) ||
                        (!string.IsNullOrEmpty(ln) && !ln.StartsWith("enc:v2:")) ||
                        (!string.IsNullOrEmpty(org) && !org.StartsWith("enc:v2:")) ||
                        (!string.IsNullOrEmpty(cls) && !cls.StartsWith("enc:v2:"));

                    if (needsMigration)
                    {
                        unencryptedUserIds.Add(id);
                    }
                }
            }

            var usersToEncrypt = await db.Users.ToListAsync();
            bool updated = false;

            foreach (var u in usersToEncrypt)
            {
                if (!string.IsNullOrEmpty(u.AccessToken) && !u.AccessToken.StartsWith("enc:v2:"))
                {
                    var plain = encryption.Decrypt(u.AccessToken);
                    if (!string.IsNullOrEmpty(plain))
                    {
                        u.AccessToken = encryption.Encrypt(plain);
                        updated = true;
                    }
                }
                if (!string.IsNullOrEmpty(u.RefreshToken) && !u.RefreshToken.StartsWith("enc:v2:"))
                {
                    var plain = encryption.Decrypt(u.RefreshToken);
                    if (!string.IsNullOrEmpty(plain))
                    {
                        u.RefreshToken = encryption.Encrypt(plain);
                        updated = true;
                    }
                }

                if (unencryptedUserIds.Contains(u.Id))
                {
                    if (!string.IsNullOrEmpty(u.FirstName)) db.Entry(u).Property(x => x.FirstName).IsModified = true;
                    if (!string.IsNullOrEmpty(u.LastName)) db.Entry(u).Property(x => x.LastName).IsModified = true;
                    if (!string.IsNullOrEmpty(u.OrgName)) db.Entry(u).Property(x => x.OrgName).IsModified = true;
                    if (!string.IsNullOrEmpty(u.ClassName)) db.Entry(u).Property(x => x.ClassName).IsModified = true;
                    updated = true;
                }
            }

            if (updated)
            {
                await db.SaveChangesAsync();
                logger.LogInformation("[DatabaseInitializer] Tokens and PII fields upgraded to envelope encryption (enc:v2) successfully.");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[DatabaseInitializer] Database initialization error");
            throw;
        }
    }
}
