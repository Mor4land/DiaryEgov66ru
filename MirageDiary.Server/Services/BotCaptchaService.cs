using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Caching.Memory;
using MirageDiary.Server.Models;

namespace MirageDiary.Server.Services;

/// <summary>
/// Provides dynamic anti-bot verification (captcha) with emoji matching and math challenges.
/// </summary>
public class BotCaptchaService
{
    private readonly IMemoryCache _cache;

    private static readonly (string Id, string Emoji, string Name)[] EmojiPool =
    {
        ("cat", "🐱", "Кот"),
        ("dog", "🐶", "Собака"),
        ("fox", "🦊", "Лиса"),
        ("bear", "🐻", "Медведь"),
        ("panda", "🐼", "Панда"),
        ("lion", "🦁", "Лев"),
        ("frog", "🐸", "Лягушка"),
        ("apple", "🍎", "Яблоко"),
        ("rocket", "🚀", "Ракета"),
        ("car", "🚗", "Машина"),
        ("ball", "⚽", "Мяч"),
        ("star", "⭐", "Звезда"),
        ("sun", "☀️", "Солнце"),
        ("flower", "🌸", "Цветок"),
        ("book", "📚", "Книга"),
        ("pizza", "🍕", "Пицца")
    };

    public BotCaptchaService(IMemoryCache? cache = null)
    {
        _cache = cache ?? new MemoryCache(new MemoryCacheOptions { SizeLimit = 10000 });
    }

    /// <summary>
    /// Generates a randomized captcha challenge screen and caches the expected answer.
    /// </summary>
    public BotScreen GenerateCaptchaScreen(string userId)
    {
        // 30% chance for simple math, 70% chance for emoji recognition
        bool isMath = Random.Shared.Next(10) < 3;

        return isMath ? GenerateMathCaptcha(userId) : GenerateEmojiCaptcha(userId);
    }

    private BotScreen GenerateEmojiCaptcha(string userId)
    {
        var targetIndex = Random.Shared.Next(EmojiPool.Length);
        var target = EmojiPool[targetIndex];

        var distractors = EmojiPool
            .Where(e => e.Id != target.Id)
            .OrderBy(_ => Random.Shared.Next())
            .Take(3)
            .ToList();

        var options = new List<(string Id, string Emoji, string Name)> { target };
        options.AddRange(distractors);
        options = options.OrderBy(_ => Random.Shared.Next()).ToList();

        // Store expected answer
        _cache.Set($"captcha:{userId}", target.Id, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10),
            Size = 1
        });

        var text = "🤖 *Проверка безопасности (Капча)*\n\n" +
                   "Для защиты от спама и автоматических ботов, пожалуйста, подтвердите, что вы человек.\n\n" +
                   $"👉 Выберите кнопку с символом: **{target.Emoji} {target.Name}**";

        var buttons = new List<List<BotButton>>
        {
            new()
            {
                BotButton.Callback($"{options[0].Emoji} {options[0].Name}", $"cmd_captcha:{options[0].Id}"),
                BotButton.Callback($"{options[1].Emoji} {options[1].Name}", $"cmd_captcha:{options[1].Id}")
            },
            new()
            {
                BotButton.Callback($"{options[2].Emoji} {options[2].Name}", $"cmd_captcha:{options[2].Id}"),
                BotButton.Callback($"{options[3].Emoji} {options[3].Name}", $"cmd_captcha:{options[3].Id}")
            }
        };

        return BotScreen.Create(text, "banner_start.png").WithButtons(buttons);
    }

    private BotScreen GenerateMathCaptcha(string userId)
    {
        int a = Random.Shared.Next(2, 9);
        int b = Random.Shared.Next(1, 9);
        int sum = a + b;

        var distractors = new HashSet<int>();
        while (distractors.Count < 3)
        {
            int offset = Random.Shared.Next(-3, 4);
            int candidate = sum + offset;
            if (candidate > 0 && candidate != sum)
            {
                distractors.Add(candidate);
            }
        }

        var options = new List<int> { sum };
        options.AddRange(distractors);
        options = options.OrderBy(_ => Random.Shared.Next()).ToList();

        string answerId = $"math_{sum}";
        _cache.Set($"captcha:{userId}", answerId, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10),
            Size = 1
        });

        var text = "🤖 *Проверка безопасности (Капча)*\n\n" +
                   "Для защиты от спама и автоматических ботов, пожалуйста, подтвердите, что вы человек.\n\n" +
                   $"👉 Решите простой пример: **{a} + {b} = ?**";

        var buttons = new List<List<BotButton>>
        {
            new()
            {
                BotButton.Callback($"🔢 {options[0]}", $"cmd_captcha:math_{options[0]}"),
                BotButton.Callback($"🔢 {options[1]}", $"cmd_captcha:math_{options[1]}")
            },
            new()
            {
                BotButton.Callback($"🔢 {options[2]}", $"cmd_captcha:math_{options[2]}"),
                BotButton.Callback($"🔢 {options[3]}", $"cmd_captcha:math_{options[3]}")
            }
        };

        return BotScreen.Create(text, "banner_start.png").WithButtons(buttons);
    }

    /// <summary>
    /// Validates the submitted captcha answer.
    /// </summary>
    public bool ValidateCaptcha(string userId, string answerId)
    {
        if (string.IsNullOrWhiteSpace(answerId)) return false;

        string key = $"captcha:{userId}";
        if (_cache.TryGetValue(key, out string? expectedAnswer))
        {
            _cache.Remove(key);
            return string.Equals(expectedAnswer, answerId, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>
    /// Sets an explicit expected answer for testing purposes.
    /// </summary>
    public void SetExpectedAnswer(string userId, string answerId)
    {
        _cache.Set($"captcha:{userId}", answerId, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10),
            Size = 1
        });
    }

    /// <summary>
    /// Clears any cached captcha state for the given user.
    /// </summary>
    public void ClearCaptcha(string userId)
    {
        _cache.Remove($"captcha:{userId}");
    }
}
