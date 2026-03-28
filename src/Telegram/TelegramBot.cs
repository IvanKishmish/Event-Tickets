using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using EventTickets.Database;
using EventTickets.Database.Entities;
using EventTickets.Enums.Conditions;
using EventTickets.Logs;
using EventTickets.Services.Abstractions;
using EventTickets.Telegram.CommandHandlers;
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
            // Реєстрація команд (наприклад, /start)
            if (typeof(ICommandHandler).IsAssignableFrom(type))
            {
                var handler = (ICommandHandler)Activator.CreateInstance(type)!;
                foreach (var command in handler.Commands)
                {
                    // Зберігаємо БЕЗ слеша для легкого пошуку
                    string key = command.TrimStart('/').ToLowerInvariant();
                    _commandHandlers[key] = type;
                    // Console.WriteLine($"✅ Зареєстровано команду: {key}");
                    ConcurrentLogger.Log($"✅ Зареєстровано команду: {key}");
                }
            }

            // Реєстрація текстових кнопок (наприклад, 📊 Статистика)
            if (typeof(ITelegramTextHandler).IsAssignableFrom(type))
            {
                var handler = (ITelegramTextHandler)Activator.CreateInstance(type)!;
                foreach (var text in handler.Texts)
                {
                    _textHandlers[Normalize(text)] = type;
                    // Console.WriteLine($"📝 Зареєстровано текст: {text}");
                    ConcurrentLogger.Log($"📝 Зареєстровано текст: {text}");
                }
            }
        }
    }

    public void Start()
    {
        _client.StartReceiving(HandleUpdateAsync, HandleErrorAsync);
        // Console.WriteLine("🤖 Telegram Bot started...");
        ConcurrentLogger.Log("🤖 Telegram Bot started...", ConsoleColor.Green);
    }

    public bool IsAdmin(long chatId) => _adminIds.Contains(chatId);

    private Task SetAwaitingOrderIdAsync(long chatId)
    {
        _pendingActions[chatId] = TelegramPendingAction.AwaitingOrderId;
        return Task.CompletedTask;
    }

    private void ClearPendingAction(long chatId)
    {
        _pendingActions.TryRemove(chatId, out _);
    }

    private async Task SendHtmlAsync(
        long chatId,
        string html,
        ReplyMarkup? replyMarkup = null,
        CancellationToken ct = default)
    {
        await _client.SendMessage(
            chatId: chatId,
            text: html,
            parseMode: ParseMode.Html,
            replyMarkup: replyMarkup,
            cancellationToken: ct);
    }

    private async Task SendPlainAsync(
        long chatId,
        string text,
        ReplyMarkup? replyMarkup = null,
        CancellationToken ct = default)
    {
        await _client.SendMessage(
            chatId: chatId,
            text: text,
            replyMarkup: replyMarkup,
            cancellationToken: ct);
    }

    public async Task PromptForOrderIdAsync(long chatId, CancellationToken ct = default)
    {
        await SetAwaitingOrderIdAsync(chatId);

        await SendPlainAsync(
            chatId,
            "Введіть ID замовлення або номер виду #ORD-12.",
            ct: ct);
    }

    public async Task NotifyNewOrderAsync(TicketOrder order, Event eventObj, CancellationToken ct = default)
    {
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
            await SendHtmlAsync(adminId, text, markup, ct);
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

            string normalizedText = Normalize(text);

            // 1. Якщо це команда зі слешем
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

            // 2. Якщо ми чекаємо від користувача введення ID (наприклад, після кнопки "Мій квиток")
            if (_pendingActions.TryGetValue(message.Chat.Id, out var pending) &&
                pending == TelegramPendingAction.AwaitingOrderId)
            {
                await ProcessOrderLookupAsync(message, ct);
                return;
            }

            // 3. Якщо це натискання на текстову кнопку меню
            if (_textHandlers.TryGetValue(normalizedText, out var textHandlerType))
            {
                var handler = (ITelegramTextHandler)Activator.CreateInstance(textHandlerType)!;
                await handler.HandleAsync(this, _client, message, ct);
                return;
            }

            // 4. Тільки якщо жоден варіант не підійшов
            await SendPlainAsync(message.Chat.Id, "Невідома команда 🧐", ct: ct);
        }
        catch (Exception ex)
        {
            // Console.WriteLine($"🔥 [Bot Handle Error]: {ex.Message}");
            ConcurrentLogger.Log($"🔥 [Bot Handle Error]: {ex.Message}", ConsoleColor.Red);
        }
    }

    private async Task HandleCallbackAsync(CallbackQuery callbackQuery, CancellationToken ct)
    {
        if (callbackQuery.Message == null)
            return;

        if (!_adminIds.Contains(callbackQuery.From.Id))
        {
            await _client.AnswerCallbackQuery(
                callbackQuery.Id,
                "Ця дія доступна лише адміну.",
                cancellationToken: ct);
            return;
        }

        var parts = callbackQuery.Data?.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts is null || parts.Length != 3 || parts[0] != "order")
            return;

        string action = parts[1];
        if (!int.TryParse(parts[2], out int orderId))
            return;

        await using var db = new AppDbContext();
        var order = await db.TicketOrders
            .Include(o => o.Event)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct);

        if (order == null)
        {
            await _client.AnswerCallbackQuery(callbackQuery.Id, "Замовлення не знайдено.", cancellationToken: ct);
            return;
        }

        if (order.Status != Status.Pending)
        {
            await _client.AnswerCallbackQuery(callbackQuery.Id, "Це замовлення вже оброблене.", cancellationToken: ct);
            return;
        }

        if (action == "confirm")
        {
            order.Status = Status.Confirmed;
            await db.SaveChangesAsync(ct);

            await SendConfirmationEmailAsync(order);

            await _client.EditMessageText(
                chatId: callbackQuery.Message.Chat.Id,
                messageId: callbackQuery.Message.MessageId,
                text: $"✅ Замовлення #{order.Id} ПІДТВЕРДЖЕНО",
                cancellationToken: ct);

            await _client.AnswerCallbackQuery(callbackQuery.Id, "Підтверджено ✅", cancellationToken: ct);
            return;
        }

        if (action == "cancel")
        {
            if (order.Event != null)
                order.Event.TotalSeats += order.Quantity;

            order.Status = Status.Cancelled;
            await db.SaveChangesAsync(ct);

            await SendCancelEmailAsync(order);

            await _client.EditMessageText(
                chatId: callbackQuery.Message.Chat.Id,
                messageId: callbackQuery.Message.MessageId,
                text: $"❌ Замовлення #{order.Id} СКАСОВАНО",
                cancellationToken: ct);

            await _client.AnswerCallbackQuery(callbackQuery.Id, "Скасовано ❌", cancellationToken: ct);
        }
    }

    private async Task ProcessOrderLookupAsync(Message message, CancellationToken ct)
    {
        string text = message.Text?.Trim() ?? string.Empty;

        var match = Regex.Match(text, @"\d+");
        if (!match.Success || !int.TryParse(match.Value, out int orderId))
        {
            await SendPlainAsync(message.Chat.Id, "Не вдалося прочитати ID замовлення.", ct: ct);
            return;
        }

        await using var db = new AppDbContext();
        var order = await db.TicketOrders
            .Include(o => o.Event)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct);

        if (order == null)
        {
            await SendPlainAsync(message.Chat.Id, "Замовлення не знайдено.", ct: ct);
            return;
        }

        string eventTitle = HtmlEncoder.Default.Encode(order.Event?.Title ?? "Невідома подія");
        string statusText = order.Status.ToString();

        string result = $@"
            <b>Ваш квиток</b>

            <b>ID:</b> #ORD-{order.Id}
            <b>Статус:</b> {statusText}
            <b>Подія:</b> {eventTitle}
            <b>Кількість:</b> {order.Quantity}
            <b>Сума:</b> {order.TotalPrice} грн";

        await SendHtmlAsync(message.Chat.Id, result, ct: ct);
        ClearPendingAction(message.Chat.Id);
    }

    private async Task SendConfirmationEmailAsync(TicketOrder order)
    {
        string subject = $"Ваш квиток активовано #{order.Id}";
        string body = $@"
        <h2>Ваше замовлення підтверджено</h2>
        <p><b>Подія:</b> {order.Event?.Title}</p>
        <p><b>Кількість:</b> {order.Quantity}</p>
        <p><b>Сума:</b> {order.TotalPrice} грн</p>
        <p><b>Статус:</b> Confirmed</p>";

        await _mailSender.SendMailAsync(subject, body, true, [order.ClientEmail]);
    }

    private async Task SendCancelEmailAsync(TicketOrder order)
    {
        string subject = $"Замовлення #{order.Id} скасовано";
        string body = $@"
        <h2>На жаль, замовлення скасовано</h2>
        <p><b>Подія:</b> {order.Event?.Title}</p>
        <p><b>Кількість:</b> {order.Quantity}</p>
        <p><b>Сума:</b> {order.TotalPrice} грн</p>
        <p><b>Статус:</b> Cancelled</p>";

        await _mailSender.SendMailAsync(subject, body, true, [order.ClientEmail]);
    }

    private Task HandleErrorAsync(ITelegramBotClient bot, Exception ex, CancellationToken ct)
    {
        // Console.WriteLine($"[Bot Error]: {ex.Message}");
        ConcurrentLogger.Log($"[Bot Error]: {ex.Message}", ConsoleColor.Red);
        return Task.CompletedTask;
    }

    // private static string? GetCommand(string text)
    // {
    //     if (!text.StartsWith('/'))
    //         return null;
    //
    //     return Normalize(text.Split(' ', 2)[0]);
    // }

    private static string Normalize(string text)
    {
        return text.Trim().ToLowerInvariant();
    }
}