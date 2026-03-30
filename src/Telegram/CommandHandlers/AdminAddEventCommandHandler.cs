using Telegram.Bot;
using Telegram.Bot.Types;

namespace EventTickets.Telegram.CommandHandlers;

public class AdminAddEventCommandHandler : ITelegramTextHandler
{
    public string[] Texts => ["addevent", TelegramKeyboards.AdminAddEvent];

    public Task HandleAsync(TelegramBot bot, TelegramBotClient client, Message message, CancellationToken ct)
        => bot.PromptForNewEventJsonAsync(message.Chat.Id, ct);
}