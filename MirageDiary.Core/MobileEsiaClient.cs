using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace MirageDiary.Core;

public class MobileEsiaSignRequest
{
    [JsonPropertyName("client_id")]
    public string ClientId { get; set; } = "PGU";

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "";
}

public class MobileEsiaSignResponse
{
    [JsonPropertyName("secret")]
    public string Secret { get; set; } = "";

    [JsonPropertyName("time")]
    public string Time { get; set; } = "";

    [JsonPropertyName("stateMarker")]
    public string StateMarker { get; set; } = "";
}

public class MobileEsiaTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = "";

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = "";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("oid")]
    public string Oid { get; set; } = "";
}

public class MobileEsiaSaveTokenRequest
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("userId")]
    public string UserId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "Pixel 8";

    [JsonPropertyName("expires")]
    public int Expires { get; set; } = 28800;

    [JsonPropertyName("accessToken")]
    public string AccessToken { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "Android";

    [JsonPropertyName("refreshToken")]
    public string RefreshToken { get; set; } = "";

    [JsonPropertyName("entToken")]
    public string EntToken { get; set; } = "";
}

public class MobileSaveTokenResponse
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = "";

    [JsonPropertyName("accessToken")]
    public string AccessToken { get; set; } = "";
}

public class MobileEsiaCreateSessionRequest
{
    [JsonPropertyName("userId")]
    public string UserId { get; set; } = "";

    [JsonPropertyName("accessToken")]
    public string AccessToken { get; set; } = "";

    [JsonPropertyName("entToken")]
    public string EntToken { get; set; } = "";

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "Pixel 8";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "Android";
}

public class MobileEntDevice
{
    [JsonPropertyName("device_id")]
    public string DeviceId { get; set; } = "";
}

public class MobileEntTokenMeta
{
    [JsonPropertyName("persistent")]
    public MobileEntDevice Persistent { get; set; } = new();

    [JsonPropertyName("rnd_id")]
    public string RndId { get; set; } = "";

    [JsonPropertyName("app_name")]
    public string AppName { get; set; } = "MP_SCHOOL";

    [JsonPropertyName("phone_model")]
    public string PhoneModel { get; set; } = "Pixel 8";

    [JsonPropertyName("os_type")]
    public string OsType { get; set; } = "android";

    [JsonPropertyName("os_version")]
    public string OsVersion { get; set; } = "14";
}

public class MobileEsiaClient
{
    private readonly HttpClient _httpClient;
    private const string AppUserAgent = "ru.gosuslugi.school/4.1.1.432 (Android 14; okhttp/4.12.0)";
    private const string ClientId = "PGU";
    private const string DefaultScope = "http://esia.gosuslugi.ru/usr_inf?mode=w http://esia.gosuslugi.ru/usr_sec?mode=w";
    public const string RedirectUri = "gosuslugi-school://delegatedauth";
    public const string TeRedirectUri = "https://www.gosuslugi.ru/tut-nichego-net";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public MobileEsiaClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// Step 1: Request signed secret from official mobile gateway
    /// </summary>
    public async Task<MobileEsiaSignResponse?> GetSignAsync()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://www.gosuslugi.ru/auth-provider/mobile/sign");
        req.Headers.TryAddWithoutValidation("User-Agent", AppUserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");

        var payload = new MobileEsiaSignRequest
        {
            ClientId = ClientId,
            Scope = DefaultScope
        };
        req.Content = JsonContent.Create(payload, options: JsonOptions);

        var resp = await _httpClient.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            string err = await resp.Content.ReadAsStringAsync();
            Console.WriteLine($"[MobileEsiaClient] ❌ GetSign failed: {resp.StatusCode} - {err}");
            return null;
        }

        return await resp.Content.ReadFromJsonAsync<MobileEsiaSignResponse>(JsonOptions);
    }

    /// <summary>
    /// Generates PKCE code_verifier and code_challenge
    /// </summary>
    public static (string Verifier, string Challenge) GeneratePkce()
    {
        byte[] randomBytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomBytes);
        string verifier = Base64UrlEncode(randomBytes);

        using var sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(Encoding.ASCII.GetBytes(verifier));
        string challenge = Base64UrlEncode(hash);

        return (verifier, challenge);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>
    /// Step 2: Build the ESIA OAuth authorization URL matching Android APK (com.esiasdk.android.domain.d)
    /// </summary>
    public string BuildAuthUrl(string secret, string time, string stateMarker, string codeChallenge, string entToken)
    {
        string encodedSecret = secret;
        string encodedTime = time.Replace("+", "%2B").Replace(" ", "+");
        string encodedRedirect = Uri.EscapeDataString(RedirectUri);
        // In Android APK d.java: scope is joined with '+' and NOT URL-escaped
        string scopeParam = "http://esia.gosuslugi.ru/usr_inf?mode=w+http://esia.gosuslugi.ru/usr_sec?mode=w";
        string encodedInstanceId = Uri.EscapeDataString(entToken);

        return $"https://esia.gosuslugi.ru/aas/oauth2/fc?" +
               $"client_id={ClientId}&" +
               $"client_secret={encodedSecret}&" +
               $"redirect_uri={encodedRedirect}&" +
               $"scope={scopeParam}&" +
               $"response_type=fat_code&" +
               $"state={stateMarker}&" +
               $"timestamp={encodedTime}&" +
               $"code_challenge={codeChallenge}&" +
               $"code_challenge_method=S256&" +
               $"instanceId={encodedInstanceId}";
    }

    /// <summary>
    /// Step 3: Exchange fat_code for permanent tokens (/aas/oauth2/te)
    /// </summary>
    public async Task<MobileEsiaTokenResponse?> ExchangeFatCodeAsync(
        string fatCode,
        string clientSecret,
        string time,
        string stateMarker,
        string codeVerifier,
        string entToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://esia.gosuslugi.ru/aas/oauth2/te");
        req.Headers.TryAddWithoutValidation("User-Agent", AppUserAgent);

        var formParams = new Dictionary<string, string>
        {
            { "client_id", ClientId },
            { "client_secret", clientSecret },
            { "scope", DefaultScope },
            { "state", stateMarker },
            { "timestamp", time },
            { "instanceId", entToken },
            { "redirect_uri", TeRedirectUri }, // "https://www.gosuslugi.ru/tut-nichego-net" from APK!
            { "fat_code", fatCode },
            { "grant_type", "fat_code" },
            { "token_type", "Bearer" },
            { "terminal_name", "Pixel 8" },
            { "terminal_type", "Android" },
            { "code_verifier", codeVerifier }
        };

        req.Content = new FormUrlEncodedContent(formParams);

        var resp = await _httpClient.SendAsync(req);
        string json = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
        {
            Console.WriteLine($"[MobileEsiaClient] ❌ ExchangeFatCode failed: {resp.StatusCode} - {json}");
            return null;
        }

        return JsonSerializer.Deserialize<MobileEsiaTokenResponse>(json, JsonOptions);
    }

    /// <summary>
    /// Step 4: Register session in School API (/auth-provider/mobile/saveToken)
    /// </summary>
    public async Task<MobileSaveTokenResponse?> SaveTokenAsync(
        string androidId,
        string userId,
        string esiaAccessToken,
        string esiaRefreshToken,
        int expiresIn,
        string entToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://www.gosuslugi.ru/auth-provider/mobile/saveToken");
        req.Headers.TryAddWithoutValidation("User-Agent", AppUserAgent);

        var payload = new MobileEsiaSaveTokenRequest
        {
            Id = androidId,
            UserId = userId,
            Name = "Pixel 8",
            Expires = expiresIn > 0 ? expiresIn : 28800,
            AccessToken = esiaAccessToken,
            Type = "Google",
            RefreshToken = esiaRefreshToken,
            EntToken = entToken
        };

        req.Content = JsonContent.Create(payload, options: JsonOptions);

        var resp = await _httpClient.SendAsync(req);
        string json = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
        {
            Console.WriteLine($"[MobileEsiaClient] ❌ SaveToken failed: {resp.StatusCode} - {json}");
            return null;
        }

        return JsonSerializer.Deserialize<MobileSaveTokenResponse>(json, JsonOptions);
    }

    /// <summary>
    /// Step 5: Silent session refresh (/auth-provider/mobile/v2/createSession)
    /// </summary>
    public async Task<MobileSaveTokenResponse?> CreateSessionAsync(
        string androidId,
        string userId,
        string accessToken,
        string entToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://www.gosuslugi.ru/auth-provider/mobile/v2/createSession");
        req.Headers.TryAddWithoutValidation("User-Agent", AppUserAgent);

        var payload = new MobileEsiaCreateSessionRequest
        {
            UserId = userId,
            AccessToken = accessToken,
            EntToken = entToken,
            Id = androidId,
            Name = "Pixel 8",
            Type = "Android"
        };

        req.Content = JsonContent.Create(payload, options: JsonOptions);

        var resp = await _httpClient.SendAsync(req);
        string json = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
        {
            Console.WriteLine($"[MobileEsiaClient] ❌ CreateSession failed: {resp.StatusCode} - {json}");
            return null;
        }

        return JsonSerializer.Deserialize<MobileSaveTokenResponse>(json, JsonOptions);
    }

    public static string BuildEntToken(string androidId)
    {
        var meta = new MobileEntTokenMeta
        {
            Persistent = new MobileEntDevice { DeviceId = androidId },
            RndId = Guid.NewGuid().ToString(),
            AppName = "MP_SCHOOL",
            PhoneModel = "Pixel 8",
            OsType = "android",
            OsVersion = "14"
        };

        string json = JsonSerializer.Serialize(meta, JsonOptions);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }
}
