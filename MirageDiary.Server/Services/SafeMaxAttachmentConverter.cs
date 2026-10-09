using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using MAX.Bot.Interfaces.Models.Request.Message.Attachment;

namespace MirageDiary.Server.Services;

/// <summary>
/// Безопасный полиморфный конвертер вложений MAX.
/// Предотвращает System.NotSupportedException при отсутствии или несовпадении дискриминатора type в $.body.attachments[0].
/// </summary>
public class SafeMaxAttachmentConverter : JsonConverter<Attachment>
{
    public override bool CanConvert(Type typeToConvert)
    {
        return typeof(Attachment).IsAssignableFrom(typeToConvert);
    }

    public override Attachment? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        try
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }

            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            string? type = null;
            if (root.TryGetProperty("type", out var typeProp))
            {
                type = typeProp.GetString();
            }

            // Резервная эвристика: если поле "type" отсутствует, определяем по содержимому payload
            if (string.IsNullOrEmpty(type) && root.TryGetProperty("payload", out var payloadProp))
            {
                if (payloadProp.TryGetProperty("buttons", out _))
                {
                    type = "inline_keyboard";
                }
                else if (payloadProp.TryGetProperty("url", out _) || payloadProp.TryGetProperty("token", out _))
                {
                    type = "image";
                }
            }

            string rawText = root.GetRawText();
            return type switch
            {
                "inline_keyboard" => JsonSerializer.Deserialize<InlineKeyboardAttachment>(rawText, options),
                "image" => JsonSerializer.Deserialize<ImageAttachment>(rawText, options),
                "file" => JsonSerializer.Deserialize<FileAttachment>(rawText, options),
                "video" => JsonSerializer.Deserialize<VideoAttachment>(rawText, options),
                "audio" => JsonSerializer.Deserialize<AudioAttachment>(rawText, options),
                "sticker" => JsonSerializer.Deserialize<StickerAttachment>(rawText, options),
                "contact" => JsonSerializer.Deserialize<ContactAttachment>(rawText, options),
                "location" => JsonSerializer.Deserialize<LocationAttachment>(rawText, options),
                _ => null
            };
        }
        catch
        {
            // Никогда не выбрасываем исключение наружу, чтобы не ронять десериализацию всего Update
            return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, Attachment value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, (object)value, value.GetType(), options);
    }
}
