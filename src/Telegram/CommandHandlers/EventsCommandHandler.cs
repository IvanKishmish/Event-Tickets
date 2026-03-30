using Telegram.Bot;
using Telegram.Bot.Types;

namespace EventTickets.Telegram.CommandHandlers;

public class EventsCommandHandler : ITelegramTextHandler
{
    public string[] Texts =>
    [
        "events",
        TelegramKeyboards.UserEvents
    ];

    public Task HandleAsync(TelegramBot bot, TelegramBotClient client, Message message, CancellationToken ct)
        => bot.ShowEventsAsync(message.Chat.Id, ct);
}