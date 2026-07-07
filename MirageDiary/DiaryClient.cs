using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Text.Encodings.Web;

namespace MirageDiary
{
    public class DiaryClient
    {
        private readonly HttpClient _client = new HttpClient();
        private string _token;
        private string _studentId;
        private const string BaseUrl = "https://dnevnik.egov66.ru/api/";
        private const string TokenUrl = "https://dnevnik.egov66.ru/api/auth/Token/Refresh";

        public DiaryClient(string token, string studentId)
        {
            _token = token;
            _studentId = studentId;
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }


        public async Task<List<HomeworkItem>> GetHomeworkAsync(string date)
        {
            string url = "homework?date=" + date;
            if (!string.IsNullOrEmpty(_studentId))
            {
                url += "&studentId=" + _studentId;
            }
            string json = await GetJsonAsync(url);
            var response = JsonSerializer.Deserialize<HomeworkResponse>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return response.Homeworks;
        }
        // Форматирование даты в строку "yyyy-MM-dd" для вызова GetHomeworkAsync
        public async Task<List<HomeworkItem>> GetDateAsync(DateTime date)
        {
            return await GetHomeworkAsync(date.ToString("yyyy-MM-dd"));
        }
    
        public async Task<string> GetJsonAsync(string endpoint)
        {
            var response = await _client.GetAsync(BaseUrl + endpoint);


            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                await RefreshTokensAsync();
                return await GetJsonAsync(endpoint);
            }

            string content = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"HTTP {(int)response.StatusCode} ({response.StatusCode}): {content}");
            }

            return content;
        }
        // Метод для обновления токенов, который читает токены из localStorage.json, отправляет их на сервер для обновления и сохраняет новые токены обратно в файл.
        public async Task RefreshTokensAsync()
        {
            string folderPath = Environment.ExpandEnvironmentVariables("%appdata%\\DiarySession\\");
            var localStoragePath = Path.Combine(folderPath, "localStorage.json");
            if (!File.Exists(localStoragePath))
                throw new FileNotFoundException("localStorage.json not found", localStoragePath);

            string localJson = File.ReadAllText(localStoragePath);

            string accessToken = null;
            string refreshToken = null;
            string childId = null;

            using (var doc = JsonDocument.Parse(localJson))
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("Auth__token", out var a)) accessToken = a.GetString();
                else if (root.TryGetProperty("accessToken", out var a2)) accessToken = a2.GetString();
                else if (root.TryGetProperty("access_token", out var a3)) accessToken = a3.GetString();

                if (root.TryGetProperty("Auth__refresh", out var r)) refreshToken = r.GetString();
                else if (root.TryGetProperty("refreshToken", out var r2)) refreshToken = r2.GetString();
                else if (root.TryGetProperty("refresh_token", out var r3)) refreshToken = r3.GetString();

                if (root.TryGetProperty("childId", out var c)) childId = c.GetString();
                else if (root.TryGetProperty("childid", out var c2)) childId = c2.GetString();
            }

            var payload = new Dictionary<string, string>();
            if (accessToken != null) payload["accessToken"] = accessToken;
            if (refreshToken != null) payload["refreshToken"] = refreshToken;

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

                _token = newAccessToken;
                _studentId = childId;
                _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);

                // Запись обновленных токенов обратно в localStorage.json
                var updatedTokenData = new Dictionary<string, string>
                {
                    { "Auth__token", newAccessToken },
                    { "Auth__refresh", newRefreshToken ?? refreshToken }, // Сохраняем старый рефреш-токен  если новый не вернулся
                    { "childId", childId }
                };

                string updatedJson = JsonSerializer.Serialize(updatedTokenData, new JsonSerializerOptions 
                { 
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All)
                });
                File.WriteAllText(localStoragePath, updatedJson);
            }
            else
            {
                throw new HttpRequestException($"Failed to refresh token: {response.StatusCode}");
            }
        }

    }

}
