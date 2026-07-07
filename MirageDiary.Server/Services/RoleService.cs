using Microsoft.Extensions.Configuration;
using MirageDiary.Core;
using MirageDiary.Server.Data;
using MirageDiary.Server.Models;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace MirageDiary.Server.Services;

public class RoleService
{
    private readonly IConfiguration _config;
    private readonly AppDbContext _db;

    public RoleService(IConfiguration config, AppDbContext db)
    {
        _config = config;
        _db = db;
    }

    public async Task<string> GetUserRoleAsync(string telegramId)
    {
        // 1. Check if user is in admins.json
        var admins = _config.GetSection("Admins").Get<string[]>();
        if (admins != null && admins.Contains(telegramId))
        {
            return "Admin";
        }

        // 2. Check DB for HeadTeacher or Student
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == telegramId);
        if (user != null && !string.IsNullOrEmpty(user.Role))
        {
            return user.Role;
        }

        // Default
        return "Student";
    }
}
