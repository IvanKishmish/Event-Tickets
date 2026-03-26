using EventTickets.Telegram.CommandHandlers;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace EventTickets.Telegram.CommandHandlers;

public class MyTicketCommandHandler : ITelegramTextHandler, ICommandHandler
{
    public string[] Commands => ["myticket", "ticket"];
    public string[] Texts => [TelegramKeyboards.UserMyTicket];

    public async Task HandleAsync(TelegramBot bot, TelegramBotClient client, Message message, CancellationToken ct)
    {
        await bot.PromptForOrderIdAsync(message.Chat.Id, ct);
    }
}