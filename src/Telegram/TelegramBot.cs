using System.Collections.Concurrent;
using System.Net.Mail;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EventTickets.Database;
using EventTickets.Database.Entities;
using EventTickets.Enums.Conditions;
using EventTickets.Logs;
using EventTickets.Services.Abstractions;
using EventTickets.Telegram.CommandHandlers;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace EventTickets.Telegram;

public class TelegramBot : ITelegramNotifier
{
    private readonly TelegramBotClient _client;
    private readonly HashSet<long> _adminIds;
    private readonly IMailSender _mailSender;

    private readonly ConcurrentDictionary<long, int> _lastBotMessageIds = new();
    private readonly ConcurrentDictionary<long, TelegramPendingAction> _pendingActions = new();
    private readonly ConcurrentDictionary<long, PendingBuyDraft> _pendingBuyDrafts = new();
    private readonly ConcurrentDictionary<int, byte> _notifiedOrderIds = new();

    private readonly Dictionary<string, ICommandHandler> _commandHandlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ITelegramTextHandler> _textHandlers = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Regex OrderIdRegex = new(@"\b\d+\b", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions EventJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed class PendingBuyDraft
    {
        public int EventId { get; init; }
        public int Quantity { get; set; }
    }

    public TelegramBot(string token, IEnumerable<long> adminIds, IMailSender mailSender)
    {
        _client = new TelegramBotClient(token);
        _adminIds = new HashSet<long>(adminIds);
        _mailSender = mailSender;

        RegisterHandlers();
    }

    public void Start() => _client.StartReceiving(HandleUpdateAsync, HandleErrorAsync);

    public bool IsAdmin(long chatId) => _adminIds.Contains(chatId);

    private void RegisterHandlers()
    {
        var types = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(SafeGetTypes)
            .Where(t => t is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false });

        foreach (var type in types)
        {
            try
            {
                if (typeof(ICommandHandler).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) != null)
                {
                    var handler = (ICommandHandler)Activator.CreateInstance(type)!;
                    foreach (var command in handler.Commands)
                    {
                        var key = NormalizeCommand(command);
                        if (!string.IsNullOrWhiteSpace(key))
                            _commandHandlers[key] = handler;
                    }
                }

                if (typeof(ITelegramTextHandler).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) != null)
                {
                    var handler = (ITelegramTextHandler)Activator.CreateInstance(type)!;
                    foreach (var text in handler.Texts)
                    {
                        var key = Normalize(text);
                        if (!string.IsNullOrWhiteSpace(key))
                            _textHandlers[key] = handler;
                    }
                }
            }
            catch (Exception ex)
            {
                ConcurrentLogger.Log($"⚠️ Handler registration skipped for {type.Name}: {ex.Message}", ConsoleColor.Yellow);
            }
        }
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t != null)!;
        }
    }

    private static string Normalize(string t) => t.Trim().ToLowerInvariant();
    private static string NormalizeCommand(string t) => t.Trim().TrimStart('/').ToLowerInvariant();

    public async Task SendCleanMessageAsync(
        long chatId,
        string text,
        ParseMode parseMode = ParseMode.None,
        ReplyMarkup? replyMarkup = null,
        CancellationToken ct = default)
    {
        if (_lastBotMessageIds.TryRemove(chatId, out int lastMessageId))
        {
            await TryDeleteMessageAsync(chatId, lastMessageId, ct);
        }

        var sent = await _client.SendMessage(
            chatId: chatId,
            text: text,
            parseMode: parseMode,
            replyMarkup: replyMarkup,
            cancellationToken: ct);

        _lastBotMessageIds[chatId] = sent.MessageId;
    }

    private async Task TryDeleteMessageAsync(long chatId, int messageId, CancellationToken ct)
    {
        try
        {
            await _client.DeleteMessage(chatId, messageId, cancellationToken: ct);
        }
        catch (ApiRequestException)
        {
        }
    }

    private async Task SafeAnswerCallbackAsync(string callbackId, string text, CancellationToken ct)
    {
        try
        {
            await _client.AnswerCallbackQuery(
                callbackQueryId: callbackId,
                text: text,
                showAlert: false,
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            ConcurrentLogger.Log($"⚠️ Callback answer error: {ex.Message}", ConsoleColor.Yellow);
        }
    }

    private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        try
        {
            // CALLBACKS (inline buttons)
            if (update.CallbackQuery is { } callbackQuery)
            {
                await HandleCallbackAsync(callbackQuery, ct);
                return;
            }

            // ONLY TEXT MESSAGES
            if (update.Message is not { Text: { } text } message)
                return;

            long chatId = message.Chat.Id;
            string normalizedText = Normalize(text);

            await TryDeleteMessageAsync(chatId, message.MessageId, ct);

            // START COMMAND (RESET STATE)
            if (text.Equals("/start", StringComparison.OrdinalIgnoreCase))
            {
                _pendingActions.TryRemove(chatId, out _);
                _pendingBuyDrafts.TryRemove(chatId, out _);

                await SendCleanMessageAsync(
                    chatId,
                    IsAdmin(chatId)
                        ? "Вітаю, адмін! Обери пункт меню:"
                        : "Вітаю! Обери пункт меню:",
                    replyMarkup: IsAdmin(chatId)
                        ? TelegramKeyboards.AdminKeyboard()
                        : TelegramKeyboards.UserKeyboard(),
                    ct: ct);

                return;
            }

            // OTHER COMMANDS (/help etc.)
            if (text.StartsWith('/'))
            {
                string key = NormalizeCommand(text);

                if (_commandHandlers.TryGetValue(key, out var commandHandler))
                {
                    await commandHandler.HandleAsync(this, _client, message, ct);
                    return;
                }
            }

            // USER IS IN "PENDING STATE" (PRIORITY BEFORE TEXT HANDLERS)
            if (_pendingActions.TryGetValue(chatId, out var action))
            {
                switch (action)
                {
                    case TelegramPendingAction.AwaitingOrderId:
                        await ProcessOrderLookupAsync(message, ct);
                        return;

                    case TelegramPendingAction.AwaitingBuyQuantity:
                        await ProcessBuyQuantityAsync(message, ct);
                        return;

                    case TelegramPendingAction.AwaitingBuyEmail:
                        await ProcessBuyEmailAsync(message, ct);
                        return;

                    case TelegramPendingAction.AwaitingHistoryEmail:
                        await ProcessHistoryEmailAsync(message, ct);
                        return;

                    case TelegramPendingAction.AwaitingNewEventJson:
                        await ProcessNewEventJsonAsync(message, ct);
                        return;
                }
            }

            // NORMAL TEXT BUTTON HANDLERS (MENU ACTIONS)
            if (_textHandlers.TryGetValue(normalizedText, out var textHandler))
            {
                _pendingActions.TryRemove(chatId, out _);
                _pendingBuyDrafts.TryRemove(chatId, out _);

                await textHandler.HandleAsync(this, _client, message, ct);
            }
        }
        catch (Exception ex)
        {
            ConcurrentLogger.Log($"🔥 Помилка TelegramBot під час HandleUpdateAsync: {ex}", ConsoleColor.Red);
        }
    }

    public async Task ShowEventsAsync(long chatId, CancellationToken ct)
    {
        await using var db = new AppDbContext();

        var events = await db.Events
            .AsNoTracking()
            .Where(e => e.TotalSeats > 0 && e.StartDate > DateTime.UtcNow)
            .OrderBy(e => e.StartDate)
            .Take(10)
            .ToListAsync(ct);

        if (events.Count == 0)
        {
            await SendCleanMessageAsync(
                chatId,
                "Наразі немає доступних подій.",
                replyMarkup: TelegramKeyboards.UserKeyboard(),
                ct: ct);
            return;
        }

        await SendCleanMessageAsync(
            chatId,
            "📅 <b>Актуальні події:</b>",
            ParseMode.Html,
            replyMarkup: TelegramKeyboards.UserKeyboard(),
            ct: ct);

        foreach (var ev in events)
        {
            string text = $"""
            <b>{HtmlEncoder.Default.Encode(ev.Title)}</b>
            📅 {ev.StartDate:dd.MM.yyyy HH:mm}
            💰 {ev.Price} грн
            🎟 Місць: {ev.TotalSeats}
            """;

            if (!string.IsNullOrWhiteSpace(ev.Description))
                text += $"\n📝 {HtmlEncoder.Default.Encode(ev.Description)}";

            var inlineMarkup = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("🎫 Купити квиток", $"buy:{ev.Id}")
                }
            });

            await _client.SendMessage(
                chatId: chatId,
                text: text,
                parseMode: ParseMode.Html,
                replyMarkup: inlineMarkup,
                cancellationToken: ct);
        }
    }

    public async Task ShowAdminStatsAsync(long chatId, CancellationToken ct)
    {
        if (!IsAdmin(chatId))
        {
            await SendCleanMessageAsync(chatId, "Доступ заборонено.", replyMarkup: TelegramKeyboards.UserKeyboard(), ct: ct);
            return;
        }

        await using var db = new AppDbContext();

        var confirmedQuery = db.TicketOrders.AsNoTracking().Where(o => o.Status == Status.Confirmed);
        var pendingQuery = db.TicketOrders.AsNoTracking().Where(o => o.Status == Status.Pending);
        var cancelledQuery = db.TicketOrders.AsNoTracking().Where(o => o.Status == Status.Cancelled);

        int confirmedOrders = await confirmedQuery.CountAsync(ct);
        int pendingOrders = await pendingQuery.CountAsync(ct);
        int cancelledOrders = await cancelledQuery.CountAsync(ct);
        int soldTickets = await confirmedQuery.SumAsync(o => (int?)o.Quantity, ct) ?? 0;
        decimal revenue = await confirmedQuery.SumAsync(o => (decimal?)o.TotalPrice, ct) ?? 0m;
        int remainingSeats = await db.Events.AsNoTracking().SumAsync(e => (int?)e.TotalSeats, ct) ?? 0;

        string text = $"""
        📊 <b>Статистика</b>

        ✅ Підтверджених замовлень: {confirmedOrders}
        ⏳ Pending-замовлень: {pendingOrders}
        ❌ Скасованих замовлень: {cancelledOrders}
        🎟 Продано квитків: {soldTickets}
        💰 Загальна виручка: {revenue} грн
        🪑 Залишок місць по всіх подіях: {remainingSeats}
        """;

        await SendCleanMessageAsync(
            chatId,
            text,
            ParseMode.Html,
            replyMarkup: TelegramKeyboards.AdminKeyboard(),
            ct: ct);
    }

    public async Task ShowPendingOrdersAsync(long chatId, CancellationToken ct)
    {
        if (!IsAdmin(chatId))
        {
            await SendCleanMessageAsync(chatId, "Доступ заборонено.", replyMarkup: TelegramKeyboards.UserKeyboard(), ct: ct);
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
            await SendCleanMessageAsync(
                chatId,
                "📭 Немає нових Pending-замовлень.",
                replyMarkup: TelegramKeyboards.AdminKeyboard(),
                ct: ct);
            return;
        }

        await SendCleanMessageAsync(
            chatId,
            "🔔 <b>Останні замовлення:</b>",
            ParseMode.Html,
            replyMarkup: TelegramKeyboards.AdminKeyboard(),
            ct: ct);

        foreach (var order in orders)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"📩 <b>Замовлення #{order.Id}</b>");
            sb.AppendLine($"Подія: {HtmlEncoder.Default.Encode(order.Event?.Title ?? "—")}");
            sb.AppendLine($"Кількість: {order.Quantity}");
            sb.AppendLine($"Сума: {order.TotalPrice} грн");
            sb.AppendLine($"Клієнт: {HtmlEncoder.Default.Encode(order.ClientEmail)}");

            var inlineMarkup = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("✅ Підтвердити", $"order:confirm:{order.Id}"),
                    InlineKeyboardButton.WithCallbackData("❌ Скасувати", $"order:cancel:{order.Id}")
                }
            });

            await _client.SendMessage(
                chatId: chatId,
                text: sb.ToString(),
                parseMode: ParseMode.Html,
                replyMarkup: inlineMarkup,
                cancellationToken: ct);
        }
    }

    public async Task ShowSettingsAsync(long chatId, CancellationToken ct)
    {
        if (!IsAdmin(chatId))
        {
            await SendCleanMessageAsync(chatId, "Цей пункт доступний лише адміну.", replyMarkup: TelegramKeyboards.UserKeyboard(), ct: ct);
            return;
        }

        string text = """
        ⚙️ Налаштування системи:

        • Статистика показує підтверджені, pending і скасовані замовлення.
        • Нові замовлення відкривають останні 5 pending-елементів.
        • Додавання події працює через JSON-форму.
        • Після кожного екрана меню повертається назад.
        """;

        await SendCleanMessageAsync(
            chatId,
            text,
            replyMarkup: TelegramKeyboards.AdminKeyboard(),
            ct: ct);
    }

    public async Task PromptForOrderIdAsync(long chatId, CancellationToken ct = default)
    {
        _pendingActions[chatId] = TelegramPendingAction.AwaitingOrderId;
        _pendingBuyDrafts.TryRemove(chatId, out _);

        await SendCleanMessageAsync(
            chatId,
            "🔍 Введіть номер вашого замовлення (ID):",
            replyMarkup: TelegramKeyboards.UserKeyboard(),
            ct: ct);
    }

    public async Task PromptForHistoryEmailAsync(long chatId, CancellationToken ct = default)
    {
        _pendingActions[chatId] = TelegramPendingAction.AwaitingHistoryEmail;
        _pendingBuyDrafts.TryRemove(chatId, out _);

        await SendCleanMessageAsync(
            chatId,
            "📜 Введіть email, який ви вказували в замовленні:",
            replyMarkup: TelegramKeyboards.UserKeyboard(),
            ct: ct);
    }

    public async Task PromptForNewEventJsonAsync(long chatId, CancellationToken ct = default)
    {
        if (!IsAdmin(chatId))
        {
            await SendCleanMessageAsync(chatId, "Доступ заборонено.", replyMarkup: TelegramKeyboards.UserKeyboard(), ct: ct);
            return;
        }

        _pendingActions[chatId] = TelegramPendingAction.AwaitingNewEventJson;
        _pendingBuyDrafts.TryRemove(chatId, out _);

        string template = """
        Надішли JSON для нової події в такому форматі:

        {"title":"Rock Night","description":"Live show","startDate":"2026-04-10T18:00:00Z","price":500,"category":"Concert","totalSeats":120}
        """;

        await SendCleanMessageAsync(
            chatId,
            template,
            replyMarkup: TelegramKeyboards.AdminKeyboard(),
            ct: ct);
    }

    private async Task BeginBuyFlowAsync(CallbackQuery query, int eventId, CancellationToken ct)
    {
        if (query.Message == null)
        {
            await SafeAnswerCallbackAsync(query.Id, "Повідомлення недоступне.", ct);
            return;
        }

        await using var db = new AppDbContext();

        var eventObj = await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == eventId, ct);
        if (eventObj == null)
        {
            await SafeAnswerCallbackAsync(query.Id, "Подію не знайдено.", ct);
            return;
        }

        if (eventObj.StartDate <= DateTime.UtcNow || eventObj.TotalSeats <= 0)
        {
            await SafeAnswerCallbackAsync(query.Id, "Ця подія вже недоступна.", ct);
            return;
        }

        long chatId = query.Message.Chat.Id;

        _pendingBuyDrafts[chatId] = new PendingBuyDraft { EventId = eventId, Quantity = 0 };
        _pendingActions[chatId] = TelegramPendingAction.AwaitingBuyQuantity;

        await SendCleanMessageAsync(
            chatId,
            $"🎫 <b>{HtmlEncoder.Default.Encode(eventObj.Title)}</b>\nВведи кількість квитків:",
            ParseMode.Html,
            replyMarkup: TelegramKeyboards.UserKeyboard(),
            ct: ct);

        await SafeAnswerCallbackAsync(query.Id, "Добре, починаємо покупку.", ct);
    }

    private async Task ProcessBuyQuantityAsync(Message message, CancellationToken ct)
    {
        long chatId = message.Chat.Id;
        string text = message.Text?.Trim() ?? string.Empty;

        if (!_pendingBuyDrafts.TryGetValue(chatId, out var draft))
        {
            _pendingActions.TryRemove(chatId, out _);
            await SendCleanMessageAsync(chatId, "Сесія покупки втрачена. Натисни кнопку ще раз.", replyMarkup: TelegramKeyboards.UserKeyboard(), ct: ct);
            return;
        }

        if (!int.TryParse(text, out int quantity) || quantity <= 0)
        {
            await SendCleanMessageAsync(chatId, "Введи коректну кількість — тільки число більше нуля.", replyMarkup: TelegramKeyboards.UserKeyboard(), ct: ct);
            return;
        }

        await using var db = new AppDbContext();
        var eventObj = await db.Events.FirstOrDefaultAsync(e => e.Id == draft.EventId, ct);

        if (eventObj == null)
        {
            _pendingActions.TryRemove(chatId, out _);
            _pendingBuyDrafts.TryRemove(chatId, out _);
            await SendCleanMessageAsync(chatId, "Подію не знайдено.", replyMarkup: TelegramKeyboards.UserKeyboard(), ct: ct);
            return;
        }

        if (eventObj.TotalSeats < quantity)
        {
            await SendCleanMessageAsync(chatId, $"Немає стільки місць. Доступно: {eventObj.TotalSeats}.", replyMarkup: TelegramKeyboards.UserKeyboard(), ct: ct);
            return;
        }

        draft.Quantity = quantity;
        _pendingActions[chatId] = TelegramPendingAction.AwaitingBuyEmail;

        await SendCleanMessageAsync(
            chatId,
            "Тепер введи email для отримання квитка:",
            replyMarkup: TelegramKeyboards.UserKeyboard(),
            ct: ct);
    }

    private async Task ProcessBuyEmailAsync(Message message, CancellationToken ct)
    {
        long chatId = message.Chat.Id;
        string email = message.Text?.Trim() ?? string.Empty;

        if (!_pendingBuyDrafts.TryGetValue(chatId, out var draft))
        {
            _pendingActions.TryRemove(chatId, out _);
            await SendCleanMessageAsync(chatId, "Сесія покупки втрачена. Натисни кнопку ще раз.", replyMarkup: TelegramKeyboards.UserKeyboard(), ct: ct);
            return;
        }

        if (!IsValidEmail(email))
        {
            await SendCleanMessageAsync(chatId, "Введи коректний email.", replyMarkup: TelegramKeyboards.UserKeyboard(), ct: ct);
            return;
        }

        await using var db = new AppDbContext();
        var eventObj = await db.Events.FirstOrDefaultAsync(e => e.Id == draft.EventId, ct);

        if (eventObj == null)
        {
            _pendingActions.TryRemove(chatId, out _);
            _pendingBuyDrafts.TryRemove(chatId, out _);
            await SendCleanMessageAsync(chatId, "Подію не знайдено.", replyMarkup: TelegramKeyboards.UserKeyboard(), ct: ct);
            return;
        }

        if (eventObj.TotalSeats < draft.Quantity)
        {
            await SendCleanMessageAsync(chatId, $"Немає стільки місць. Доступно: {eventObj.TotalSeats}.", replyMarkup: TelegramKeyboards.UserKeyboard(), ct: ct);
            return;
        }

        var order = new TicketOrder
        {
            EventId = eventObj.Id,
            Event = eventObj,
            Quantity = draft.Quantity,
            ClientEmail = email,
            TotalPrice = eventObj.Price * draft.Quantity,
            CreatedAt = DateTime.UtcNow,
            Status = Status.Pending
        };

        eventObj.TotalSeats -= draft.Quantity;

        db.TicketOrders.Add(order);
        await db.SaveChangesAsync(ct);

        try
        {
            string subject = $"🎫 Квиток: {eventObj.Title}";
            string htmlBody = $"""
                <div style='font-family: sans-serif; border: 1px solid #ddd; border-radius: 10px; padding: 20px; max-width: 500px; margin: auto;'>
                    <h2 style='text-align:center;'>Дякуємо за замовлення!</h2>
                    <p><b>Подія:</b> {HtmlEncoder.Default.Encode(eventObj.Title)}</p>
                    <p><b>Дата:</b> {eventObj.StartDate:dd.MM.yyyy HH:mm}</p>
                    <p><b>Кількість квитків:</b> {draft.Quantity}</p>
                    <p><b>Сума до сплати:</b> {order.TotalPrice} грн</p>
                    <p><b>Статус:</b> Очікує підтвердження</p>
                    <p><b>Номер замовлення:</b> #{order.Id}</p>
                </div>
                """;

            bool sent = await _mailSender.SendMailAsync(subject, htmlBody, true, [email]);
            if (!sent)
            {
                ConcurrentLogger.Log($"[MAIL ERROR] Не вдалося надіслати лист на {email} для замовлення #{order.Id}", ConsoleColor.Red);
            }
        }
        catch (Exception ex)
        {
            ConcurrentLogger.Log($"[MAIL EXCEPTION] {ex.Message}", ConsoleColor.Red);
        }

        await NotifyNewOrderAsync(order, eventObj, ct);

        _pendingActions.TryRemove(chatId, out _);
        _pendingBuyDrafts.TryRemove(chatId, out _);

        string ok = $"""
        ✅ Замовлення створено.

        <b>Номер:</b> #{order.Id}
        <b>Подія:</b> {HtmlEncoder.Default.Encode(eventObj.Title)}
        <b>Кількість:</b> {draft.Quantity}
        <b>Сума:</b> {order.TotalPrice} грн
        <b>Статус:</b> {order.Status}
        """;

        await SendCleanMessageAsync(
            chatId,
            ok,
            ParseMode.Html,
            replyMarkup: TelegramKeyboards.UserKeyboard(),
            ct: ct);
    }

    private async Task ProcessHistoryEmailAsync(Message message, CancellationToken ct)
    {
        long chatId = message.Chat.Id;
        string email = message.Text?.Trim() ?? string.Empty;

        if (!IsValidEmail(email))
        {
            await SendCleanMessageAsync(chatId, "Введи коректний email.", replyMarkup: TelegramKeyboards.UserKeyboard(), ct: ct);
            return;
        }

        await using var db = new AppDbContext();

        var orders = await db.TicketOrders
            .AsNoTracking()
            .Include(o => o.Event)
            .Where(o => o.ClientEmail.ToLower() == email.ToLower())
            .OrderByDescending(o => o.CreatedAt)
            .Take(10)
            .ToListAsync(ct);

        _pendingActions.TryRemove(chatId, out _);

        if (orders.Count == 0)
        {
            await SendCleanMessageAsync(
                chatId,
                "За цим email замовлень не знайдено.",
                replyMarkup: TelegramKeyboards.UserKeyboard(),
                ct: ct);
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("<b>📜 Моя історія замовлень</b>");
        sb.AppendLine();

        foreach (var order in orders)
        {
            sb.AppendLine($"<b>#{order.Id}</b>");
            sb.AppendLine($"Подія: {HtmlEncoder.Default.Encode(order.Event?.Title ?? "—")}");
            sb.AppendLine($"Дата: {(order.Event != null ? order.Event.StartDate.ToString("dd.MM.yyyy HH:mm") : "—")}");
            sb.AppendLine($"Кількість: {order.Quantity}");
            sb.AppendLine($"Сума: {order.TotalPrice} грн");
            sb.AppendLine($"Статус: {order.Status}");
            sb.AppendLine();
        }

        await SendCleanMessageAsync(
            chatId,
            sb.ToString(),
            ParseMode.Html,
            replyMarkup: TelegramKeyboards.UserKeyboard(),
            ct: ct);
    }

    private async Task ProcessOrderLookupAsync(Message message, CancellationToken ct)
    {
        long chatId = message.Chat.Id;
        string text = message.Text?.Trim() ?? string.Empty;
        var match = OrderIdRegex.Match(text);

        if (!match.Success || !int.TryParse(match.Value, out int id))
        {
            await SendCleanMessageAsync(
                chatId,
                "❌ Введи коректний номер замовлення — тільки число.",
                replyMarkup: TelegramKeyboards.UserKeyboard(),
                ct: ct);
            return;
        }

        await using var db = new AppDbContext();
        var order = await db.TicketOrders
            .AsNoTracking()
            .Include(o => o.Event)
            .FirstOrDefaultAsync(o => o.Id == id, ct);

        _pendingActions.TryRemove(chatId, out _);

        string res = order == null
            ? "❌ Не знайдено."
            : $"""
              🎫 <b>Квиток #{order.Id}</b>
              Статус: {order.Status}
              Подія: {HtmlEncoder.Default.Encode(order.Event?.Title ?? "—")}
              Кількість: {order.Quantity}
              Сума: {order.TotalPrice} грн
              """;

        await SendCleanMessageAsync(
            chatId,
            res,
            ParseMode.Html,
            replyMarkup: TelegramKeyboards.UserKeyboard(),
            ct: ct);
    }

    private async Task ProcessNewEventJsonAsync(Message message, CancellationToken ct)
    {
        long chatId = message.Chat.Id;

        if (!IsAdmin(chatId))
        {
            _pendingActions.TryRemove(chatId, out _);
            await SendCleanMessageAsync(chatId, "Доступ заборонено.", replyMarkup: TelegramKeyboards.UserKeyboard(), ct: ct);
            return;
        }

        string json = message.Text?.Trim() ?? string.Empty;

        try
        {
            var newEvent = JsonSerializer.Deserialize<Event>(json, EventJsonOptions);

            if (newEvent == null)
            {
                await SendCleanMessageAsync(chatId, "Не вдалося прочитати JSON.", replyMarkup: TelegramKeyboards.AdminKeyboard(), ct: ct);
                return;
            }

            if (string.IsNullOrWhiteSpace(newEvent.Title) || newEvent.Price < 0 || newEvent.TotalSeats < 0)
            {
                await SendCleanMessageAsync(chatId, "У JSON є помилка: title/price/totalSeats.", replyMarkup: TelegramKeyboards.AdminKeyboard(), ct: ct);
                return;
            }

            if (newEvent.StartDate.Kind == DateTimeKind.Unspecified)
                newEvent.StartDate = DateTime.SpecifyKind(newEvent.StartDate, DateTimeKind.Utc);
            else
                newEvent.StartDate = newEvent.StartDate.ToUniversalTime();

            await using var db = new AppDbContext();
            db.Events.Add(newEvent);
            await db.SaveChangesAsync(ct);

            _pendingActions.TryRemove(chatId, out _);

            await SendCleanMessageAsync(
                chatId,
                $"✅ Подію додано: <b>{HtmlEncoder.Default.Encode(newEvent.Title)}</b> (ID: {newEvent.Id})",
                ParseMode.Html,
                replyMarkup: TelegramKeyboards.AdminKeyboard(),
                ct: ct);
        }
        catch (Exception ex)
        {
            await SendCleanMessageAsync(
                chatId,
                $"❌ Помилка JSON: {HtmlEncoder.Default.Encode(ex.Message)}",
                ParseMode.Html,
                replyMarkup: TelegramKeyboards.AdminKeyboard(),
                ct: ct);
        }
    }

    private async Task HandleCallbackAsync(CallbackQuery query, CancellationToken ct)
    {
        if (query.Message == null)
        {
            await SafeAnswerCallbackAsync(query.Id, "Повідомлення недоступне.", ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(query.Data))
            return;

        string data = query.Data.Trim();

        if (data.StartsWith("buy:", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(data["buy:".Length..], out int eventId))
            {
                await BeginBuyFlowAsync(query, eventId, ct);
            }
            else
            {
                await SafeAnswerCallbackAsync(query.Id, "Невірний ID події.", ct);
            }

            return;
        }

        if (!IsAdmin(query.From.Id))
        {
            await SafeAnswerCallbackAsync(query.Id, "Недостатньо прав.", ct);
            return;
        }

        if (data.StartsWith("order:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = data.Split(':', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length != 3 || !int.TryParse(parts[2], out int orderId))
            {
                await SafeAnswerCallbackAsync(query.Id, "Невірний формат callback.", ct);
                return;
            }

            string action = parts[1].ToLowerInvariant();

            await using var db = new AppDbContext();

            // ❗ ВАЖЛИВО: беремо ТІЛЬКИ order + EventId (без rely на navigation)
            var order = await db.TicketOrders
                .FirstOrDefaultAsync(o => o.Id == orderId, ct);

            if (order == null)
            {
                await SafeAnswerCallbackAsync(query.Id, "Замовлення не знайдено.", ct);
                return;
            }

            if (order.Status != Status.Pending)
            {
                await SafeAnswerCallbackAsync(query.Id, $"Вже оброблено: {order.Status}", ct);
                return;
            }

            var ev = await db.Events
                .FirstOrDefaultAsync(e => e.Id == order.EventId, ct);

            if (ev == null)
            {
                await SafeAnswerCallbackAsync(query.Id, "Подію не знайдено.", ct);
                return;
            }

            long chatId = query.Message.Chat.Id;

            switch (action)
            {
                case "confirm":
                    order.Status = Status.Confirmed;

                    await db.SaveChangesAsync(ct);

                    await SendConfirmationEmailSafeAsync(order);

                    await _client.EditMessageText(
                        chatId: chatId,
                        messageId: query.Message.MessageId,
                        text: $"✅ Замовлення #{order.Id} підтверджено.",
                        cancellationToken: ct);

                    await SafeAnswerCallbackAsync(query.Id, "Підтверджено.", ct);
                    break;

                case "cancel":
                    order.Status = Status.Cancelled;

                    ev.TotalSeats += order.Quantity;
                    db.Events.Update(ev);

                    await db.SaveChangesAsync(ct);

                    await _client.EditMessageText(
                        chatId: chatId,
                        messageId: query.Message.MessageId,
                        text: $"❌ Замовлення #{order.Id} скасовано.",
                        cancellationToken: ct);

                    await SafeAnswerCallbackAsync(query.Id, "Скасовано.", ct);
                    break;

                default:
                    await SafeAnswerCallbackAsync(query.Id, "Невідома дія.", ct);
                    break;
            }

            return;
        }

        await SafeAnswerCallbackAsync(query.Id, "Невідомий callback.", ct);
    }

    private static bool IsValidEmail(string email)
    {
        try
        {
            _ = new MailAddress(email);
            return !email.EndsWith("@example.com", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public async Task NotifyNewOrderAsync(TicketOrder order, Event eventObj, CancellationToken ct = default)
    {
        if (!_notifiedOrderIds.TryAdd(order.Id, 0))
            return;

        string msg = $"""
        🔔 <b>Нове замовлення #{order.Id}</b>
        Подія: {HtmlEncoder.Default.Encode(eventObj.Title)}
        Кількість: {order.Quantity}
        Сума: {order.TotalPrice} грн
        Статус: {order.Status}
        """;

        foreach (var adminId in _adminIds)
        {
            try
            {
                await _client.SendMessage(
                    chatId: adminId,
                    text: msg,
                    parseMode: ParseMode.Html,
                    cancellationToken: ct);
            }
            catch (Exception ex)
            {
                ConcurrentLogger.Log($"⚠️ Не вдалося надіслати нотифікацію адміну {adminId}: {ex.Message}", ConsoleColor.Yellow);
            }
        }
    }

    private async Task SendConfirmationEmailSafeAsync(TicketOrder order)
    {
        try
        {
            string subject = $"🎫 Замовлення #{order.Id} підтверджено";

            string body = $"""
                <div style='font-family: sans-serif;'>
                    <h2>Ваше замовлення підтверджено</h2>
                    <p><b>Замовлення:</b> #{order.Id}</p>
                    <p><b>Кількість:</b> {order.Quantity}</p>
                    <p><b>Сума:</b> {order.TotalPrice} грн</p>
                </div>
                """;

            await _mailSender.SendMailAsync(subject, body, true, [order.ClientEmail]);
        }
        catch (Exception ex)
        {
            ConcurrentLogger.Log($"⚠️ Не вдалося відправити Email для замовлення #{order.Id}: {ex.Message}", ConsoleColor.Yellow);
        }
    }

    private Task HandleErrorAsync(ITelegramBotClient bot, Exception ex, CancellationToken ct)
    {
        ConcurrentLogger.Log($"🔥 Telegram receive error: {ex}", ConsoleColor.Red);
        return Task.CompletedTask;
    }
}