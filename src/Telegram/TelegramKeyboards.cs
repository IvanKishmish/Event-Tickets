using Telegram.Bot.Types.ReplyMarkups;

namespace EventTickets.Telegram;

public static class TelegramKeyboards
{
    public const string UserEvents = "📅 Події";
    public const string UserMyTicket = "🔍 Мій квиток";
    public const string UserHistory = "📜 Моя історія";

    public const string AdminStats = "📊 Статистика";
    public const string AdminNewOrders = "📩 Нові замовлення";
    public const string AdminAddEvent = "➕ Додати подію";
    public const string AdminSettings = "⚙️ Налаштування";

    public static ReplyKeyboardMarkup UserKeyboard() => new([
            [new KeyboardButton(UserEvents), new KeyboardButton(UserMyTicket)],
            [new KeyboardButton(UserHistory)]
        ])
        { ResizeKeyboard = true };

    public static ReplyKeyboardMarkup AdminKeyboard() => new([
            [new KeyboardButton(AdminStats), new KeyboardButton(AdminNewOrders)],
            [new KeyboardButton(AdminAddEvent), new KeyboardButton(AdminSettings)]
        ])
        { ResizeKeyboard = true };
}