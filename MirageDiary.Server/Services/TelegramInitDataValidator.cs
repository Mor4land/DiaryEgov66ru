using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.Extensions.Configuration;

namespace MirageDiary.Server.Services;

public interface ITelegramInitDataValidator
{
    bool ValidateInitData(string initData, out string telegramUserId);
}

public class TelegramInitDataValidator : ITelegramInitDataValidator
{
    private readonly string _botToken;

    public TelegramInitDataValidator(IConfiguration configuration)
    {
        _botToken = configuration["BotToken"] ?? "";
    }

    public bool ValidateInitData(string initData, out string telegramUserId)
    {
        telegramUserId = "";
        if (string.IsNullOrWhiteSpace(initData)) return false;

        try
        {
            var parsed = HttpUtility.ParseQueryString(initData);
            string? hash = parsed["hash"];

            // Extract telegram user id from "user" property in initData
            string? userJson = parsed["user"];
            if (!string.IsNullOrEmpty(userJson))
            {
                using var doc = JsonDocument.Parse(userJson);
                if (doc.RootElement.TryGetProperty("id", out var idProp))
                {
                    telegramUserId = idProp.GetInt64().ToString();
                }
            }

            if (string.IsNullOrEmpty(hash))
            {
                // Fallback for raw userId in development
                return !string.IsNullOrEmpty(telegramUserId);
            }

            if (string.IsNullOrEmpty(_botToken) || _botToken == "YOUR_BOT_TOKEN_HERE")
            {
                return !string.IsNullOrEmpty(telegramUserId);
            }

            // Telegram WebApp HMAC-SHA256 signature verification algorithm:
            // 1. Secret key = HMAC_SHA256("WebAppData", botToken)
            // 2. Data check string = sorted key=value pairs joined by '\n' (excluding 'hash')
            // 3. Calculated hash = HMAC_SHA256(secretKey, data_check_string)
            var keys = parsed.AllKeys.Where(k => k != null && k != "hash").OrderBy(k => k).ToList();
            var dataCheckLines = new List<string>();
            foreach (var key in keys)
            {
                dataCheckLines.Add($"{key}={parsed[key]}");
            }
            string dataCheckString = string.Join("\n", dataCheckLines);

            byte[] secretKey;
            using (var hmacWebData = new HMACSHA256(Encoding.UTF8.GetBytes("WebAppData")))
            {
                secretKey = hmacWebData.ComputeHash(Encoding.UTF8.GetBytes(_botToken));
            }

            byte[] computedHashBytes;
            using (var hmacData = new HMACSHA256(secretKey))
            {
                computedHashBytes = hmacData.ComputeHash(Encoding.UTF8.GetBytes(dataCheckString));
            }

            string computedHashHex = Convert.ToHexString(computedHashBytes).ToLowerInvariant();
            return string.Equals(computedHashHex, hash, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
