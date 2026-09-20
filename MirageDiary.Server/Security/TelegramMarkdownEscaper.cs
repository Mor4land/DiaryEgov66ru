using System.Text;

namespace MirageDiary.Server.Security;

/// <summary>
/// Utility for safely escaping special characters in Telegram Markdown v1.
/// In Markdown v1, '_', '*', '[', ']', and '`' must be preceded by a backslash
/// when they represent literal text, otherwise Telegram rejects the message with
/// HTTP 400 Bad Request: can't parse entities.
/// </summary>
public static class TelegramMarkdownEscaper
{
    public static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var sb = new StringBuilder(text.Length + 16);
        foreach (char c in text)
        {
            switch (c)
            {
                case '_':
                case '*':
                case '[':
                case ']':
                case '`':
                case '\\':
                    sb.Append('\\').Append(c);
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
