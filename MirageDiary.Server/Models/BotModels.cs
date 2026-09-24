using System.Collections.Generic;

namespace MirageDiary.Server.Models;

public class BotButton
{
    public string Text { get; set; } = string.Empty;
    public string? CallbackData { get; set; }
    public string? Url { get; set; }
    public string? WebAppUrl { get; set; }
    public string? MaxWebApp { get; set; }
    public string? MaxPayload { get; set; }

    public BotButton() { }

    public BotButton(string text, string callbackData)
    {
        Text = text;
        CallbackData = callbackData;
    }

    public static BotButton Callback(string text, string data) => new(text, data);
    public static BotButton Link(string text, string url) => new() { Text = text, Url = url };
    public static BotButton WebApp(string text, string url) => new() { Text = text, WebAppUrl = url };
    public static BotButton MaxApp(string text, string webApp, string payload) => new() { Text = text, MaxWebApp = webApp, MaxPayload = payload };
}

public class BotScreen
{
    public string Text { get; set; } = string.Empty;
    public string? BannerName { get; set; }
    public string? BannerFileName { get => BannerName; set => BannerName = value; }
    public List<List<BotButton>> Buttons { get; set; } = new();
    public bool ParseAsMarkdown { get; set; } = true;
    public string? AlertNotification { get; set; }

    public static BotScreen Create(string text, string? bannerName = null) => new()
    {
        Text = text,
        BannerName = bannerName
    };

    public BotScreen WithButtons(List<List<BotButton>> buttons)
    {
        Buttons = buttons;
        return this;
    }

    public BotScreen WithAlert(string alert)
    {
        AlertNotification = alert;
        return this;
    }
}

public class BotCallbackResult
{
    public BotScreen? Screen { get; set; }
    public BotScreen? NextScreen { get => Screen; set => Screen = value; }
    public string? AlertText { get; set; }
    public bool ShowAlert { get; set; }

    public static BotCallbackResult Ok(BotScreen? screen = null, string? alert = null, bool showAlert = false) => new()
    {
        Screen = screen,
        AlertText = alert,
        ShowAlert = showAlert
    };
}
