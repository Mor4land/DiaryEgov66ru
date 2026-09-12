namespace MirageDiary.Core;

public class TokenData
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string StudentId { get; set; } = "";
    public string Oid { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string DeviceId { get; set; } = "";
}

public interface ITokenStore
{
    /// <summary>
    /// Gets the tokens and student ID for the given user.
    /// In a server context, userId could be the Telegram User ID.
    /// </summary>
    Task<TokenData> GetTokensAsync(string userId);

    /// <summary>
    /// Saves the updated tokens.
    /// </summary>
    Task SaveTokensAsync(string userId, TokenData tokenData);

    /// <summary>
    /// Purges/revokes stored tokens for the given user upon authorization revocation (401/403).
    /// </summary>
    Task RevokeTokensAsync(string userId);
}
