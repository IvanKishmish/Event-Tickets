using System.Text;
using EventTickets.Database;
using EventTickets.Enums.Conditions;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace EventTickets.Telegram.CommandHandlers;

public class AdminNewOrdersCommandHandler : ITelegramTextHandler
{
    public string[] Texts => ["neworders", TelegramKeyboards.AdminNewOrders];

    public async Task HandleAsync(TelegramBot bot, TelegramBotClient client, Message message, CancellationToken ct)
    {
        if (!bot.IsAdmin(message.Chat.Id))
        {
            await bot.SendCleanMessageAsync(message.Chat.Id, "Доступ заборонено.", ct: ct);
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
            // Видаляємо старе повідомлення і показуємо, що замовлень нема + лишаємо клавіатуру
            await bot.SendCleanMessageAsync(
                message.Chat.Id, 
                "📭 Немає нових Pending-замовлень.", 
                replyMarkup: TelegramKeyboards.AdminKeyboard(), 
                ct: ct);
            return;
        }

        // Якщо замовлення є, спочатку "чистимо" чат заголовком
        await bot.SendCleanMessageAsync(
            message.Chat.Id, 
            "<b>🔔 Останні замовлення:</b>", 
            ParseMode.Html, 
            replyMarkup: TelegramKeyboards.AdminKeyboard(), 
            ct: ct);

        foreach (var order in orders)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"📩 <b>Замовлення #{order.Id}</b>");
            sb.AppendLine($"Сума: {order.TotalPrice} грн");
            sb.AppendLine($"Клієнт: {order.ClientEmail}");

            var inlineMarkup = new InlineKeyboardMarkup([
                [
                    InlineKeyboardButton.WithCallbackData("✅ Підтвердити", $"order:confirm:{order.Id}"),
                    InlineKeyboardButton.WithCallbackData("❌ Скасувати", $"order:cancel:{order.Id}")
                ]
            ]);

            await client.SendMessage(message.Chat.Id, sb.ToString(), ParseMode.Html, replyMarkup: inlineMarkup, cancellationToken: ct);
        }
    }
}