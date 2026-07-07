using System.Threading.Tasks;
using MirageDiary.Core;
using MirageDiary.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace MirageDiary.Server.Services;

public class DbTokenStore : ITokenStore
{
    private readonly AppDbContext _context;

    public DbTokenStore(AppDbContext context)
    {
        _context = context;
    }

    public async Task<TokenData> GetTokensAsync(string userId)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null)
        {
            return null;
        }

        return new TokenData
        {
            AccessToken = user.AccessToken,
            RefreshToken = user.RefreshToken,
            StudentId = user.StudentId
        };
    }

    public async Task SaveTokensAsync(string userId, TokenData tokenData)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null)
        {
            user = new Models.UserEntity
            {
                Id = userId,
                Role = "Student" // Default
            };
            _context.Users.Add(user);
        }

        user.AccessToken = tokenData.AccessToken;
        user.RefreshToken = tokenData.RefreshToken;
        user.StudentId = tokenData.StudentId;

        await _context.SaveChangesAsync();
    }
}
