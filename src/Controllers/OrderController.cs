using System.Net;
using System.Text.Json;
using EventTickets.Database;
using EventTickets.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace EventTickets.Controllers;

public class OrderController
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
            Console.WriteLine($"[JSON Error]: {je.Message}");
            // ConcurrentLogger.Log($"[JSON Error]: {je.Message}", ConsoleColor.Red);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Request Error]: {ex.Message}");
            // ConcurrentLogger.Log($"[Request Error]: {ex.Message}", ConsoleColor.Red);
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
        db.TicketOrders.Add(order);
        await db.SaveChangesAsync();
        
        response.StatusCode = 200;
        response.ContentType = "application/json";
        
        await JsonSerializer.SerializeAsync(response.OutputStream, order);
        
        response.OutputStream.Close();
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