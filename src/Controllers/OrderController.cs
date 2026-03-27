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
            // Console.WriteLine($"[JSON Error]: {je.Message}");
            ConcurrentLogger.Log($"[JSON Error]: {je.Message}", ConsoleColor.Red);
        }
        catch (Exception ex)
        {
            // Console.WriteLine($"[Request Error]: {ex.Message}");
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
            // Можна відправити JSON з повідомленням "Немає місць"
            response.Close();
            return;
        }
        
        order.TotalPrice = eventObj.Price * order.Quantity; // Рахуємо самі!
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

        // Відправляємо лист (передаємо список отримувачів, де тільки пошта клієнта)
        bool sent = await mailSender.SendMailAsync(subject, htmlBody, true, [order.ClientEmail]);
        
        if(!sent)
            // Console.WriteLine($"[MAIL ERROR] Не вдалося надіслати лист на {order.ClientEmail} для замовлення #{order.Id}");
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
        string idStr = request.Url!.Segments.Last().Trim('/');
        
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

        var result = new
        {
            status = order.Status
        };
        
        response.StatusCode = 200;
        response.ContentType = "application/json";

        await JsonSerializer.SerializeAsync(response.OutputStream, result);

        response.OutputStream.Close();
    }

    public async Task GetOrdersPublicAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        await using var db = new AppDbContext();

        var orders = await db.TicketOrders.Select(o => new
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
}