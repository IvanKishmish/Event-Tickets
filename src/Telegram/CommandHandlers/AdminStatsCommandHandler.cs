using EventTickets.Database;
using EventTickets.Enums.Conditions;
using EventTickets.Telegram.CommandHandlers;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace EventTickets.Telegram.CommandHandlers;

public class AdminStatsCommandHandler : ITelegramTextHandler, ICommandHandler
{
    public string[] Commands => ["stats"];
    public string[] Texts => [TelegramKeyboards.AdminStats];

    public async Task HandleAsync(TelegramBot bot, TelegramBotClient client, Message message, CancellationToken ct)
    {
        if (!bot.IsAdmin(message.Chat.Id))
        {
            await client.SendMessage(message.Chat.Id, "Доступ заборонено.", cancellationToken: ct);
            return;
        }

        await using var db = new AppDbContext();

        var confirmedOrders = await db.TicketOrders
            .Where(o => o.Status == Status.Confirmed)
            .ToListAsync(ct);

        int count = confirmedOrders.Count;
        decimal total = confirmedOrders.Sum(o => o.TotalPrice);

        string text = $@"
            📊 <b>Статистика</b>

            <b>Підтверджених замовлень:</b> {count}
            <b>Загальна сума:</b> {total} грн";

        await client.SendMessage(
            message.Chat.Id,
            text,
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }
}