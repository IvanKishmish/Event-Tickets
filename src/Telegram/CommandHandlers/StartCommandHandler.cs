using Telegram.Bot;
using Telegram.Bot.Types;

namespace EventTickets.Telegram.CommandHandlers;

public class StartCommandHandler : ICommandHandler
{
    public string[] Commands => ["start", "help"];

    public async Task HandleAsync(TelegramBot bot, TelegramBotClient client, Message message, CancellationToken ct)
    {
        bool isAdmin = bot.IsAdmin(message.Chat.Id);

        string welcomeText = isAdmin
            ? "Вітаю, Адмін! Ось панель керування замовленнями:"
            : "Ласкаво просимо до EventTickets! Тут ти можеш переглянути події та статус свого квитка.";

        await client.SendMessage(
            chatId: message.Chat.Id,
            text: welcomeText,
            replyMarkup: isAdmin ? TelegramKeyboards.AdminKeyboard() : TelegramKeyboards.UserKeyboard(),
            cancellationToken: ct);
    }
}