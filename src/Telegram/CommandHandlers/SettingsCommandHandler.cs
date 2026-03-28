using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace EventTickets.Telegram.CommandHandlers;

public class SettingsCommandHandler : ITelegramTextHandler
{
    public string[] Texts => ["settings", TelegramKeyboards.AdminSettings];

    public async Task HandleAsync(TelegramBot bot, TelegramBotClient client, Message message, CancellationToken ct)
    {
        if (!bot.IsAdmin(message.Chat.Id))
        {
            await bot.SendCleanMessageAsync(message.Chat.Id, "Цей пункт доступний лише адміну.", ct: ct);
            return;
        }

        // В кінці методу HandleAsync змініть на:
        await bot.SendCleanMessageAsync(
            message.Chat.Id,
            "⚙️ Налаштування системи:",
            replyMarkup: TelegramKeyboards.AdminKeyboard(), // Повертаємо клавіатуру адміну
            ct: ct);
    }
}