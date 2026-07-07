using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using MirageDiary.Server.Services;
using System;

namespace MirageDiary.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MaxBotController : ControllerBase
{
    // Adjust Request object depending on Max.Bot.SDK specifications
    [HttpPost]
    public async Task<IActionResult> Post([FromBody] MaxBotUpdateMock update, [FromServices] MaxCommandService handleUpdateService)
    {
        try
        {
            if (!string.IsNullOrEmpty(update.ChatId) && !string.IsNullOrEmpty(update.Text))
            {
                await handleUpdateService.HandleMessageAsync(update.ChatId, update.Text);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error handling MAX update: {ex.Message}");
        }
        
        return Ok();
    }
}

// Temporary mock until exact Max.Bot.SDK type is known
public class MaxBotUpdateMock
{
    public string ChatId { get; set; }
    public string Text { get; set; }
}
