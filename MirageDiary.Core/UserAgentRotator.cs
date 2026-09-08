using System;
using System.Collections.Generic;
using System.Threading;

namespace MirageDiary.Core;

public static class UserAgentRotator
{
    private static int _currentIndex = 0;
    private static readonly Random _random = new Random();

    private static readonly string[] UserAgents = new[]
    {
        // Chrome Windows
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",

        // Edge Windows
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36 Edg/123.0.0.0",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36 Edg/122.0.0.0",

        // macOS Chrome & Safari
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.3.1 Safari/605.1.15",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36",

        // Firefox Windows & Linux
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:124.0) Gecko/20100101 Firefox/124.0",
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:123.0) Gecko/20100101 Firefox/123.0",
        "Mozilla/5.0 (X11; Linux x86_64; rv:124.0) Gecko/20100101 Firefox/124.0",

        // Mobile Android & iOS
        "Mozilla/5.0 (Linux; Android 14; SM-S918B) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.6312.40 Mobile Safari/537.36",
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Mobile/15E148 Safari/604.1",
        "Mozilla/5.0 (Linux; Android 14; Pixel 8 Pro) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.6312.40 Mobile Safari/537.36"
    };

    /// <summary>
    /// Gets the next User-Agent in round-robin fashion.
    /// Thread-safe lock-free implementation. Immune to integer overflow.
    /// </summary>
    public static string GetNextUserAgent()
    {
        uint index = unchecked((uint)Interlocked.Increment(ref _currentIndex));
        return UserAgents[index % (uint)UserAgents.Length];
    }

    /// <summary>
    /// Gets a randomly chosen User-Agent from the pool.
    /// </summary>
    public static string GetRandomUserAgent()
    {
        lock (_random)
        {
            return UserAgents[_random.Next(UserAgents.Length)];
        }
    }

    /// <summary>
    /// Gets a consistent User-Agent bound to a specific user ID.
    /// </summary>
    public static string GetUserAgentForUser(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return GetNextUserAgent();
        uint hash = unchecked((uint)userId.GetHashCode());
        return UserAgents[hash % (uint)UserAgents.Length];
    }
}
