using System;
using System.IO;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using System.Text.Json;
using System.Threading.Tasks;
using System.Text.Encodings.Web;

namespace MirageDiary
{
    public partial class Main : Form
    {
        public static string folderPath = Environment.ExpandEnvironmentVariables("%appdata%\\DiarySession\\");
        public string filePath = Path.Combine(folderPath, "localStorage.json");
        private Timer tokenPollTimer;

        public Main()
        {
            InitializeComponent();
            if (!Directory.Exists(folderPath))
            {
                Directory.CreateDirectory(folderPath);
            }
            InitializeAsync();

            tokenPollTimer = new Timer();
            tokenPollTimer.Interval = 1000;
            tokenPollTimer.Tick += TokenPollTimer_Tick;
            tokenPollTimer.Start();
        }
        private void Form1_Load(object sender, EventArgs e)
        {

        }

        private async void InitializeAsync()
        {
            var env = await CoreWebView2Environment.CreateAsync(null, folderPath);
            await webView21.EnsureCoreWebView2Async(env);
            webView21.Source = new Uri("https://dnevnik.egov66.ru/");
            webView21.NavigationCompleted += async (s, e) =>
            {
                await CheckAndSaveTokensAsync();
            };
        }

        private async void TokenPollTimer_Tick(object sender, EventArgs e)
        {
            await CheckAndSaveTokensAsync();
        }

        private async Task CheckAndSaveTokensAsync()
        {
            if (webView21.CoreWebView2 == null) return;

            try
            {
                string authToken = await webView21.CoreWebView2.ExecuteScriptAsync("localStorage.getItem('Auth__token')");
                string authRefresh = await webView21.CoreWebView2.ExecuteScriptAsync("localStorage.getItem('Auth__refresh')");
                string childid = await webView21.CoreWebView2.ExecuteScriptAsync("localStorage.getItem('childId')");

                string cleanToken = UnescapeJsonString(authToken);
                string cleanRefresh = UnescapeJsonString(authRefresh);
                string cleanChildId = UnescapeJsonString(childid);

                if (!string.IsNullOrEmpty(cleanToken) && cleanToken != "null")
                {
                    bool needsWrite = true;
                    if (File.Exists(filePath))
                    {
                        try
                        {
                            string existingJson = File.ReadAllText(filePath);
                            TokenData existingData = JsonSerializer.Deserialize<TokenData>(existingJson);
                            if (existingData != null &&
                                existingData.Auth__token == cleanToken &&
                                existingData.Auth__refresh == cleanRefresh &&
                                existingData.childId == cleanChildId)
                            {
                                needsWrite = false; 
                            }
                        }
                        catch
                        {
                        
                        }
                    }

                    if (needsWrite)
                    {
                        TokenData tokenData = new TokenData(cleanToken, cleanRefresh, cleanChildId);
                        string json = JsonSerializer.Serialize(tokenData, new JsonSerializerOptions 
                        { 
                            WriteIndented = true,
                            Encoder = JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All)
                        });
                        File.WriteAllText(filePath, json);
                    }
                }
            }
            catch
            {
            
            }
        }



        private string UnescapeJsonString(string json)
        {
            if (string.IsNullOrEmpty(json) || json == "null") return null;
            try
            {
                return JsonSerializer.Deserialize<string>(json);
            }
            catch
            {
                if (json.StartsWith("\"") && json.EndsWith("\"") && json.Length >= 2)
                {
                    return json.Substring(1, json.Length - 2);
                }
                return json;
            }
        }

        private async void btnSync_Click(object sender, EventArgs e)
        {
            if (!long.TryParse(txtChatId.Text.Trim(), out long chatId))
            {
                MessageBox.Show("Пожалуйста, введите корректный Telegram Chat ID (число).", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string token = null;
            string refresh = null;
            string childId = null;

            // Попробуем прочитать из файла
            if (File.Exists(filePath))
            {
                try
                {
                    string json = File.ReadAllText(filePath);
                    using (var doc = JsonDocument.Parse(json))
                    {
                        var root = doc.RootElement;
                        if (root.TryGetProperty("Auth__token", out var a)) token = a.GetString();
                        if (root.TryGetProperty("Auth__refresh", out var r)) refresh = r.GetString();
                        if (root.TryGetProperty("childId", out var c)) childId = c.GetString();
                    }
                }
                catch { }
            }

            // Если не удалось из файла, пробуем напрямую из WebView
            if (string.IsNullOrEmpty(token) && webView21.CoreWebView2 != null)
            {
                try
                {
                    string authToken = await webView21.CoreWebView2.ExecuteScriptAsync("localStorage.getItem('Auth__token')");
                    string authRefresh = await webView21.CoreWebView2.ExecuteScriptAsync("localStorage.getItem('Auth__refresh')");
                    string cid = await webView21.CoreWebView2.ExecuteScriptAsync("localStorage.getItem('childId')");

                    token = UnescapeJsonString(authToken);
                    refresh = UnescapeJsonString(authRefresh);
                    childId = UnescapeJsonString(cid);
                }
                catch { }
            }

            if (string.IsNullOrEmpty(token))
            {
                MessageBox.Show("Не удалось получить токены авторизации. Пожалуйста, войдите в дневник.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            btnSync.Enabled = false;
            btnSync.Text = "Синхронизация...";

            try
            {
                using (var client = new HttpClient())
                {
                    var payload = new
                    {
                        ChatId = chatId,
                        AuthToken = token,
                        AuthRefresh = refresh,
                        StudentId = childId
                    };
                    string payloadJson = JsonSerializer.Serialize(payload);
                    var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

                    // Отправляем на локальный HTTP-сервер бота
                    var response = await client.PostAsync("http://localhost:8080/sync", content);

                    if (response.IsSuccessStatusCode)
                    {
                        MessageBox.Show("Связывание с Telegram успешно выполнено! Проверьте чат с ботом.", "Успех", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else
                    {
                        MessageBox.Show($"Сервер вернул ошибку: {(int)response.StatusCode} ({response.StatusCode})", "Ошибка привязки", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось связаться с сервером бота: {ex.Message}\n\nУбедитесь, что телеграм-бот запущен на этом компьютере.", "Ошибка подключения", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnSync.Enabled = true;
                btnSync.Text = "Связать с Telegram";
            }
        }
    }
    class TokenData
    {
        public TokenData(string authToken, string authRefresh, string childId)
        {
            Auth__token = authToken;
            Auth__refresh = authRefresh;
            this.childId = childId;
        }

        public string Auth__token { get; set; }
        public string Auth__refresh { get; set; }
        public string childId { get; set; }
    }
}
