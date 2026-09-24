using System.Threading.Tasks;
using MirageDiary.Server.Models;

namespace MirageDiary.Server.Services;

/// <summary>
/// Единый интерфейс адаптера платформы мессенджера (Telegram, MAX).
/// Предоставляет абстракцию над транспортом, отправкой сообщений и рендерингом BotScreen.
/// </summary>
public interface IBotPlatformAdapter
{
    /// <summary>
    /// Идентификатор платформы ("telegram", "max").
    /// </summary>
    string Platform { get; }

    /// <summary>
    /// Отрисовывает экран BotScreen (текст, баннер, вложения, кнопки) с редактированием или отправкой.
    /// </summary>
    Task RenderScreenAsync(long chatId, string? messageId, BotScreen screen);

    /// <summary>
    /// Отправляет подтверждение/всплывающее уведомление на callback-запрос.
    /// </summary>
    Task AnswerCallbackAsync(string callbackId, string? alertText = null, bool showAlert = false);

    /// <summary>
    /// Отправляет простое текстовое сообщение.
    /// </summary>
    Task SendTextMessageAsync(long chatId, string text);

    /// <summary>
    /// Проверяет обязательную подписку на канал (для платформ, поддерживающих проверки).
    /// </summary>
    Task<bool> EnsureChannelSubscribedAsync(long chatId, string userId);
}
