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

        // Do not re-encrypt if already in latest format
        if (plainText.StartsWith("enc:v2:")) return plainText;

        // If legacy enc:v1 format was passed, decrypt it first so we can re-encrypt to enc:v2
        if (plainText.StartsWith("enc:v1:"))
        {
            var decrypted = Decrypt(plainText);
            if (!string.IsNullOrEmpty(decrypted))
            {
                plainText = decrypted;
            }
        }

        byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);

        // 1. Generate unique random 256-bit Data Encryption Key (DEK) for this token
        byte[] dek = RandomNumberGenerator.GetBytes(32);

        // 2. Encrypt plaintext data using DEK with AES-256-GCM
        byte[] dataNonce = RandomNumberGenerator.GetBytes(12); // 96-bit nonce
        byte[] dataTag = new byte[16];                         // 128-bit auth tag
        byte[] cipherBytes = new byte[plainBytes.Length];

        using (var aesGcmData = new AesGcm(dek, 16))
        {
            aesGcmData.Encrypt(dataNonce, plainBytes, cipherBytes, dataTag);
        }

        // 3. Encrypt DEK using Master Key (KEK) with AES-256-GCM
        byte[] dekNonce = RandomNumberGenerator.GetBytes(12);
        byte[] dekTag = new byte[16];
        byte[] encDek = new byte[dek.Length];

        using (var aesGcmKek = new AesGcm(_key, 16))
        {
            aesGcmKek.Encrypt(dekNonce, dek, encDek, dekTag);
        }

        // Defense-in-depth: Immediately wipe plaintext DEK from memory
        CryptographicOperations.ZeroMemory(dek);

        // Format: enc:v2:<dekNonce>:<dekTag>:<encDek>:<dataNonce>:<dataTag>:<cipherBytes>
        string payload = $"{Convert.ToBase64String(dekNonce)}:{Convert.ToBase64String(dekTag)}:{Convert.ToBase64String(encDek)}:{Convert.ToBase64String(dataNonce)}:{Convert.ToBase64String(dataTag)}:{Convert.ToBase64String(cipherBytes)}";
        return $"enc:v2:{payload}";
    }

    public string Decrypt(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText)) return cipherText;

        // Support enc:v2: Envelope Encryption
        if (cipherText.StartsWith("enc:v2:"))
        {
            try
            {
                string payload = cipherText.Substring("enc:v2:".Length);
                string[] parts = payload.Split(':');
                if (parts.Length != 6) return "";

                byte[] dekNonce = Convert.FromBase64String(parts[0]);
                byte[] dekTag = Convert.FromBase64String(parts[1]);
                byte[] encDek = Convert.FromBase64String(parts[2]);
                byte[] dataNonce = Convert.FromBase64String(parts[3]);
                byte[] dataTag = Convert.FromBase64String(parts[4]);
                byte[] cipherBytes = Convert.FromBase64String(parts[5]);

                // 1. Decrypt DEK using Master Key (KEK)
                byte[] dek = new byte[encDek.Length];
                using (var aesGcmKek = new AesGcm(_key, 16))
                {
                    aesGcmKek.Decrypt(dekNonce, encDek, dekTag, dek);
                }

                // 2. Decrypt data using decrypted DEK
                byte[] plainBytes = new byte[cipherBytes.Length];
                try
                {
                    using (var aesGcmData = new AesGcm(dek, 16))
                    {
                        aesGcmData.Decrypt(dataNonce, cipherBytes, dataTag, plainBytes);
                    }
                    return Encoding.UTF8.GetString(plainBytes);
                }
                finally
                {
                    // Always wipe DEK from memory
                    CryptographicOperations.ZeroMemory(dek);
                }
            }
            catch
            {
                // Decryption failed (corrupted or mismatched key)
                return "";
            }
        }

        // Backward compatibility: Support legacy enc:v1 format
        if (cipherText.StartsWith("enc:v1:"))
        {
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
                return "";
            }
        }

        // Return plainText as fallback if legacy unencrypted
        return cipherText;
    }

    public string HashToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return "";
        using var sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
