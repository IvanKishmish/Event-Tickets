using System.Text;
using System.Text.Encodings.Web;
using EventTickets.Database;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace EventTickets.Telegram.CommandHandlers;

public class EventsCommandHandler : ITelegramTextHandler
{
    public string[] Texts =>
    [
        "events",
        "📅 Події",
        TelegramKeyboards.UserEvents
    ];

    public async Task HandleAsync(TelegramBot bot, TelegramBotClient client, Message message, CancellationToken ct)
    {
        await using var db = new AppDbContext();

        var events = await db.Events
            .OrderBy(e => e.StartDate)
            .Take(10)
            .ToListAsync(ct);

        if (events.Count == 0)
        {
            await client.SendMessage(message.Chat.Id, "Подій поки що немає.", cancellationToken: ct);
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("📅 <b>Актуальні події</b>\n");

        foreach (var e in events)
        {
            sb.AppendLine($"• <b>{HtmlEncoder.Default.Encode(e.Title)}</b>");
            sb.AppendLine($"  Дата: {e.StartDate:dd.MM.yyyy HH:mm}");
            sb.AppendLine($"  Ціна: {e.Price} грн");
            sb.AppendLine($"  Місця: {e.TotalSeats}\n");
        }

        await client.SendMessage(
            chatId: message.Chat.Id,
            text: sb.ToString(),
            parseMode: ParseMode.Html,
            cancellationToken: ct);
    }
}