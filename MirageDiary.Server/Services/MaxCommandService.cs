using System;
using System.Threading.Tasks;

// Assuming Max.Bot.SDK has similar types, mocking them for compilation if they differ
namespace MirageDiary.Server.Services;

public class MaxCommandService
{
    private readonly DiaryInteractionService _diaryService;

    public MaxCommandService(DiaryInteractionService diaryService)
    {
        _diaryService = diaryService;
    }

    // Mock representation of an update in MAX
    public async Task HandleMessageAsync(string chatId, string text)
    {
        if (text.StartsWith("/start") || text.StartsWith("/menu"))
        {
            await SendMessageToMaxAsync(chatId, "Добро пожаловать в MAX Bot! Откройте Mini App для входа в дневник.");
        }
        else if (text.StartsWith("/homework"))
        {
            await SendMessageToMaxAsync(chatId, "Получаю домашнее задание...");
            
            // Use specific prefix for max if user DB is shared. e.g., "max_12345"
            var response = await _diaryService.GetHomeworkStringAsync(chatId);
            await SendMessageToMaxAsync(chatId, response);
        }
        else if (text.StartsWith("/schedule"))
        {
            await SendMessageToMaxAsync(chatId, "Расписание пока в разработке...");
        }
        else if (text.StartsWith("/alarm"))
        {
            await SendMessageToMaxAsync(chatId, "Функция напоминаний скоро будет доступна.");
        }
    }

    private Task SendMessageToMaxAsync(string chatId, string text)
    {
        // Integration with Max.Bot.SDK goes here.
        Console.WriteLine($"[MAX BOT] To {chatId}: {text}");
        return Task.CompletedTask;
    }
}
