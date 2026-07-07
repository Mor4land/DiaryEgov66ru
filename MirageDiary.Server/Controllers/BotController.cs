using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using Telegram.Bot.Types;
using MirageDiary.Server.Services;
using System;

namespace MirageDiary.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class BotController : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Post([FromBody] Update update, [FromServices] BotCommandService handleUpdateService)
    {
        Console.WriteLine($"[WEBHOOK] Received update. Type: {update.Type}");
        try
        {
            await handleUpdateService.HandleUpdateAsync(update);
        }
        catch (Exception ex)
        {
            // For webhook endpoints, we generally want to return Ok() even on errors
            // to prevent Telegram from retrying the failing update repeatedly
            Console.WriteLine($"Error handling update: {ex.Message}");
        }
        
        return Ok();
    }
}
