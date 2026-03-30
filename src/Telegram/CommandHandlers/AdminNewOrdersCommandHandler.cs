using Telegram.Bot;
using Telegram.Bot.Types;

namespace EventTickets.Telegram.CommandHandlers;

public class AdminNewOrdersCommandHandler : ITelegramTextHandler
{
    public string[] Texts => ["neworders", TelegramKeyboards.AdminNewOrders];

    public Task HandleAsync(TelegramBot bot, TelegramBotClient client, Message message, CancellationToken ct)
        => bot.ShowPendingOrdersAsync(message.Chat.Id, ct);
}