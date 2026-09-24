using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using MirageDiary.Server.Services;
using Xunit;

namespace MirageDiary.Tests;

public class TelegramInitDataValidatorTests
{
    private const string TestBotToken = "123456789:ABCdefGHIjklMNOpqrsTUVwxyz";

    private static string GenerateValidInitData(string botToken, long userId, string username, long authDate)
    {
        var userJson = $"{{\"id\":{userId},\"first_name\":\"Ivan\",\"username\":\"{username}\"}}";
        var dict = new SortedDictionary<string, string>
        {
            { "auth_date", authDate.ToString() },
            { "query_id", "AAG_test_123" },
            { "user", userJson }
        };

        var dataCheckString = string.Join("\n", dict.Select(kv => $"{kv.Key}={kv.Value}"));

        byte[] secretKey;
        using (var hmacWebData = new HMACSHA256(Encoding.UTF8.GetBytes("WebAppData")))
        {
            secretKey = hmacWebData.ComputeHash(Encoding.UTF8.GetBytes(botToken));
        }

        byte[] hashBytes;
        using (var hmac = new HMACSHA256(secretKey))
        {
            hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(dataCheckString));
        }

        string hashHex = Convert.ToHexString(hashBytes).ToLowerInvariant();

        return $"auth_date={authDate}&query_id=AAG_test_123&user={Uri.EscapeDataString(userJson)}&hash={hashHex}";
    }

    [Fact]
    public void ValidateInitData_ValidSignature_ReturnsTrueAndExtractsUserId()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "BotToken", TestBotToken }
            })
            .Build();

        var validator = new TelegramInitDataValidator(config);
        long expectedUserId = 987654321;
        string validInitData = GenerateValidInitData(TestBotToken, expectedUserId, "testuser", 1700000000);

        bool isValid = validator.ValidateInitData(validInitData, out string extractedUserId);

        Assert.True(isValid);
        Assert.Equal(expectedUserId.ToString(), extractedUserId);
    }

    [Fact]
    public void ValidateInitData_TamperedHash_ReturnsFalse()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "BotToken", TestBotToken }
            })
            .Build();

        var validator = new TelegramInitDataValidator(config);
        string validInitData = GenerateValidInitData(TestBotToken, 12345, "hacker", 1700000000);
        string tamperedInitData = validInitData.Substring(0, validInitData.Length - 4) + "ffff";

        bool isValid = validator.ValidateInitData(tamperedInitData, out _);

        Assert.False(isValid);
    }

    [Fact]
    public void ValidateInitData_TamperedDataPayload_ReturnsFalse()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "BotToken", TestBotToken }
            })
            .Build();

        var validator = new TelegramInitDataValidator(config);
        string validInitData = GenerateValidInitData(TestBotToken, 12345, "legit", 1700000000);
        // Change auth_date in query string
        string tamperedInitData = validInitData.Replace("auth_date=1700000000", "auth_date=1799999999");

        bool isValid = validator.ValidateInitData(tamperedInitData, out _);

        Assert.False(isValid);
    }

    [Fact]
    public void ValidateInitData_NullOrEmpty_ReturnsFalse()
    {
        var config = new ConfigurationBuilder().Build();
        var validator = new TelegramInitDataValidator(config);

        Assert.False(validator.ValidateInitData("", out _));
        Assert.False(validator.ValidateInitData(null!, out _));
        Assert.False(validator.ValidateInitData("   ", out _));
    }
}
