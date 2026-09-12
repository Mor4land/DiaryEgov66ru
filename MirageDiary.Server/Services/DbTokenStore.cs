using System.Threading.Tasks;
using MirageDiary.Core;
using MirageDiary.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace MirageDiary.Server.Services;

public class DbTokenStore : ITokenStore
{
    private readonly AppDbContext _context;
    private readonly ITokenEncryptionService _encryption;

    public DbTokenStore(AppDbContext context, ITokenEncryptionService encryption)
    {
        _context = context;
        _encryption = encryption;
    }

    public async Task<TokenData> GetTokensAsync(string userId)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null)
        {
            return null;
        }

        string access = _encryption.Decrypt(user.AccessToken) ?? "";
        string refresh = _encryption.Decrypt(user.RefreshToken) ?? "";

        // If decryption returned raw ciphertext or failed, treat as empty
        if (access.StartsWith("enc:v1:")) access = "";
        if (refresh.StartsWith("enc:v1:")) refresh = "";

        return new TokenData
        {
            AccessToken = access,
            RefreshToken = refresh,
            StudentId = user.StudentId ?? "",
            Oid = user.Oid ?? "",
            SessionId = user.SessionId ?? "",
            DeviceId = user.DeviceId ?? ""
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

        string access = tokenData?.AccessToken ?? "";
        string refresh = tokenData?.RefreshToken ?? "";

        user.AccessToken = string.IsNullOrEmpty(access) ? "" : _encryption.Encrypt(access);
        user.RefreshToken = string.IsNullOrEmpty(refresh) ? "" : _encryption.Encrypt(refresh);
        user.StudentId = tokenData?.StudentId ?? "";

        if (!string.IsNullOrEmpty(tokenData?.Oid))
            user.Oid = tokenData.Oid;
        if (!string.IsNullOrEmpty(tokenData?.SessionId))
            user.SessionId = tokenData.SessionId;
        if (!string.IsNullOrEmpty(tokenData?.DeviceId))
            user.DeviceId = tokenData.DeviceId;

        await _context.SaveChangesAsync();
    }

    public async Task RevokeTokensAsync(string userId)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user != null)
        {
            user.AccessToken = "";
            user.RefreshToken = "";
            await _context.SaveChangesAsync();
        }
    }
}
