using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using EventTickets.Database;
using EventTickets.Database.Entities;
using EventTickets.Enums.Conditions;
using EventTickets.Logs;
using EventTickets.Services.Abstractions;
using EventTickets.Telegram.CommandHandlers;
using EventTickets.Utils; // Додано namespace для ConcurrentHashSet
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace EventTickets.Telegram;

public class TelegramBot : ITelegramNotifier
{
    private readonly TelegramBotClient _client;
    private readonly List<long> _adminIds;
    private readonly IMailSender _mailSender;

    // Сховище для ID останнього повідомлення БОТА (щоб видаляти/редагувати свої повідомлення)
    private readonly ConcurrentDictionary<long, int> _lastBotMessageIds = new();
    
    // Сховище для останньої команди/кнопки ЮЗЕРА (щоб видаляти дублікати натискань)
    private readonly ConcurrentDictionary<long, string> _lastUserCommands = new();
    
    // Зберігаємо ID замовлень, про які вже надіслано сповіщення (протягом сесії роботи бота)
    private readonly ConcurrentHashSet<int> _notifiedOrderIds = new();
    
    private readonly ConcurrentDictionary<long, TelegramPendingAction> _pendingActions = new();
    private readonly Dictionary<string, Type> _commandHandlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Type> _textHandlers = new(StringComparer.OrdinalIgnoreCase);

    public TelegramBot(string token, List<long> adminIds, IMailSender mailSender)
    {
        _client = new TelegramBotClient(token);
        _adminIds = adminIds;
        _mailSender = mailSender;

        RegisterHandlers();
    }
    
    private void RegisterHandlers()
    {
        var types = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(s => s.GetTypes())
            .Where(t => !t.IsAbstract && !t.IsInterface);

        foreach (var type in types)
        {
            if (typeof(ICommandHandler).IsAssignableFrom(type))
            {
                var handler = (ICommandHandler)Activator.CreateInstance(type)!;
                foreach (var command in handler.Commands)
                {
                    string key = command.TrimStart('/').ToLowerInvariant();
                    _commandHandlers[key] = type;
                    ConcurrentLogger.Log($"✅ Зареєстровано команду: {key}");
                }
            }

            if (typeof(ITelegramTextHandler).IsAssignableFrom(type))
            {
                var handler = (ITelegramTextHandler)Activator.CreateInstance(type)!;
                foreach (var text in handler.Texts)
                {
                    _textHandlers[Normalize(text)] = type;
                    ConcurrentLogger.Log($"📝 Зареєстровано текст: {text}");
                }
            }
        }
    }

    public void Start()
    {
        _client.StartReceiving(HandleUpdateAsync, HandleErrorAsync);
        ConcurrentLogger.Log("🤖 Telegram Bot started...", ConsoleColor.Green);
    }

    public bool IsAdmin(long chatId) => _adminIds.Contains(chatId);

    // --- СЕКЦІЯ ОЧИЩЕННЯ ЧАТУ ---

    /// <summary>
    /// Надсилає нове повідомлення, попередньо видаляючи старе повідомлення бота в цьому чаті.
    /// </summary>
    public async Task SendCleanMessageAsync(
        long chatId,
        string text,
        ParseMode parseMode = ParseMode.None,
        ReplyMarkup? replyMarkup = null,
        CancellationToken ct = default)
    {
        // Видаляємо старе повідомлення бота, якщо воно було
        if (_lastBotMessageIds.TryRemove(chatId, out int lastMessageId))
        {
            try
            {
                await _client.DeleteMessage(chatId, lastMessageId, ct);
            }
            catch { /* Ігноруємо помилки видалення (наприклад, якщо повідомлення вже видалено юзером) */ }
        }

        // Надсилаємо нове
        var sentMessage = await _client.SendMessage(
            chatId: chatId,
            text: text,
            parseMode: parseMode,
            replyMarkup: replyMarkup,
            cancellationToken: ct);

        // Запам'ятовуємо ID нового повідомлення
        _lastBotMessageIds[chatId] = sentMessage.MessageId;
    }

    private async Task TryDeleteUserMessageAsync(long chatId, int messageId, CancellationToken ct)
    {
        try
        {
            await _client.DeleteMessage(chatId, messageId, ct);
        }
        catch { /* Бот не завжди може видаляти повідомлення юзера (залежить від прав) */ }
    }

    // --- КІНЕЦЬ СЕКЦІЇ ОЧИЩЕННЯ ---

    public async Task PromptForOrderIdAsync(long chatId, CancellationToken ct = default)
    {
        _pendingActions[chatId] = TelegramPendingAction.AwaitingOrderId;

        await SendCleanMessageAsync(
            chatId,
            "<b>Введіть ID замовлення</b> або номер виду <code>#ORD-12</code>.",
            ParseMode.Html,
            replyMarkup: TelegramKeyboards.UserKeyboard(),
            ct: ct);
    }

    public async Task NotifyNewOrderAsync(TicketOrder order, Event eventObj, CancellationToken ct = default)
    {
        // 1. ПЕРЕВІРКА НА ДУБЛІКАТИ
        // Метод Add поверне false, якщо такий ID вже є в списку
        if (!_notifiedOrderIds.Add(order.Id))
        {
            ConcurrentLogger.Log($"⚠️ Сповіщення про замовлення #{order.Id} вже надсилалося. Блокуємо дублікат.", ConsoleColor.Yellow);
            return;
        }

        string eventTitle = HtmlEncoder.Default.Encode(eventObj.Title);

        string text = $@"
🔔 <b>Нове замовлення #{order.Id}!</b>

<b>Подія:</b> {eventTitle}
<b>Кількість:</b> {order.Quantity}
<b>Сума:</b> {order.TotalPrice} грн
<b>Клієнт:</b> {HtmlEncoder.Default.Encode(order.ClientEmail)}
<b>Статус:</b> {order.Status}";

        var markup = new InlineKeyboardMarkup([
            [
                InlineKeyboardButton.WithCallbackData("✅ Підтвердити", $"order:confirm:{order.Id}"),
                InlineKeyboardButton.WithCallbackData("❌ Скасувати", $"order:cancel:{order.Id}")
            ]
        ]);

        foreach (var adminId in _adminIds)
        {
            try
            {
                await _client.SendMessage(adminId, text, ParseMode.Html, replyMarkup: markup, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                ConcurrentLogger.Log($"❌ Помилка надсилання сповіщення адміну {adminId}: {ex.Message}", ConsoleColor.Red);
            }
        }
    }
    
    private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        try
        {
            if (update.CallbackQuery is { } callbackQuery)
            {
                await HandleCallbackAsync(callbackQuery, ct);
                return;
            }

            if (update.Message is not { Text: { } text } message) return;

            long chatId = message.Chat.Id;
            string normalizedText = Normalize(text);

            // --- НОВА ЛОГІКА ВИДАЛЕННЯ ---
            // Якщо це команда (починається з /) або текст, який є в кнопках
            bool isKnownCommand = text.StartsWith('/') || 
                                  _textHandlers.ContainsKey(normalizedText) || 
                                  _pendingActions.ContainsKey(chatId);

            if (isKnownCommand)
            {
                // Видаляємо повідомлення юзера ВІДРАЗУ, щоб воно не стакалося
                await TryDeleteUserMessageAsync(chatId, message.MessageId, ct);
            }
            // --------------------------------

            // 1. Команди /
            if (text.StartsWith('/'))
            {
                string commandKey = text.Split(' ')[0].TrimStart('/').ToLowerInvariant();
                if (_commandHandlers.TryGetValue(commandKey, out var commandHandlerType))
                {
                    var handler = (ICommandHandler)Activator.CreateInstance(commandHandlerType)!;
                    await handler.HandleAsync(this, _client, message, ct);
                    return;
                }
            }

            // 2. Очікування вводу ID
            if (_pendingActions.TryGetValue(chatId, out var pending) && pending == TelegramPendingAction.AwaitingOrderId)
            {
                await ProcessOrderLookupAsync(message, ct);
                return;
            }

            // 3. Текстові кнопки меню
            if (_textHandlers.TryGetValue(normalizedText, out var textHandlerType))
            {
                var handler = (ITelegramTextHandler)Activator.CreateInstance(textHandlerType)!;
                await handler.HandleAsync(this, _client, message, ct);
                return;
            }

            await SendCleanMessageAsync(chatId, "Невідома команда 🧐", ct: ct);
        }
        catch (Exception ex)
        {
            ConcurrentLogger.Log($"🔥 [Bot Handle Error]: {ex.Message}", ConsoleColor.Red);
        }
    }

    private async Task HandleCallbackAsync(CallbackQuery callbackQuery, CancellationToken ct)
    {
        if (callbackQuery.Message == null) return;

        if (!_adminIds.Contains(callbackQuery.From.Id))
        {
            await _client.AnswerCallbackQuery(callbackQuery.Id, "Ця дія доступна лише адміну.", cancellationToken: ct);
            return;
        }

        var parts = callbackQuery.Data?.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts is null || parts.Length != 3 || parts[0] != "order") return;

        string action = parts[1];
        if (!int.TryParse(parts[2], out int orderId)) return;

        await using var db = new AppDbContext();
        var order = await db.TicketOrders.Include(o => o.Event).FirstOrDefaultAsync(o => o.Id == orderId, ct);

        if (order == null || order.Status != Status.Pending)
        {
            await _client.AnswerCallbackQuery(callbackQuery.Id, "Замовлення не знайдено або вже оброблене.", cancellationToken: ct);
            return;
        }

        if (action == "confirm")
        {
            order.Status = Status.Confirmed;
            await db.SaveChangesAsync(ct);
            await SendConfirmationEmailAsync(order);

            await _client.EditMessageText(callbackQuery.Message.Chat.Id, callbackQuery.Message.MessageId,
                $"✅ Замовлення #{order.Id} ПІДТВЕРДЖЕНО", cancellationToken: ct);
        }
        else if (action == "cancel")
        {
            if (order.Event != null) order.Event.TotalSeats += order.Quantity;
            order.Status = Status.Cancelled;
            await db.SaveChangesAsync(ct);
            await SendCancelEmailAsync(order);

            await _client.EditMessageText(callbackQuery.Message.Chat.Id, callbackQuery.Message.MessageId,
                $"❌ Замовлення #{order.Id} СКАСОВАНО", cancellationToken: ct);
        }
        
        await _client.AnswerCallbackQuery(callbackQuery.Id, "Готово!", cancellationToken: ct);
    }

    private async Task ProcessOrderLookupAsync(Message message, CancellationToken ct)
    {
        string text = message.Text?.Trim() ?? string.Empty;
        var match = Regex.Match(text, @"\d+");

        if (!match.Success || !int.TryParse(match.Value, out int orderId))
        {
            await SendCleanMessageAsync(
                message.Chat.Id, 
                "❌ Не вдалося прочитати ID замовлення. Спробуйте ще раз.", 
                replyMarkup: TelegramKeyboards.UserKeyboard(), // Повертаємо клавіатуру
                ct: ct);
            return;
        }

        await using var db = new AppDbContext();
        var order = await db.TicketOrders.Include(o => o.Event).FirstOrDefaultAsync(o => o.Id == orderId, ct);

        if (order == null)
        {
            await SendCleanMessageAsync(
                message.Chat.Id, 
                "🔍 Замовлення не знайдено.", 
                replyMarkup: TelegramKeyboards.UserKeyboard(), // Повертаємо клавіатуру
                ct: ct);
            return;
        }

        string result = $@"
<b>🎫 Ваш квиток</b>

<b>ID:</b> #ORD-{order.Id}
<b>Статус:</b> {order.Status}
<b>Подія:</b> {HtmlEncoder.Default.Encode(order.Event?.Title ?? "---")}
<b>Кількість:</b> {order.Quantity}
<b>Сума:</b> {order.TotalPrice} грн";

        // Тут клавіатура обов'язкова, щоб юзер міг піти в інший розділ
        await SendCleanMessageAsync(
            message.Chat.Id, 
            result, 
            ParseMode.Html, 
            replyMarkup: TelegramKeyboards.UserKeyboard(), 
            ct: ct);
    
        _pendingActions.TryRemove(message.Chat.Id, out _);
    }

    private async Task SendConfirmationEmailAsync(TicketOrder order)
    {
        string subject = $"Ваш квиток активовано #{order.Id}";
        string body = $"<h2>Ваше замовлення підтверджено</h2><p>Подія: {order.Event?.Title}</p>";
        await _mailSender.SendMailAsync(subject, body, true, [order.ClientEmail]);
    }

    private async Task SendCancelEmailAsync(TicketOrder order)
    {
        string subject = $"Замовлення #{order.Id} скасовано";
        string body = $"<h2>На жаль, замовлення скасовано</h2><p>Подія: {order.Event?.Title}</p>";
        await _mailSender.SendMailAsync(subject, body, true, [order.ClientEmail]);
    }

    private Task HandleErrorAsync(ITelegramBotClient bot, Exception ex, CancellationToken ct)
    {
        ConcurrentLogger.Log($"[Bot Error]: {ex.Message}", ConsoleColor.Red);
        return Task.CompletedTask;
    }

    private static string Normalize(string text) => text.Trim().ToLowerInvariant();
}