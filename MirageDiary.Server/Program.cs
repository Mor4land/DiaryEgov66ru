using Microsoft.EntityFrameworkCore;
using MirageDiary.Core;
using MirageDiary.Server.Data;
using MirageDiary.Server.Services;
using Telegram.Bot;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddOpenApi();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", builder =>
    {
        builder.AllowAnyOrigin()
               .AllowAnyMethod()
               .AllowAnyHeader();
    });
});

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=miragediary.db"));

builder.Services.AddHttpClient<DiaryClient>();

// HttpClient for diary reverse-proxy (must NOT auto-follow redirects so we can rewrite Location header)
builder.Services.AddHttpClient("diary-proxy")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false // cookies forwarded manually via headers
    });

builder.Services.AddScoped<ITokenStore, DbTokenStore>();
builder.Services.AddScoped<P2PCacheService>();

builder.Configuration.AddJsonFile("admins.json", optional: true, reloadOnChange: true);

// Configure Telegram Bot
var botToken = builder.Configuration["BotToken"] ?? "YOUR_BOT_TOKEN_HERE";
builder.Services.AddHttpClient("tgwebhook")
    .AddTypedClient<ITelegramBotClient>(httpClient => new TelegramBotClient(botToken, httpClient));
builder.Services.AddSingleton<MaintenanceModeService>();
builder.Services.AddSingleton<BotStateService>();
builder.Services.AddHostedService<SmartAlarmWorker>();
builder.Services.AddScoped<RoleService>();
builder.Services.AddScoped<DiaryInteractionService>();
builder.Services.AddScoped<BotCommandService>();
builder.Services.AddScoped<MaxCommandService>();
builder.Services.AddSingleton<BookmarkletAuthService>();
builder.Services.AddSingleton<PlaywrightAuthManager>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors("AllowAll");
app.UseAuthorization();
app.MapControllers();

// Ensure DB is fully up to date
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var conn = db.Database.GetDbConnection();
    conn.Open();
    using var cmd = conn.CreateCommand();

    // Create Users table if not exists
    cmd.CommandText = @"CREATE TABLE IF NOT EXISTS ""Users"" (
        ""Id"" TEXT NOT NULL CONSTRAINT ""PK_Users"" PRIMARY KEY,
        ""AccessToken"" TEXT NOT NULL,
        ""RefreshToken"" TEXT NOT NULL,
        ""StudentId"" TEXT NOT NULL,
        ""ClassName"" TEXT NOT NULL DEFAULT '',
        ""OrgName"" TEXT NOT NULL DEFAULT '',
        ""Role"" TEXT NOT NULL DEFAULT 'Student',
        ""IsAlarmEnabled"" INTEGER NOT NULL DEFAULT 1
    );";
    cmd.ExecuteNonQuery();

    // Create Cache table if not exists
    cmd.CommandText = @"CREATE TABLE IF NOT EXISTS ""Cache"" (
        ""CacheKey"" TEXT NOT NULL CONSTRAINT ""PK_Cache"" PRIMARY KEY,
        ""JsonData"" TEXT NOT NULL DEFAULT '',
        ""UpdatedAt"" TEXT NOT NULL DEFAULT ''
    );";
    cmd.ExecuteNonQuery();

    // Create Schedules table if not exists
    cmd.CommandText = @"CREATE TABLE IF NOT EXISTS ""Schedules"" (
        ""OrgName"" TEXT NOT NULL CONSTRAINT ""PK_Schedules"" PRIMARY KEY,
        ""FileId"" TEXT NOT NULL DEFAULT '',
        ""FileType"" TEXT NOT NULL DEFAULT '',
        ""UploadedAt"" TEXT NOT NULL DEFAULT ''
    );";
    cmd.ExecuteNonQuery();

    // Add IsAlarmEnabled column to Users if missing (for existing DBs)
    cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Users') WHERE name='IsAlarmEnabled';";
    var colCount = (long)(cmd.ExecuteScalar() ?? 0L);
    if (colCount == 0)
    {
        cmd.CommandText = "ALTER TABLE Users ADD COLUMN IsAlarmEnabled INTEGER NOT NULL DEFAULT 1;";
        cmd.ExecuteNonQuery();
    }

    conn.Close();
}

app.Run();
