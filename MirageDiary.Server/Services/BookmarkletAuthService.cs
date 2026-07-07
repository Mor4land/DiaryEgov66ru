using System;
using System.Collections.Concurrent;

namespace MirageDiary.Server.Services;

/// <summary>
/// Manages short-lived one-time auth codes that link a Telegram user
/// to a pending bookmarklet authentication request.
/// </summary>
public class BookmarkletAuthService
{
    private record PendingAuth(string TelegramId, DateTime ExpiresAt);
    private record CompletedAuth(DateTime CompletedAt);

    // code → pending auth (waiting for user to use bookmarklet)
    private readonly ConcurrentDictionary<string, PendingAuth> _pending = new();

    // code → completed (tokens saved; Mini App polls until it sees this)
    private readonly ConcurrentDictionary<string, CompletedAuth> _completed = new();

    /// <summary>Create a new 6-char code for the given Telegram user (TTL 10 min).</summary>
    public string CreateCode(string telegramId)
    {
        // Clean up expired codes
        var now = DateTime.UtcNow;
        foreach (var kv in _pending)
            if (kv.Value.ExpiresAt < now) _pending.TryRemove(kv.Key, out _);

        var code = Guid.NewGuid().ToString("N")[..8].ToUpper(); // e.g. "A3F9B2C1"
        _pending[code] = new PendingAuth(telegramId, now.AddMinutes(10));
        return code;
    }

    /// <summary>Returns the Telegram ID for a valid, unused code, or null if invalid/expired.</summary>
    public string? ClaimCode(string code)
    {
        if (_pending.TryGetValue(code, out var auth) && auth.ExpiresAt > DateTime.UtcNow)
        {
            _pending.TryRemove(code, out _);
            _completed[code] = new CompletedAuth(DateTime.UtcNow);
            return auth.TelegramId;
        }
        return null;
    }

    /// <summary>Checks whether the given code has been successfully used. Removes it after first check.</summary>
    public bool IsCompleted(string code)
    {
        if (_completed.TryRemove(code, out _))
            return true;
        // Also return true if code was already used (no longer in pending)
        return !_pending.ContainsKey(code);
    }

    public bool IsPending(string code) => _pending.ContainsKey(code);
}
