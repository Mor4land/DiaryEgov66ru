using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace MirageDiary.Server.Security;

/// <summary>
/// Защищённое хранилище конфиденциальных строк и ключей.
/// Исключает хранение открытых строковых литералов в IL-байткоде и метаданных сборки.
/// Применяет динамическое размаскирование, нулевую очистку памяти (ZeroMemory)
/// и строгие файловые дескрипторы прав доступа (POSIX 0600).
/// </summary>
public static class SecureSecretVault
{
    // Энтропийная маска (соль) для рантайм-деобфускации
    private static readonly byte[] EntropyMask = new byte[] { 0x5A, 0xC3, 0x91, 0x17, 0xFE, 0x42, 0x8D, 0x29 };

    // Замаскированные байты мастер-ключа шифрования базы SQLite и токенов
    // Исключает нахождение plain-text строки "MirageDiary_Master_Security_Key_2026_Gosuslugi" при дизассемблировании
    private static readonly byte[] ObfuscatedMasterKey = new byte[]
    {
        23, 170, 227, 118, 153, 39, 201, 64,
        59, 177, 232, 72, 179, 35, 254, 93,
        63, 177, 206, 68, 155, 33, 248, 91,
        51, 183, 232, 72, 181, 39, 244, 118,
        104, 243, 163, 33, 161, 5, 226, 90,
        47, 176, 253, 98, 153, 43
    };

    // Замаскированная строка подключения к базе SQLite ("Data Source=miragediary.db")
    private static readonly byte[] ObfuscatedDbConnection = new byte[]
    {
        30, 162, 229, 118, 222, 17, 226, 92,
        40, 160, 244, 42, 147, 43, 255, 72,
        61, 166, 245, 126, 159, 48, 244, 7,
        62, 161
    };

    /// <summary>
    /// Безопасно восстанавливает строку подключения к SQLite.
    /// Приоритет: Переменная окружения > Конфиг > Защищённый встроенный дескриптор.
    /// </summary>
    public static string ResolveSqliteConnectionString(IConfiguration? config = null)
    {
        string? connStr = Environment.GetEnvironmentVariable("MIRAGE_SQLITE_CONNECTION")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? config?.GetConnectionString("DefaultConnection");

        if (!string.IsNullOrWhiteSpace(connStr) && !connStr.Contains("YOUR_"))
        {
            return connStr;
        }

        byte[] buffer = new byte[ObfuscatedDbConnection.Length];
        try
        {
            for (int i = 0; i < ObfuscatedDbConnection.Length; i++)
            {
                buffer[i] = (byte)(ObfuscatedDbConnection[i] ^ EntropyMask[i % EntropyMask.Length]);
            }
            return Encoding.UTF8.GetString(buffer);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    /// <summary>
    /// Получает криптографически стойкий 256-битный ключ для шифрования токенов и данных SQLite.
    /// Выполняет деривацию через SHA-256 и очищает промежуточные структуры из стека.
    /// </summary>
    public static byte[] ResolveMasterEncryptionKey(IConfiguration? config = null)
    {
        // 1. Наивысший приоритет: переменные окружения ОС (безопасно передаваемые через systemd / Docker secrets)
        string? envKey = Environment.GetEnvironmentVariable("MIRAGE_TOKEN_ENCRYPTION_KEY")
            ?? Environment.GetEnvironmentVariable("MIRAGE_SQLITE_KEY")
            ?? Environment.GetEnvironmentVariable("MIRAGE_MASTER_KEY")
            ?? Environment.GetEnvironmentVariable("SECURITY__TOKENENCRYPTIONKEY")
            ?? config?["Security:TokenEncryptionKey"];

        if (!string.IsNullOrWhiteSpace(envKey) && !envKey.StartsWith("YOUR_"))
        {
            byte[] envBytes = Encoding.UTF8.GetBytes(envKey);
            try
            {
                return SHA256.HashData(envBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(envBytes);
            }
        }

        // 2. Проверка изолированного файла ключа на диске (POSIX 0600)
        string baseDir = AppContext.BaseDirectory;
        string currentDir = Directory.GetCurrentDirectory();
        string[] candidateKeyPaths = new[]
        {
            Path.Combine(baseDir, "token_master.key"),
            Path.Combine(currentDir, "token_master.key"),
            Path.Combine(currentDir, "Data", "token_master.key"),
            Path.Combine(baseDir, "data", "token_master.key")
        };

        foreach (var keyPath in candidateKeyPaths)
        {
            var loadedKey = TryLoadSecureKeyFile(keyPath);
            if (loadedKey != null && loadedKey.Length == 32)
            {
                return loadedKey;
            }
        }

        // 3. Деобфускация встроенного эталонного мастер-ключа приложения
        byte[] rawBytes = new byte[ObfuscatedMasterKey.Length];
        try
        {
            for (int i = 0; i < ObfuscatedMasterKey.Length; i++)
            {
                rawBytes[i] = (byte)(ObfuscatedMasterKey[i] ^ EntropyMask[i % EntropyMask.Length]);
            }

            byte[] keyHash = SHA256.HashData(rawBytes);

            // Сохраняем эталонный ключ в файлы token_master.key с правами 0600
            TrySaveSecureKeyFile(Path.Combine(baseDir, "token_master.key"), keyHash);
            TrySaveSecureKeyFile(Path.Combine(currentDir, "token_master.key"), keyHash);
            TrySaveSecureKeyFile(Path.Combine(currentDir, "Data", "token_master.key"), keyHash);

            return keyHash;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rawBytes);
        }
    }

    private static byte[]? TryLoadSecureKeyFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            EnforceFilePermissions(path);
            string content = File.ReadAllText(path).Trim();
            if (string.IsNullOrEmpty(content)) return null;

            return Convert.FromBase64String(content);
        }
        catch
        {
            return null;
        }
    }

    private static void TrySaveSecureKeyFile(string path, byte[] key)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, Convert.ToBase64String(key));
            EnforceFilePermissions(path);
        }
        catch { }
    }

    /// <summary>
    /// Ограничивает права доступа к файлу (POSIX 0600 - чтение и запись только владельцу процесса).
    /// Защищает SQLite файл и ключевые файлы от чтения неавторизованными пользователями хоста.
    /// </summary>
    public static void EnforceFilePermissions(string filePath)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
            catch { }
        }
    }
}
