using Telegram.Bot;
using Telegram.Bot.Types;

namespace EventTickets.Telegram.CommandHandlers;

public interface ITelegramTextHandler
{
    string[] Commands { get; }
    Task HandleAsync(TelegramBot bot, TelegramBotClient client, Message message, CancellationToken ct);

}