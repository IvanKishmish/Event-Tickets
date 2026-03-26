using EventTickets.Database.Entities;

namespace EventTickets.Telegram;

public interface ITelegramNotifier
{
    Task NotifyNewOrderAsync(TicketOrder order, Event eventObj, CancellationToken ct = default);
}