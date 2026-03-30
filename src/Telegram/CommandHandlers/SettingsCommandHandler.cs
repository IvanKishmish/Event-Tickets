using Telegram.Bot;
using Telegram.Bot.Types;

namespace EventTickets.Telegram.CommandHandlers;

public class SettingsCommandHandler : ITelegramTextHandler
{
    public string[] Texts => ["settings", TelegramKeyboards.AdminSettings];

    public Task HandleAsync(TelegramBot bot, TelegramBotClient client, Message message, CancellationToken ct)
        => bot.ShowSettingsAsync(message.Chat.Id, ct);
}