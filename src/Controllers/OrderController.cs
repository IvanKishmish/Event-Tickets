using System.Net;
using System.Net.Mail;
using System.Text.Json;
using EventTickets.Database;
using EventTickets.Database.Entities;
using EventTickets.Logs;
using EventTickets.Services.Abstractions;
using EventTickets.Telegram;
using Microsoft.EntityFrameworkCore;

namespace EventTickets.Controllers;

public class OrderController(IMailSender mailSender, ITelegramNotifier telegramNotifier)
{
    public async Task CreateOrderAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        TicketOrder? order = null;
        try
        {
            order = await JsonSerializer.DeserializeAsync<TicketOrder>(request.InputStream);
        }
        catch (JsonException je)
        {
            ConcurrentLogger.Log($"[JSON Error]: {je.Message}", ConsoleColor.Red);
        }
        catch (Exception ex)
        {
            ConcurrentLogger.Log($"[Request Error]: {ex.Message}", ConsoleColor.Red);
        }
        
        if (order == null)
        {
            response.StatusCode = 400;
            response.Close();
            return;
        }
        
        await using AppDbContext db = new AppDbContext();
        
        var eventObj = await db.Events.FirstOrDefaultAsync(e => e.Id == order.EventId);

        if (eventObj == null)
        {
            response.StatusCode = 404;
            response.Close();
            return;
        }
        
        if (eventObj.TotalSeats < order.Quantity)
        {
            response.StatusCode = 409; // Conflict - місць немає
            response.Close();
            return;
        }
        
        order.TotalPrice = eventObj.Price * order.Quantity; 
        order.CreatedAt = DateTime.UtcNow;
        order.Status = Enums.Conditions.Status.Pending;
        
        eventObj.TotalSeats -= order.Quantity;
        
        order.Event =  eventObj;
        
        if (string.IsNullOrWhiteSpace(order.ClientEmail) || !IsValidEmail(order.ClientEmail))
        {
            response.StatusCode = 400;
            await response.OutputStream.FlushAsync();
            response.Close();
            return;
        }
        
        db.TicketOrders.Add(order);
        await db.SaveChangesAsync();
        
        string subject = $"🎫 Квиток активовано: {eventObj.Title}";
        string htmlBody = $@"
        <div style='font-family: sans-serif; border: 1px solid #ddd; border-radius: 10px; padding: 20px; max-width: 500px; margin: auto;'>
        <h2 style='color: #2e7d32; text-align: center;'>Дякуємо за замовлення!</h2>
        <hr style='border: 0; border-top: 1px solid #eee;'>
        <p><b>Подія:</b> {eventObj.Title}</p>
        <p><b>Дата:</b> {eventObj.StartDate:dd.MM.yyyy HH:mm}</p>
        <p><b>Кількість квитків:</b> {order.Quantity}</p>
        <p><b>Сума до сплати:</b> {order.TotalPrice} грн</p>
        <div style='background: #f9f9f9; padding: 15px; border-radius: 5px; text-align: center; margin-top: 20px;'>
            <p style='margin: 0; font-size: 12px; color: #666;'>Ваш номер замовлення:</p>
            <h3 style='margin: 5px 0;'>#ORD-{order.Id}</h3>
            <p style='font-size: 14px; color: #d32f2f;'><b>Статус:</b> Очікує підтвердження</p>
        </div>
        <p style='font-size: 12px; color: #999; text-align: center; margin-top: 20px;'>
            Будь ласка, збережіть цей лист. Ви зможете перевірити статус квитка за вашим ID.
        </p>
        </div>";

        bool sent = await mailSender.SendMailAsync(subject, htmlBody, true, [order.ClientEmail]);
        
        if(!sent)
            ConcurrentLogger.Log($"[MAIL ERROR] Не вдалося надіслати лист на {order.ClientEmail} для замовлення #{order.Id}", ConsoleColor.Red);
        
        await telegramNotifier.NotifyNewOrderAsync(order, eventObj);
        
        response.StatusCode = 200;
        response.ContentType = "application/json";
        
        await JsonSerializer.SerializeAsync(response.OutputStream, order);
        response.OutputStream.Close();
    }
    
    private static bool IsValidEmail(string email)
    {
        try
        {
            var _ = new MailAddress(email);
            return !email.EndsWith("@example.com", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public async Task GetOrderStatusAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        var segments = request.Url!.Segments
            .Select(s => s.Trim('/'))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        string idStr = segments.LastOrDefault() ?? "";

        if (!int.TryParse(idStr, out int id))
        {
            response.StatusCode = 400;
            response.Close();
            return;
        }

        await using var db = new AppDbContext();
        var order = await db.TicketOrders.FirstOrDefaultAsync(e => e.Id == id);

        if (order == null)
        {
            response.StatusCode = 404;
            response.Close();
            return;
        }

        var result = new { status = order.Status };

        response.StatusCode = 200;
        response.ContentType = "application/json";

        await JsonSerializer.SerializeAsync(response.OutputStream, result);
        response.OutputStream.Close();
    }

    // ОНОВЛЕНО: Тепер підтримує фільтр ?status=Pending (або 0, 1, 2)
    public async Task GetOrdersPublicAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        await using var db = new AppDbContext();
        var query = db.TicketOrders.AsQueryable();

        // Читаємо параметр status з URL
        string? statusStr = request.QueryString["status"];
        if (!string.IsNullOrWhiteSpace(statusStr) && Enum.TryParse<Enums.Conditions.Status>(statusStr, true, out var statusEnum))
        {
            query = query.Where(o => o.Status == statusEnum);
        }

        var orders = await query.Select(o => new
        {
            o.Id,
            o.EventId,
            o.Quantity,
            o.TotalPrice,
            o.CreatedAt,
            o.Status
        }).ToListAsync();

        response.StatusCode = 200;
        response.ContentType = "application/json";
        
        await JsonSerializer.SerializeAsync(response.OutputStream, orders, new JsonSerializerOptions { WriteIndented = true });
        response.OutputStream.Close();
    }

    // НОВЕ: Отримання історії замовлень користувача за Email
    public async Task GetMyOrdersAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        string? email = request.QueryString["email"];
        
        if (string.IsNullOrWhiteSpace(email))
        {
            response.StatusCode = 400; // Потрібно передати email
            response.Close();
            return;
        }

        await using var db = new AppDbContext();
        
        // Include(o => o.Event) дозволяє дістати назву події для історії
        var myOrders = await db.TicketOrders
            .Include(o => o.Event)
            .Where(o => o.ClientEmail == email)
            .Select(o => new
            {
                o.Id,
                EventTitle = o.Event!.Title,
                EventDate = o.Event.StartDate,
                o.Quantity,
                o.TotalPrice,
                o.Status,
                o.CreatedAt
            })
            .ToListAsync();

        response.StatusCode = 200;
        response.ContentType = "application/json";
        
        await JsonSerializer.SerializeAsync(response.OutputStream, myOrders, new JsonSerializerOptions { WriteIndented = true });
        response.OutputStream.Close();
    }

    // НОВЕ: Скасування замовлення користувачем
    public async Task CancelOrderAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        try
        {
            var parts = request.Url!.AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries);

            int cancelIndex = Array.IndexOf(parts, "cancel");

            if (cancelIndex <= 0 || !int.TryParse(parts[cancelIndex - 1], out int id))
            {
                ConcurrentLogger.Log("❌ Invalid cancel request", ConsoleColor.Red);
                response.StatusCode = 400;
                return;
            }

            await using var db = new AppDbContext();

            var order = await db.TicketOrders
                .Include(o => o.Event)
                .FirstOrDefaultAsync(o => o.Id == id);

            if (order == null)
            {
                response.StatusCode = 404;
                return;
            }

            if (order.Status != Enums.Conditions.Status.Pending)
            {
                ConcurrentLogger.Log($"⚠️ Cannot cancel order #{id} with status {order.Status}", ConsoleColor.Yellow);
                response.StatusCode = 400;
                return;
            }

            order.Status = Enums.Conditions.Status.Cancelled;

            if (order.Event != null)
                order.Event.TotalSeats += order.Quantity;

            await db.SaveChangesAsync();

            ConcurrentLogger.Log($"✅ Order #{id} cancelled", ConsoleColor.Green);

            response.StatusCode = 204;
        }
        catch (Exception ex)
        {
            ConcurrentLogger.Log($"🔥 Cancel error: {ex.Message}", ConsoleColor.Red);
            response.StatusCode = 500;
        }
        finally
        {
            response.Close();
        }
    }
}