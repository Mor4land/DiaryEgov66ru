using System;
using System.Text.Json;
using System.Threading.Tasks;
using MirageDiary.Core;
using MirageDiary.Server.Data;
using MirageDiary.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace MirageDiary.Server.Services;

public class DiaryInteractionService
{
    private readonly IServiceProvider _serviceProvider;

    public DiaryInteractionService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task<string> GetHomeworkStringAsync(string userId)
    {
        using var scope = _serviceProvider.CreateScope();
        var tokenStore = scope.ServiceProvider.GetRequiredService<ITokenStore>();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var p2pCache = scope.ServiceProvider.GetRequiredService<P2PCacheService>();
        var maintenanceMode = scope.ServiceProvider.GetRequiredService<MaintenanceModeService>();
        
        var tokenData = await tokenStore.GetTokensAsync(userId);
        if (tokenData == null || string.IsNullOrEmpty(tokenData.AccessToken))
        {
            return "Вы не авторизованы. Пожалуйста, откройте Mini App и войдите в систему.";
        }

        var user = await dbContext.Users.FindAsync(userId);
        string orgName = user?.OrgName ?? "Unknown";
        string className = user?.ClassName ?? "Unknown";
        string targetDate = DateTime.UtcNow.ToString("yyyy-MM-dd");

        string cacheKey = p2pCache.GenerateKey(orgName, className, targetDate);

        if (maintenanceMode.IsMaintenanceModeActive())
        {
            return await GetFromCacheFallback(p2pCache, cacheKey, "Дневник на техработах, вывожу закэшированное расписание:\n\n");
        }

        var httpClientFactory = scope.ServiceProvider.GetRequiredService<System.Net.Http.IHttpClientFactory>();
        var client = httpClientFactory.CreateClient();
        var clientWithUser = new DiaryClient(client, tokenStore, userId);

        try
        {
            var hw = await clientWithUser.GetHomeworkAsync(targetDate);
            
            // Save to P2P cache for other students in the class
            var jsonToCache = JsonSerializer.Serialize(hw);
            await p2pCache.SaveCachedDataAsync(cacheKey, jsonToCache);

            if (hw == null || hw.Count == 0)
            {
                return "Домашнего задания на сегодня нет.";
            }

            string response = "📚 Домашнее задание:\n\n";
            foreach (var item in hw)
            {
                response += $"* {item.LessonName}: {item.Description}\n";
            }
            return response;
        }
        catch (DiaryMaintenanceException)
        {
            maintenanceMode.ActivateMaintenanceMode(15);
            return await GetFromCacheFallback(p2pCache, cacheKey, "⚠️ Обнаружены неполадки Дневника. Включен резервный режим на 15 мин.\nВывожу последние закэшированные данные:\n\n");
        }
        catch (Exception ex)
        {
            return $"Ошибка при получении данных: {ex.Message}";
        }
    }

    private async Task<string> GetFromCacheFallback(P2PCacheService cacheService, string cacheKey, string prefix)
    {
        var json = await cacheService.GetCachedDataAsync(cacheKey);
        if (string.IsNullOrEmpty(json))
        {
            return prefix + "К сожалению, в кэше сервера еще нет данных для вашего класса за этот день.";
        }

        try
        {
            var hw = JsonSerializer.Deserialize<System.Collections.Generic.List<HomeworkItem>>(json);
            if (hw == null || hw.Count == 0) return prefix + "Домашнего задания на сегодня нет (из кэша).";

            string response = prefix;
            foreach (var item in hw)
            {
                response += $"* {item.LessonName}: {item.Description}\n";
            }
            return response;
        }
        catch
        {
            return prefix + "Ошибка чтения данных из кэша.";
        }
    }
}
