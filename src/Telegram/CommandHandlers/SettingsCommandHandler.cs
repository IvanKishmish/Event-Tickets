using EventTickets.Telegram.CommandHandlers;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace EventTickets.Telegram.CommandHandlers;

public class SettingsCommandHandler : ITelegramTextHandler
{
    public string[] Texts => new[]
    {
        "settings",
        TelegramKeyboards.AdminSettings
    };

    public async Task HandleAsync(TelegramBot bot, TelegramBotClient client, Message message, CancellationToken ct)
    {
        if (!bot.IsAdmin(message.Chat.Id))
        {
            await client.SendMessage(message.Chat.Id, "Цей пункт доступний лише адміну.", cancellationToken: ct);
            return;
        }

        await client.SendMessage(
            message.Chat.Id,
            "⚙️ Налаштування ще можна тут дописати: SMTP, токен бота, шаблони повідомлень, резервні дії.",
            cancellationToken: ct);
    }
}