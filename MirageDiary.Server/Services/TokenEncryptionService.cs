using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace MirageDiary.Server.Services;

public interface ITokenEncryptionService
{
    string Encrypt(string plainText);
    string Decrypt(string cipherText);
    string HashToken(string token);
}

public class TokenEncryptionService : ITokenEncryptionService
{
    private readonly byte[] _key;

    public TokenEncryptionService(IConfiguration configuration)
    {
        _key = MirageDiary.Server.Security.SecureSecretVault.ResolveMasterEncryptionKey(configuration);
    }

    public string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return plainText;

        // Do not re-encrypt if already encrypted
        if (plainText.StartsWith("enc:v1:")) return plainText;

        byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
        byte[] nonce = RandomNumberGenerator.GetBytes(12); // AES-GCM 96-bit nonce
        byte[] tag = new byte[16]; // 128-bit authentication tag
        byte[] cipherBytes = new byte[plainBytes.Length];

        using (var aesGcm = new AesGcm(_key, 16))
        {
            aesGcm.Encrypt(nonce, plainBytes, cipherBytes, tag);
        }

        // Format: enc:v1:<nonce_base64>:<tag_base64>:<ciphertext_base64>
        string payload = $"{Convert.ToBase64String(nonce)}:{Convert.ToBase64String(tag)}:{Convert.ToBase64String(cipherBytes)}";
        return $"enc:v1:{payload}";
    }

    public string Decrypt(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText)) return cipherText;

        // Return plainText as fallback if legacy unencrypted
        if (!cipherText.StartsWith("enc:v1:")) return cipherText;

        try
        {
            string payload = cipherText.Substring("enc:v1:".Length);
            string[] parts = payload.Split(':');
            if (parts.Length != 3) return "";

            byte[] nonce = Convert.FromBase64String(parts[0]);
            byte[] tag = Convert.FromBase64String(parts[1]);
            byte[] cipherBytes = Convert.FromBase64String(parts[2]);
            byte[] plainBytes = new byte[cipherBytes.Length];

            using (var aesGcm = new AesGcm(_key, 16))
            {
                aesGcm.Decrypt(nonce, cipherBytes, tag, plainBytes);
            }

            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            // Decryption failed (e.g. key mismatch or corrupted data). Return empty string instead of raw ciphertext enc:v1:...
            return "";
        }
    }

    public string HashToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return "";
        using var sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
