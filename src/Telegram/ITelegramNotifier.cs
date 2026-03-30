using EventTickets.Database.Entities;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace EventTickets.Telegram;

public interface ITelegramNotifier
{
    Task NotifyNewOrderAsync(TicketOrder order, Event eventObj, CancellationToken ct = default);

    Task SendCleanMessageAsync(
        long chatId,
        string text,
        ParseMode parseMode = ParseMode.None,
        ReplyMarkup? replyMarkup = null,
        CancellationToken ct = default);
}