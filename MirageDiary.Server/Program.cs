using Microsoft.EntityFrameworkCore;
using MirageDiary.Server.Data;
using MirageDiary.Server.Extensions;

var builder = WebApplication.CreateBuilder(args);

// 1. Configuration files
builder.Configuration.AddJsonFile("admins.json", optional: true, reloadOnChange: true);
builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "data", "admins.json"), optional: true, reloadOnChange: true);

// 2. Add API Controllers & OpenAPI
builder.Services.AddControllers();
builder.Services.AddOpenApi();

// 3. CORS policy (Hardened for Telegram MiniApp & trusted clients)
builder.Services.AddCors(options =>
{
    options.AddPolicy("MirageCorsPolicy", policy =>
    {
        var configuredOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
        if (configuredOrigins.Length > 0)
        {
            policy.WithOrigins(configuredOrigins)
                  .AllowAnyMethod()
                  .AllowAnyHeader()
                  .AllowCredentials();
        }
        else if (builder.Environment.IsDevelopment())
        {
            policy.SetIsOriginAllowed(_ => true)
                  .AllowAnyMethod()
                  .AllowAnyHeader()
                  .AllowCredentials();
        }
        else
        {
            policy.WithOrigins(
                "https://web.telegram.org",
                "https://myschool.gosuslugi.ru"
            )
            .AllowAnyMethod()
            .AllowAnyHeader();
        }
    });
});

// 4. Database Context (Dual-provider: PostgreSQL or SQLite via standard connection string / env)
var defaultConnStr = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
    ?? Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Data Source=miragediary.db";

var dbProvider = builder.Configuration["DatabaseProvider"]?.ToLowerInvariant()
    ?? (defaultConnStr.StartsWith("Host=", StringComparison.OrdinalIgnoreCase) ||
        defaultConnStr.StartsWith("Server=", StringComparison.OrdinalIgnoreCase) ||
        defaultConnStr.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
        defaultConnStr.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase) ? "npgsql" : "sqlite");

builder.Services.AddDbContext<AppDbContext>(options =>
{
    if (dbProvider is "npgsql" or "postgres" or "postgresql")
    {
        options.UseNpgsql(defaultConnStr);
    }
    else
    {
        options.UseSqlite(defaultConnStr);
    }
});

// 5. Mirage Services, RateLimiting, Telegram Bot, and Scoped TLS Validation
builder.Services.AddMirageServices(builder.Configuration);

// 6. Forwarded Headers for reverse-proxy (trusted local reverse-proxy)
builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | 
                               Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
    options.KnownProxies.Add(System.Net.IPAddress.Loopback);
    options.KnownProxies.Add(System.Net.IPAddress.IPv6Loopback);
});

var app = builder.Build();

// Pipeline configuration
app.UseForwardedHeaders();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors("MirageCorsPolicy");
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthorization();
app.MapControllers();

app.MapGet("/admin", () => Results.Redirect("/admin.html", permanent: false));
app.MapGet("/privacy", () => Results.Redirect("/privacy.html", permanent: false));

app.MapFallbackToFile("index.html");

// Safe database schema migrations and legacy token encryption
await DatabaseInitializer.InitializeDatabaseAsync(app.Services);

app.Run();
