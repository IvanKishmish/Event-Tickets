using Telegram.Bot.Types.ReplyMarkups;

namespace EventTickets.Telegram;

public static class TelegramKeyboards
{
    public const string UserEvents = "📅 Події";
    public const string UserMyTicket = "🔍 Мій квиток";

    public const string AdminStats = "📊 Статистика";
    public const string AdminNewOrders = "📩 Нові замовлення";
    public const string AdminSettings = "⚙️ Налаштування";

    public static ReplyKeyboardMarkup UserKeyboard()
    {
        return new ReplyKeyboardMarkup([
            [new KeyboardButton(UserEvents), new KeyboardButton(UserMyTicket)]
        ])
        {
            ResizeKeyboard = true
        };
    }

    public static ReplyKeyboardMarkup AdminKeyboard()
    {
        return new ReplyKeyboardMarkup([
            [new KeyboardButton(AdminStats), new KeyboardButton(AdminNewOrders)],
            [new KeyboardButton(AdminSettings)]
        ])
        {
            ResizeKeyboard = true
        };
    }
}