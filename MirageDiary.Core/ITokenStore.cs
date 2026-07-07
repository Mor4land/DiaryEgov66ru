namespace MirageDiary.Core;

public class TokenData
{
    public string AccessToken { get; set; }
    public string RefreshToken { get; set; }
    public string StudentId { get; set; }
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
}
