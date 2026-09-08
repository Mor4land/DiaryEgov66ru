using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using MirageDiary.Server.Services;
using System;
using MaxUpdate = MAX.Bot.Interfaces.Models.Update;

namespace MirageDiary.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MaxBotController : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Post([FromBody] MaxUpdate update, [FromServices] MaxCommandService handleUpdateService)
    {
        try
        {
            await handleUpdateService.HandleUpdateAsync(update);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error handling MAX webhook update: {ex.Message}");
        }
        
        return Ok();
    }
}
