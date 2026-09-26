using System;
using Microsoft.Extensions.Configuration;
using MirageDiary.Server.Services;
using Xunit;

namespace MirageDiary.Tests;

public class TokenEncryptionServiceTests
{
    private readonly TokenEncryptionService _service;

    public TokenEncryptionServiceTests()
    {
        var config = new ConfigurationBuilder().Build();
        _service = new TokenEncryptionService(config);
    }

    [Fact]
    public void Encrypt_PlainText_ReturnsEncV2Prefix()
    {
        string secret = "test_esia_access_token_12345";
        string encrypted = _service.Encrypt(secret);

        Assert.NotNull(encrypted);
        Assert.StartsWith("enc:v2:", encrypted);
        var parts = encrypted["enc:v2:".Length..].Split(':');
        Assert.Equal(6, parts.Length);
    }

    [Fact]
    public void GosuslugiStudentInfo_DeserializesKidIdAndMarksProperly()
    {
        string jsonParent = @"[
            {
                ""trusted"": true,
                ""disabled"": false,
                ""student_last_name"": ""Иванов"",
                ""student_first_name"": ""Иван"",
                ""student_id"": ""child-uuid-123"",
                ""kid_id"": 195972879,
                ""permission_mark"": true
            }
        ]";

        var students = System.Text.Json.JsonSerializer.Deserialize<List<MirageDiary.Core.Models.GosuslugiStudentInfo>>(jsonParent, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(students);
        Assert.Single(students);
        Assert.Equal(195972879, students[0].KidId);
        Assert.True(students[0].PermissionMark);
        Assert.Equal("child-uuid-123", students[0].StudentId);
    }

    [Fact]
    public void Decrypt_EncryptedText_ReturnsOriginalPlainText()
    {
        string secret = "secret_gosuslugi_session_payload";
        string encrypted = _service.Encrypt(secret);
        string decrypted = _service.Decrypt(encrypted);

        Assert.Equal(secret, decrypted);
    }

    [Fact]
    public void Encrypt_SameTextTwice_ProducesDifferentCiphertextsDueToRandomDEKAndNonce()
    {
        string secret = "identical_token_value";
        string enc1 = _service.Encrypt(secret);
        string enc2 = _service.Encrypt(secret);

        Assert.NotEqual(enc1, enc2);
        Assert.Equal(secret, _service.Decrypt(enc1));
        Assert.Equal(secret, _service.Decrypt(enc2));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Encrypt_EmptyOrNull_ReturnsInput(string? input)
    {
        string? result = _service.Encrypt(input!);
        Assert.Equal(input, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Decrypt_EmptyOrNull_ReturnsInput(string? input)
    {
        string? result = _service.Decrypt(input!);
        Assert.Equal(input, result);
    }

    [Fact]
    public void Encrypt_AlreadyEncrypted_DoesNotDoubleEncrypt()
    {
        string secret = "token_sample";
        string encrypted = _service.Encrypt(secret);
        string doubleEncrypted = _service.Encrypt(encrypted);

        Assert.Equal(encrypted, doubleEncrypted);
    }

    [Fact]
    public void Decrypt_TamperedCiphertext_ReturnsEmptyString()
    {
        string secret = "sensitive_data";
        string encrypted = _service.Encrypt(secret);

        // Corrupt the last character
        char lastChar = encrypted[^1];
        char corruptedChar = lastChar == 'A' ? 'B' : 'A';
        string tampered = encrypted[..^1] + corruptedChar;

        string decrypted = _service.Decrypt(tampered);
        Assert.Equal(string.Empty, decrypted);
    }

    [Fact]
    public void HashToken_ReturnsDeterministicHexSha256()
    {
        string token = "my_fixed_token";
        string hash1 = _service.HashToken(token);
        string hash2 = _service.HashToken(token);

        Assert.Equal(hash1, hash2);
        Assert.Equal(64, hash1.Length);
    }
}
