using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MirageDiary.Server.Models;
using MAX.Bot.Interfaces;
using MAX.Bot.Interfaces.Models;
using MAX.Bot.Interfaces.Models.Request;
using MAX.Bot.Interfaces.Models.Request.Message;
using MAX.Bot.Interfaces.Models.Request.Message.Attachment;
using MAX.Bot.Interfaces.Models.Request.Message.Attachment.Payloads;

namespace MirageDiary.Server.Services;

/// <summary>
/// Тонкий платформенный адаптер MAX Bot API.
/// Отвечает за прием сетевых обновлений MAX, конвертацию клавиатур
/// и отрисовку BotScreen через IBotPlatformAdapter.
/// </summary>
public class MaxCommandService : IBotPlatformAdapter
{
    private static readonly ConcurrentDictionary<long, DateTime> _userCooldowns = new();
    private static readonly ConcurrentDictionary<long, ConcurrentQueue<string>> _chatMessageHistory = new();

    private readonly IMaxBotClient _botClient;
    private readonly DiaryBotWorkflowEngine _engine;
    private readonly IConfiguration _config;
    private readonly ILogger<MaxCommandService> _logger;

    public string Platform => "max";

    public MaxCommandService(
        IMaxBotClient botClient,
        DiaryBotWorkflowEngine engine,
        IConfiguration config,
        ILogger<MaxCommandService> logger)
    {
        _botClient = botClient;
        _engine = engine;
        _config = config;
        _logger = logger;
    }

    private static void RecordMessageId(long chatId, string? messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId)) return;
        var queue = _chatMessageHistory.GetOrAdd(chatId, _ => new ConcurrentQueue<string>());
        queue.Enqueue(messageId);
        while (queue.Count > 100 && queue.TryDequeue(out _)) { }
    }

    public async Task HandleUpdateAsync(Update update)
    {
        if (update == null) return;

        try
        {
            if (update is MessageCreatedUpdate msgCreated)
            {
                var msg = msgCreated.Message;
                long chatId = msg?.Sender?.Id ?? 0;
                if (chatId == 0) return;
                RecordMessageId(chatId, msg?.Body?.Mid);

                if (!CheckUserCooldown(chatId)) return;

                string maxUserId = $"max_{chatId}";
                var attachment = msg?.Body?.Attachments?.FirstOrDefault(a => a is ImageAttachment || a is FileAttachment);
                if (attachment != null)
                {
                    string fileId = msg?.Body?.Mid ?? Guid.NewGuid().ToString();
                    string fileType = attachment is ImageAttachment ? "photo" : "document";
                    await _engine.HandleFileUploadAsync(this, chatId, maxUserId, fileId, fileType);
                    return;
                }

                string text = msg?.Body?.Text?.Trim() ?? "";
                string? username = msg?.Sender?.Username;
                string? firstName = msg?.Sender?.FirstName;
                string? lastName = msg?.Sender?.LastName;

                await _engine.ProcessMessageOrCommandAsync(this, chatId, maxUserId, text, username, firstName, lastName);
            }
            else if (update is MessageCallbackUpdate msgCallback)
            {
                var cb = msgCallback.Callback;
                if (cb == null) return;

                long chatId = cb.User?.Id ?? msgCallback.Message?.Sender?.Id ?? 0;
                if (chatId == 0) return;
                RecordMessageId(chatId, msgCallback.Message?.Body?.Mid);

                string messageId = msgCallback.Message?.Body?.Mid ?? "";
                string maxUserId = $"max_{chatId}";
                string data = cb.Payload ?? "";

                await _engine.ProcessCallbackAsync(this, chatId, messageId, maxUserId, cb.CallbackId, data);
            }
            else if (update is BotStartedUpdate botStarted)
            {
                long userId = botStarted.User?.Id ?? botStarted.ChatId;
                if (userId != 0)
                {
                    long chatId = botStarted.ChatId != 0 ? botStarted.ChatId : userId;
                    await _engine.ProcessMessageOrCommandAsync(this, chatId, $"max_{userId}", "/start", botStarted.User?.Username, botStarted.User?.FirstName, botStarted.User?.LastName);
                }
            }
            else if (update is BotStoppedUpdate botStopped)
            {
                long userId = botStopped.User?.Id ?? botStopped.ChatId;
                if (userId != 0)
                {
                    long chatId = botStopped.ChatId != 0 ? botStopped.ChatId : userId;
                    await _engine.HandleUserStopAsync(this, chatId, $"max_{userId}");
                }
            }
            else if (update is DialogRemovedUpdate dialogRemoved)
            {
                long userId = dialogRemoved.User?.Id ?? dialogRemoved.ChatId;
                if (userId != 0)
                {
                    long chatId = dialogRemoved.ChatId != 0 ? dialogRemoved.ChatId : userId;
                    await _engine.HandleUserStopAsync(this, chatId, $"max_{userId}");
                }
            }
            else if (update is DialogClearedUpdate dialogCleared)
            {
                long userId = dialogCleared.User?.Id ?? dialogCleared.ChatId;
                if (userId != 0)
                {
                    long chatId = dialogCleared.ChatId != 0 ? dialogCleared.ChatId : userId;
                    await _engine.HandleUserStopAsync(this, chatId, $"max_{userId}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing MAX update");
        }
    }

    public async Task RenderScreenAsync(long chatId, string? messageId, BotScreen screen)
    {
        var keyboard = ConvertToMaxKeyboard(screen.Buttons);
        if (!string.IsNullOrEmpty(screen.BannerFileName))
        {
            await SendOrEditBannerScreenAsync(chatId, messageId, screen.BannerFileName, screen.Text, keyboard);
        }
        else
        {
            await EditOrSendMessageAsync(chatId, messageId, screen.Text, keyboard);
        }
    }

    public async Task AnswerCallbackAsync(string callbackId, string? alertText = null, bool showAlert = false)
    {
        try
        {
            await _botClient.AnswerCallbackAsync(new AnswerCallbackRequest
            {
                CallbackId = callbackId,
                Notification = alertText ?? "👌"
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to answer MAX callback {CallbackId}", callbackId);
        }
    }

    public async Task SendTextMessageAsync(long chatId, string text)
    {
        await SendUserMessageAsync(chatId, new SendMessageRequest
        {
            Text = text,
            Format = MessageFormat.Markdown
        });
    }

    public Task<bool> EnsureChannelSubscribedAsync(long chatId, string userId) => Task.FromResult(true);

    private static List<List<Button>> ConvertToMaxKeyboard(List<List<BotButton>> buttons)
    {
        var keyboard = new List<List<Button>>();
        if (buttons == null) return keyboard;

        foreach (var row in buttons)
        {
            var rowButtons = new List<Button>();
            foreach (var btn in row)
            {
                if (!string.IsNullOrEmpty(btn.MaxWebApp))
                    rowButtons.Add(new OpenAppButton { Text = btn.Text, WebApp = btn.MaxWebApp, Payload = btn.MaxPayload ?? "" });
                else if (!string.IsNullOrEmpty(btn.Url))
                    rowButtons.Add(new LinkButton { Text = btn.Text, Url = btn.Url });
                else if (!string.IsNullOrEmpty(btn.WebAppUrl))
                    rowButtons.Add(new LinkButton { Text = btn.Text, Url = btn.WebAppUrl });
                else
                    rowButtons.Add(new CallbackButton { Text = btn.Text, Payload = btn.CallbackData ?? "noop" });
            }
            keyboard.Add(rowButtons);
        }
        return keyboard;
    }

    private async Task SendOrEditBannerScreenAsync(long chatId, string? messageId, string bannerFileName, string text, List<List<Button>> keyboard)
    {
        var baseUrl = _config["MiniAppUrl"]?.TrimEnd('/') ?? "https://miragediary.space";
        var attachments = new List<Attachment>();

        if (!string.IsNullOrEmpty(bannerFileName))
        {
            attachments.Add(new ImageAttachment { Payload = new ImagePayload { Url = $"{baseUrl}/images/banners/{bannerFileName}" } });
        }
        if (keyboard != null && keyboard.Count > 0)
        {
            attachments.Add(new InlineKeyboardAttachment { Payload = new InlineKeyboardPayload { Buttons = keyboard } });
        }

        if (!string.IsNullOrEmpty(messageId))
        {
            try
            {
                await _botClient.EditMessageByIdAsync(messageId, new SendMessageRequest
                {
                    Text = text,
                    Format = MessageFormat.Markdown,
                    Attachments = attachments
                });
                return;
            }
            catch
            {
                try { await _botClient.DeleteMessageByIdAsync(messageId); } catch { }
            }
        }

        await SendUserMessageAsync(chatId, new SendMessageRequest
        {
            Text = text,
            Format = MessageFormat.Markdown,
            Attachments = attachments
        });
    }

    private async Task EditOrSendMessageAsync(long chatId, string? messageId, string text, List<List<Button>>? keyboard = null)
    {
        var attachments = keyboard != null && keyboard.Count > 0
            ? new List<Attachment> { new InlineKeyboardAttachment { Payload = new InlineKeyboardPayload { Buttons = keyboard } } }
            : null;

        if (!string.IsNullOrEmpty(messageId))
        {
            try
            {
                await _botClient.EditMessageByIdAsync(messageId, new SendMessageRequest
                {
                    Text = text,
                    Format = MessageFormat.Markdown,
                    Attachments = attachments
                });
                return;
            }
            catch
            {
                try
                {
                    await _botClient.EditMessageByIdAsync(messageId, new SendMessageRequest
                    {
                        Text = text,
                        Format = null,
                        Attachments = attachments
                    });
                    return;
                }
                catch
                {
                    try { await _botClient.DeleteMessageByIdAsync(messageId); } catch { }
                }
            }
        }

        await SendUserMessageAsync(chatId, new SendMessageRequest
        {
            Text = text,
            Format = MessageFormat.Markdown,
            Attachments = attachments
        });
    }

    private async Task SendUserMessageAsync(long userId, SendMessageRequest request)
    {
        try
        {
            request.UserId = userId;
            request.ChatId = null;
            var resp = await _botClient.SendMessageAsync(request);
            if (resp?.Message?.Body?.Mid != null)
                RecordMessageId(userId, resp.Message.Body.Mid);
        }
        catch (Exception) when (request.Format == MessageFormat.Markdown)
        {
            try
            {
                request.Format = null;
                var resp = await _botClient.SendMessageAsync(request);
                if (resp?.Message?.Body?.Mid != null)
                    RecordMessageId(userId, resp.Message.Body.Mid);
            }
            catch (Exception retryEx)
            {
                _logger.LogError(retryEx, "Failed to send MAX message in PlainText fallback to user {UserId}", userId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send MAX message to user {UserId}", userId);
        }
    }

    public async Task DeleteChatHistoryAsync(long chatId)
    {
        try
        {
            await _botClient.DeleteChatAsync(chatId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to DeleteChatAsync for MAX user {ChatId}", chatId);
        }

        if (_chatMessageHistory.TryRemove(chatId, out var queue))
        {
            while (queue.TryDequeue(out var mid))
            {
                if (!string.IsNullOrEmpty(mid))
                {
                    try
                    {
                        await _botClient.DeleteMessageByIdAsync(mid);
                    }
                    catch { }
                }
            }
        }
    }

    private static bool CheckUserCooldown(long chatId)
    {
        var now = DateTime.UtcNow;
        if (_userCooldowns.TryGetValue(chatId, out var lastTime) && (now - lastTime).TotalSeconds < 1.0)
            return false;
        _userCooldowns[chatId] = now;
        return true;
    }
}
