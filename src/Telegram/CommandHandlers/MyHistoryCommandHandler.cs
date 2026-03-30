using Telegram.Bot;
using Telegram.Bot.Types;

namespace EventTickets.Telegram.CommandHandlers;

public class MyHistoryCommandHandler : ITelegramTextHandler
{
    public string[] Texts =>
    [
        "history",
        "orders",
        TelegramKeyboards.UserHistory
    ];

    public Task HandleAsync(TelegramBot bot, TelegramBotClient client, Message message, CancellationToken ct)
        => bot.PromptForHistoryEmailAsync(message.Chat.Id, ct);
}