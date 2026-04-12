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
        if (request.HttpMethod != "POST")
        {
            response.StatusCode = 405;
            return;
        }

        if (request.ContentType == null || !request.ContentType.Contains("application/json"))
        {
            response.StatusCode = 400;
            return;
        }

        if (request.ContentLength64 == 0)
        {
            response.StatusCode = 400;
            return;
        }
        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            var order = await JsonSerializer.DeserializeAsync<TicketOrder>(request.InputStream, options);

            if (order == null)
            {
                response.StatusCode = 400;
                return;
            }

            if (order.EventId <= 0 || order.SeatIds.Count == 0)
            {
                response.StatusCode = 400;
                return;
            }

            if (string.IsNullOrWhiteSpace(order.ClientEmail) || !IsValidEmail(order.ClientEmail))
            {
                response.StatusCode = 400;
                return;
            }

            await using var db = new AppDbContext();

            var eventObj = await db.Events
                .Include(e => e.Seats)
                .FirstOrDefaultAsync(e => e.Id == order.EventId);

            if (eventObj == null)
            {
                response.StatusCode = 400;
                return;
            }

            // Тепер selectedSeats можна взяти прямо з eventObj, не роблячи новий await db.Seats...
            var selectedSeats = eventObj.Seats
                .Where(s => order.SeatIds.Contains(s.Id))
                .ToList();
            
            if (selectedSeats.Count != order.SeatIds.Count || selectedSeats.Any(s => !s.IsFree))
            {
                response.StatusCode = 409; // Conflict: місця вже зайняті або не існують
                return;
            }
            
            order.Quantity = selectedSeats.Count;            
            order.TotalPrice = eventObj.Price * order.Quantity;
            order.CreatedAt = DateTime.UtcNow;
            order.Status = Enums.Conditions.Status.Pending;
            order.Event = eventObj;

            foreach (var seat in selectedSeats)
            {
                seat.IsFree = false; // Міняємо статус кожного вибраного місця
            }
            
            eventObj.TotalSeats -= order.Quantity;

            db.TicketOrders.Add(order);
            await db.SaveChangesAsync();

            try
            {
                string subject = $"🎫 Квиток: {eventObj.Title}";
                string seatNumbers = string.Join(", ", selectedSeats.Select(s => s.Number));
                string htmlBody = $@"
                    <div style='font-family: sans-serif; border: 1px solid #ddd; border-radius: 10px; padding: 20px; max-width: 500px; margin: auto;'>
                        <h2 style='text-align:center;'>Дякуємо за замовлення!</h2>
                        <p><b>Подія:</b> {eventObj.Title}</p>
                        <p><b>Дата:</b> {eventObj.StartDate:dd.MM.yyyy HH:mm}</p>
                        <p><b>Місця:</b> {seatNumbers}</p>
                        <p><b>Кількість квитків:</b> {order.Quantity}</p>
                        <p><b>Сума до сплати:</b> {order.TotalPrice} грн</p>
                        <p><b>Статус:</b> Очікує підтвердження</p>
                        <p><b>Номер замовлення:</b> #{order.Id}</p>
                    </div>";

                bool sent = await mailSender.SendMailAsync(subject, htmlBody, true, [order.ClientEmail]);
                if (!sent)
                    ConcurrentLogger.Log($"[MAIL ERROR] Не вдалося надіслати лист на {order.ClientEmail} для замовлення #{order.Id}", ConsoleColor.Red);
            }
            catch (Exception ex)
            {
                ConcurrentLogger.Log($"[MAIL EXCEPTION] {ex.Message}", ConsoleColor.Red);
            }

            await telegramNotifier.NotifyNewOrderAsync(order, eventObj);

            response.StatusCode = 201;
            response.ContentType = "application/json";

            var result = new
            {
                order.Id,
                order.EventId,
                order.Quantity,
                Seats = order.SeatIds,
                order.TotalPrice,
                order.CreatedAt,
                Status = order.Status.ToString(),
                order.ClientEmail
            };

            await JsonSerializer.SerializeAsync(response.OutputStream, result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException je)
        {
            ConcurrentLogger.Log($"[JSON Error]: {je.Message}", ConsoleColor.Red);
            response.StatusCode = 400;
        }
        catch (Exception ex)
        {
            ConcurrentLogger.Log($"🔥 CreateOrder error: {ex.Message}", ConsoleColor.Red);
            response.StatusCode = 500;
        }
        finally
        {
            response.OutputStream.Close();
        }
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

    public async Task GetOrderStatusAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        int id = GetIdFromRequest(request, response);
        if (id == -1) return;

        await using var db = new AppDbContext();
        var order = await db.TicketOrders.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id);

        if (order == null)
        {
            response.StatusCode = 404;
            response.Close();
            return;
        }

        var result = new
        {
            id = order.Id,
            status = (int)order.Status,
            statusText = order.Status.ToString()
        };

        response.StatusCode = 200;
        response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(response.OutputStream, result, new JsonSerializerOptions { WriteIndented = true });
        response.OutputStream.Close();
    }

    public async Task GetOrdersPublicAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        await using var db = new AppDbContext();
        var query = db.TicketOrders.AsNoTracking();

        string? statusStr = request.QueryString["status"];
        if (TryParseOrderStatusFilter(statusStr, out var statusEnum))
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
            Status = (int)o.Status,
            StatusText = o.Status.ToString()
        }).ToListAsync();

        response.StatusCode = 200;
        response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(response.OutputStream, orders, new JsonSerializerOptions { WriteIndented = true });
        response.OutputStream.Close();
    }

    public async Task GetMyOrdersAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        string? email = request.QueryString["email"];

        if (string.IsNullOrWhiteSpace(email))
        {
            response.StatusCode = 400;
            response.Close();
            return;
        }

        string normalizedEmail = email.Trim().ToLowerInvariant();

        await using var db = new AppDbContext();

        var myOrders = await db.TicketOrders
            .AsNoTracking()
            .Include(o => o.Event)
            .Where(o => o.ClientEmail.ToLower() == normalizedEmail)
            .Select(o => new
            {
                o.Id,
                EventTitle = o.Event != null ? o.Event.Title : null,
                EventDate = o.Event != null ? o.Event.StartDate : default(DateTime),
                o.Quantity,
                o.SeatIds,
                o.TotalPrice,
                Status = (int)o.Status,
                StatusText = o.Status.ToString(),
                o.CreatedAt
            })
            .ToListAsync();

        response.StatusCode = 200;
        response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(response.OutputStream, myOrders, new JsonSerializerOptions { WriteIndented = true });
        response.OutputStream.Close();
    }

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
                response.StatusCode = 400;
                return;
            }

            order.Status = Enums.Conditions.Status.Cancelled;

            // Знаходимо місця, які були заброньовані в цьому замовленні
            var seatsToRelease = await db.Seats
                .Where(s => order.SeatIds.Contains(s.Id))
                .ToListAsync();

            foreach (var seat in seatsToRelease)
            {
                seat.IsFree = true; // Звільняємо місця
            }

            if (order.Event != null)
                order.Event.TotalSeats += order.Quantity;

            await db.SaveChangesAsync();

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

    private static bool TryParseOrderStatusFilter(string? raw, out Enums.Conditions.Status status)
    {
        status = default;

        if (string.IsNullOrWhiteSpace(raw))
            return false;

        raw = raw.Trim();

        if (int.TryParse(raw, out int numeric) && Enum.IsDefined(typeof(Enums.Conditions.Status), numeric))
        {
            status = (Enums.Conditions.Status)numeric;
            return true;
        }

        if (Enum.TryParse(raw, true, out status))
            return true;

        if (raw.Equals("accepted", StringComparison.OrdinalIgnoreCase) ||
            raw.Equals("confirmed", StringComparison.OrdinalIgnoreCase))
        {
            status = Enums.Conditions.Status.Confirmed;
            return true;
        }

        if (raw.Equals("pending", StringComparison.OrdinalIgnoreCase))
        {
            status = Enums.Conditions.Status.Pending;
            return true;
        }

        if (raw.Equals("cancelled", StringComparison.OrdinalIgnoreCase) ||
            raw.Equals("canceled", StringComparison.OrdinalIgnoreCase) ||
            raw.Equals("declined", StringComparison.OrdinalIgnoreCase))
        {
            status = Enums.Conditions.Status.Cancelled;
            return true;
        }

        return false;
    }

    private int GetIdFromRequest(HttpListenerRequest request, HttpListenerResponse response)
    {
        var segments = request.Url!.Segments
            .Select(s => s.Trim('/'))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        string idStr = segments.LastOrDefault() ?? string.Empty;

        if (!int.TryParse(idStr, out int id))
        {
            response.StatusCode = 400;
            response.Close();
            ConcurrentLogger.Log("❌ Invalid ID in request!", ConsoleColor.Red);
            return -1;
        }

        return id;
    }
}