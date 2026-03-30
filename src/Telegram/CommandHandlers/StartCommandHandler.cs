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
            ? "Вітаю, адмін! Ось панель керування:"
            : "Ласкаво просимо до EventTickets! Тут ти можеш переглянути події та статус свого квитка.";

        await bot.SendCleanMessageAsync(
            chatId: message.Chat.Id,
            text: welcomeText,
            replyMarkup: isAdmin ? TelegramKeyboards.AdminKeyboard() : TelegramKeyboards.UserKeyboard(),
            ct: ct);
    }
}