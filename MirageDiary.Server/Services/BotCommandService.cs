using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using MirageDiary.Server.Models;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace MirageDiary.Server.Services;

/// <summary>
/// Тонкий платформенный адаптер Telegram Bot API.
/// Отвечает исключительно за транспорт сетевых событий Telegram,
/// конвертацию клавиатур и отрисовку BotScreen через IBotPlatformAdapter.
/// </summary>
public class BotCommandService : IBotPlatformAdapter
{
    private static readonly ConcurrentDictionary<string, string> _bannerFileIdCache = new();
    private static readonly ConcurrentDictionary<long, string> _currentBannerByChat = new();
    private static readonly ConcurrentDictionary<long, DateTime> _userCooldowns = new();

    private readonly ITelegramBotClient _botClient;
    private readonly DiaryBotWorkflowEngine _engine;
    private readonly IConfiguration _config;

    public string Platform => "telegram";

    public BotCommandService(
        ITelegramBotClient botClient,
        DiaryBotWorkflowEngine engine,
        IConfiguration config)
    {
        _botClient = botClient;
        _engine = engine;
        _config = config;
    }

    public async Task HandleUpdateAsync(Update update)
    {
        if (update.Type == UpdateType.Message && update.Message != null)
        {
            var msg = update.Message;
            var chatId = msg.Chat.Id;

            if (!CheckUserCooldown(chatId))
            {
                await SafeSendMessageAsync(chatId, "⏳ _Защита от спама: подождите 2 секунды перед следующим запросом._");
                return;
            }

            if (msg.Type == MessageType.Photo || msg.Type == MessageType.Document)
            {
                string fileId = msg.Type == MessageType.Photo ? msg.Photo![^1].FileId : msg.Document!.FileId;
                string fileType = msg.Type == MessageType.Photo ? "photo" : "document";
                await _engine.HandleFileUploadAsync(this, chatId, chatId.ToString(), fileId, fileType);
                return;
            }

            string text = msg.Text?.Trim() ?? "";
            string? username = msg.From?.Username;
            string? firstName = msg.From?.FirstName;
            string? lastName = msg.From?.LastName;

            await _engine.ProcessMessageOrCommandAsync(this, chatId, chatId.ToString(), text, username, firstName, lastName);
        }
        else if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery != null)
        {
            var cq = update.CallbackQuery;
            var chatId = cq.Message?.Chat.Id;
            if (chatId == null) return;

            string? messageId = cq.Message?.MessageId.ToString();
            string data = cq.Data ?? "";

            await _engine.ProcessCallbackAsync(this, chatId.Value, messageId, chatId.Value.ToString(), cq.Id, data);
        }
    }

    public async Task RenderScreenAsync(long chatId, string? messageId, BotScreen screen)
    {
        int? msgId = int.TryParse(messageId, out int parsed) ? parsed : null;
        var markup = ConvertToTelegramMarkup(screen.Buttons);

        if (!string.IsNullOrEmpty(screen.FileId))
        {
            if (screen.FileType == "photo")
            {
                await _botClient.SendPhoto(chatId, InputFile.FromFileId(screen.FileId), caption: screen.Text, parseMode: ParseMode.Markdown, replyMarkup: markup);
            }
            else
            {
                await _botClient.SendDocument(chatId, InputFile.FromFileId(screen.FileId), caption: screen.Text, parseMode: ParseMode.Markdown, replyMarkup: markup);
            }
            return;
        }

        if (!string.IsNullOrEmpty(screen.BannerFileName))
        {
            await SendOrEditBannerScreenAsync(chatId, msgId, screen.BannerFileName, screen.Text, markup);
        }
        else
        {
            if (msgId.HasValue)
            {
                try
                {
                    await _botClient.EditMessageText(chatId, msgId.Value, screen.Text, ParseMode.Markdown, replyMarkup: markup);
                    return;
                }
                catch { }
            }
            await _botClient.SendMessage(chatId, screen.Text, ParseMode.Markdown, replyMarkup: markup);
        }
    }

    public async Task AnswerCallbackAsync(string callbackId, string? alertText = null, bool showAlert = false)
    {
        try
        {
            await _botClient.AnswerCallbackQuery(callbackId, alertText, showAlert);
        }
        catch { }
    }

    public async Task SendTextMessageAsync(long chatId, string text)
    {
        await SafeSendMessageAsync(chatId, text);
    }

    public async Task<bool> EnsureChannelSubscribedAsync(long chatId, string userId)
    {
        var chName = _config["Telegram:RequiredChannel"] ?? "";
        if (string.IsNullOrWhiteSpace(chName)) return true;

        try
        {
            var m = await _botClient.GetChatMember(chName, chatId);
            return m.Status is ChatMemberStatus.Member or ChatMemberStatus.Administrator or ChatMemberStatus.Creator or ChatMemberStatus.Restricted;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ChannelCheck] Error on check: {ex.Message}");
            return true;
        }
    }

    private static InlineKeyboardMarkup ConvertToTelegramMarkup(List<List<BotButton>> buttons)
    {
        var rows = new List<IEnumerable<InlineKeyboardButton>>();
        foreach (var row in buttons)
        {
            var rowButtons = new List<InlineKeyboardButton>();
            foreach (var btn in row)
            {
                if (!string.IsNullOrEmpty(btn.WebAppUrl))
                    rowButtons.Add(InlineKeyboardButton.WithWebApp(btn.Text, new WebAppInfo { Url = btn.WebAppUrl }));
                else if (!string.IsNullOrEmpty(btn.Url))
                    rowButtons.Add(InlineKeyboardButton.WithUrl(btn.Text, btn.Url));
                else
                    rowButtons.Add(InlineKeyboardButton.WithCallbackData(btn.Text, btn.CallbackData ?? ""));
            }
            rows.Add(rowButtons);
        }
        return new InlineKeyboardMarkup(rows);
    }

    private async Task SendOrEditBannerScreenAsync(long chatId, int? msgId, string bannerFileName, string caption, InlineKeyboardMarkup markup)
    {
        if (msgId.HasValue && _currentBannerByChat.TryGetValue(chatId, out var currentBanner) && currentBanner == bannerFileName)
        {
            try
            {
                await _botClient.EditMessageCaption(chatId, msgId.Value, caption: caption, parseMode: ParseMode.Markdown, replyMarkup: markup);
                return;
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (ex.Message.Contains("message is not modified"))
            {
                return;
            }
            catch { }
        }

        var candidatePaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "wwwroot", "images", "banners", bannerFileName),
            Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "images", "banners", bannerFileName),
            Path.Combine(Directory.GetCurrentDirectory(), "MirageDiary.Server", "wwwroot", "images", "banners", bannerFileName),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "wwwroot", "images", "banners", bannerFileName),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "MirageDiary.Server", "wwwroot", "images", "banners", bannerFileName)
        };
        var localPath = candidatePaths.FirstOrDefault(File.Exists) ?? candidatePaths[0];

        InputFile mediaFile;
        if (_bannerFileIdCache.TryGetValue(bannerFileName, out var cachedFileId))
            mediaFile = InputFile.FromFileId(cachedFileId);
        else if (File.Exists(localPath))
            mediaFile = InputFile.FromStream(new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read), bannerFileName);
        else
        {
            await SafeSendMessageAsync(chatId, caption, markup);
            return;
        }

        if (msgId.HasValue)
        {
            try
            {
                var inputMedia = new InputMediaPhoto(mediaFile) { Caption = caption, ParseMode = ParseMode.Markdown };
                var edited = await _botClient.EditMessageMedia(chatId, msgId.Value, inputMedia, replyMarkup: markup);
                if (edited is Message editedMsg && editedMsg.Photo != null && editedMsg.Photo.Length > 0)
                    _bannerFileIdCache[bannerFileName] = editedMsg.Photo[^1].FileId;
                _currentBannerByChat[chatId] = bannerFileName;
                return;
            }
            catch
            {
                try { await _botClient.DeleteMessage(chatId, msgId.Value); } catch { }
            }
        }

        try
        {
            var sent = await _botClient.SendPhoto(chatId, mediaFile, caption: caption, parseMode: ParseMode.Markdown, replyMarkup: markup);
            if (sent.Photo != null && sent.Photo.Length > 0)
                _bannerFileIdCache[bannerFileName] = sent.Photo[^1].FileId;
            _currentBannerByChat[chatId] = bannerFileName;
        }
        catch
        {
            await SafeSendMessageAsync(chatId, caption, markup);
        }
    }

    private async Task<Message?> SafeSendMessageAsync(long chatId, string text, InlineKeyboardMarkup? markup = null)
    {
        try { return await _botClient.SendMessage(chatId, text, ParseMode.Markdown, replyMarkup: markup); }
        catch { try { return await _botClient.SendMessage(chatId, text, replyMarkup: markup); } catch { return null; } }
    }

    private static bool CheckUserCooldown(long chatId)
    {
        var now = DateTime.UtcNow;
        if (_userCooldowns.TryGetValue(chatId, out var lastTime) && (now - lastTime).TotalSeconds < 2.0)
            return false;
        _userCooldowns[chatId] = now;
        return true;
    }
}
