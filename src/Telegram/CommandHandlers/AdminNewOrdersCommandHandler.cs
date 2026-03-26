using System.Text;
using EventTickets.Database;
using EventTickets.Enums.Conditions;
using EventTickets.Telegram.CommandHandlers;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace EventTickets.Telegram.CommandHandlers;

public class AdminNewOrdersCommandHandler : ITelegramTextHandler, ICommandHandler
{
    public string[] Commands => ["neworders"];
    public string[] Texts => [TelegramKeyboards.AdminNewOrders];

    public async Task HandleAsync(TelegramBot bot, TelegramBotClient client, Message message, CancellationToken ct)
    {
        if (!bot.IsAdmin(message.Chat.Id))
        {
            await client.SendMessage(message.Chat.Id, "Доступ заборонено.", cancellationToken: ct);
            return;
        }

        await using var db = new AppDbContext();

        var orders = await db.TicketOrders
            .Include(o => o.Event)
            .Where(o => o.Status == Status.Pending)
            .OrderByDescending(o => o.CreatedAt)
            .Take(5)
            .ToListAsync(ct);

        if (orders.Count == 0)
        {
            await client.SendMessage(message.Chat.Id, "Немає нових Pending-замовлень.", cancellationToken: ct);
            return;
        }

        foreach (var order in orders)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"📩 <b>Замовлення #{order.Id}</b>");
            sb.AppendLine($"Подія: {order.Event?.Title}");
            sb.AppendLine($"Кількість: {order.Quantity}");
            sb.AppendLine($"Сума: {order.TotalPrice} грн");
            sb.AppendLine($"Статус: {order.Status}");
            sb.AppendLine($"Клієнт: {order.ClientEmail}");

            var markup = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("✅ Підтвердити", $"order:confirm:{order.Id}"),
                    InlineKeyboardButton.WithCallbackData("❌ Скасувати", $"order:cancel:{order.Id}")
                }
            });

            await client.SendMessage(
                chatId: message.Chat.Id,
                text: sb.ToString(),
                parseMode: ParseMode.Html,
                replyMarkup: markup,
                cancellationToken: ct);
        }
    }
}