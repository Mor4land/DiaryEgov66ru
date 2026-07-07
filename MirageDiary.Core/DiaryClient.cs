using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MirageDiary.Core.Models;

namespace MirageDiary.Core;

public class DiaryClient
{
    private readonly HttpClient _client;
    private readonly ITokenStore _tokenStore;
    private readonly string _userId;
    
    private const string BaseUrl = "https://dnevnik.egov66.ru/api/";
    private const string TokenUrl = "https://dnevnik.egov66.ru/api/auth/Token/Refresh";

    public DiaryClient(HttpClient client, ITokenStore tokenStore, string userId)
    {
        _client = client;
        _tokenStore = tokenStore;
        _userId = userId;
    }

    private async Task EnsureAuthorizationAsync()
    {
        var tokenData = await _tokenStore.GetTokensAsync(_userId);
        if (tokenData != null && !string.IsNullOrEmpty(tokenData.AccessToken))
        {
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenData.AccessToken);
        }
    }

    public async Task<List<HomeworkItem>> GetHomeworkAsync(DateTime date)
    {
        return await GetHomeworkAsync(date.ToString("yyyy-MM-dd"));
    }

    public async Task<List<HomeworkItem>> GetHomeworkAsync(string date)
    {
        await EnsureAuthorizationAsync();
        var tokenData = await _tokenStore.GetTokensAsync(_userId);

        string url = "homework?date=" + date;
        if (tokenData != null && !string.IsNullOrEmpty(tokenData.StudentId))
        {
            url += "&studentId=" + tokenData.StudentId;
        }

        string json = await GetJsonAsync(url);
        var response = JsonSerializer.Deserialize<HomeworkResponse>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        return response?.Homeworks ?? new List<HomeworkItem>();
    }

    public async Task<StudentResponse> GetStudentsAsync()
    {
        await EnsureAuthorizationAsync();
        string json = await GetJsonAsync("students");
        return JsonSerializer.Deserialize<StudentResponse>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }

    private async Task<string> GetJsonAsync(string endpoint)
    {
        var response = await _client.GetAsync(BaseUrl + endpoint);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            await RefreshTokensAsync();
            // Re-ensure authorization header with the new token
            await EnsureAuthorizationAsync();
            response = await _client.GetAsync(BaseUrl + endpoint);
        }

        string content = await response.Content.ReadAsStringAsync();
        
        // Handle 502/503 for reactive tech maintenance detection
        if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable || 
            response.StatusCode == System.Net.HttpStatusCode.BadGateway)
        {
            throw new DiaryMaintenanceException($"Дневник недоступен ({(int)response.StatusCode}).");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} ({response.StatusCode}): {content}");
        }

        return content;
    }

    public async Task RefreshTokensAsync()
    {
        var tokenData = await _tokenStore.GetTokensAsync(_userId);
        if (tokenData == null || string.IsNullOrEmpty(tokenData.RefreshToken))
        {
            throw new InvalidOperationException("No refresh token available to refresh the session.");
        }

        var payload = new Dictionary<string, string>
        {
            { "accessToken", tokenData.AccessToken ?? "" },
            { "refreshToken", tokenData.RefreshToken }
        };

        var payloadJson = JsonSerializer.Serialize(payload);
        var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

        var response = await _client.PostAsync(TokenUrl, content);

        if (response.IsSuccessStatusCode)
        {
            var json = await response.Content.ReadAsStringAsync();
            string newAccessToken = null;
            string newRefreshToken = null;

            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("Auth__token", out var a)) newAccessToken = a.GetString();
                else if (root.TryGetProperty("accessToken", out var a2)) newAccessToken = a2.GetString();
                else if (root.TryGetProperty("access_token", out var a3)) newAccessToken = a3.GetString();

                if (root.TryGetProperty("Auth__refresh", out var r)) newRefreshToken = r.GetString();
                else if (root.TryGetProperty("refreshToken", out var r2)) newRefreshToken = r2.GetString();
                else if (root.TryGetProperty("refresh_token", out var r3)) newRefreshToken = r3.GetString();
            }

            if (string.IsNullOrEmpty(newAccessToken))
                throw new HttpRequestException("Refresh response did not contain access token.");

            tokenData.AccessToken = newAccessToken;
            if (!string.IsNullOrEmpty(newRefreshToken))
            {
                tokenData.RefreshToken = newRefreshToken;
            }

            await _tokenStore.SaveTokensAsync(_userId, tokenData);
        }
        else
        {
            throw new HttpRequestException($"Failed to refresh token: {response.StatusCode}");
        }
    }
}

public class DiaryMaintenanceException : Exception
{
    public DiaryMaintenanceException(string message) : base(message) { }
}
