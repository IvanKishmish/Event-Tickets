using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using EventTickets.Database;
using EventTickets.Database.Entities;
using EventTickets.Logs;
using Microsoft.EntityFrameworkCore;

namespace EventTickets.Controllers;

public class EventController
{
    public async Task GetEventsAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        await using AppDbContext db = new AppDbContext();

        var query = db.Events.AsQueryable();
        
        string? title = request.QueryString["title"];
        string? categoryStr = request.QueryString["category"];
        string? minSeats = request.QueryString["minSeats"];
        string? maxSeats = request.QueryString["maxSeats"];
        string? minPrice = request.QueryString["minPrice"];
        string? maxPrice = request.QueryString["maxPrice"];

        if (!string.IsNullOrWhiteSpace(title))
            query = query.Where(e => e.Title == title);
        
        if(!string.IsNullOrWhiteSpace(categoryStr))
            query = query.Where(e => e.Category.ToString() ==  categoryStr);
        
        if(decimal.TryParse(minSeats, out decimal minSeatsDecimal))
            query = query.Where(e => e.TotalSeats >= minSeatsDecimal);
        
        if(decimal.TryParse(maxSeats, out decimal maxSeatsDecimal))
            query = query.Where(e => e.TotalSeats <= maxSeatsDecimal);
        
        if(decimal.TryParse(minPrice, out decimal minPriceDecimal))
            query = query.Where(e => e.Price >= minPriceDecimal);
        
        if(decimal.TryParse(maxPrice, out decimal maxPriceDecimal))
            query = query.Where(e => e.Price <= maxPriceDecimal);
        
        if(int.TryParse(request.QueryString["id"], out int idParsed))
            query  = query.Where(p => p.Id == idParsed);

        var events = await query.ToListAsync();
        
        response.ContentType = "application/json";
        response.StatusCode = 200;
        
        await JsonSerializer.SerializeAsync(response.OutputStream, events, new JsonSerializerOptions { WriteIndented = true });
        response.OutputStream.Close();
    }

    public async Task CreateEventAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        try
        {
            // 1. Читаємо тіло запиту
            using var reader = new StreamReader(request.InputStream);
            string json = await reader.ReadToEndAsync();

            // 2. Десеріалізуємо JSON у модель Event
            var options = new JsonSerializerOptions 
            { 
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() }
            };
            var newEvent = JsonSerializer.Deserialize<Event>(json, options);

            if (newEvent == null)
            {
                response.StatusCode = 400; // Bad Request
                return;
            }

            // 3. Важливо: PostgreSQL вимагає UTC для DateTime
            if (newEvent.StartDate.Kind != DateTimeKind.Utc)
            {
                newEvent.StartDate = DateTime.SpecifyKind(newEvent.StartDate, DateTimeKind.Utc);
            }

            // 4. Зберігаємо в базу даних
            await using var db = new AppDbContext();
            db.Events.Add(newEvent);
            await db.SaveChangesAsync();

            // 5. Відправляємо відповідь
            response.ContentType = "application/json";
            response.StatusCode = 201; // Created
            await JsonSerializer.SerializeAsync(response.OutputStream, newEvent, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            // Console.WriteLine($"🔥 Помилка створення івенту: {ex.Message}");
            ConcurrentLogger.Log($"🔥 Помилка створення івенту: {ex.Message}", ConsoleColor.Red);
            response.StatusCode = 500;
        }
        finally
        {
            response.OutputStream.Close();
        }
    }

    public async Task GetEventByIdAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        int id = GetIdFromRequest(request, response);
        if (id == -1) return;

        await using var db = new AppDbContext();

        var eventObj = await db.Events.FindAsync(id);

        if (eventObj == null)
        {
            response.StatusCode = 404;
            response.Close();
            return;
        }

        response.StatusCode = 200;
        response.ContentType = "application/json";

        await JsonSerializer.SerializeAsync(response.OutputStream, eventObj, new JsonSerializerOptions { WriteIndented = true });
        response.OutputStream.Close();
    }

    public async Task GetEventsCategoriesAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        await using var db = new AppDbContext();
        
        var categories = await db.Events
            .Select(e => e.Category.ToString())
            .Distinct() //унікальний робимо список
            .ToListAsync();

        if (categories.Count == 0)
        {
            response.StatusCode = 400;
            response.Close();
            return;
        }

        response.StatusCode = 200;
        response.ContentType = "application/json";
        
        await JsonSerializer.SerializeAsync(response.OutputStream, categories, new JsonSerializerOptions { WriteIndented = true });
        response.OutputStream.Close();
    }

    public async Task DeleteEventByIdAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        int id = GetIdFromRequest(request, response);
        if (id == -1) return;

        await using var db = new AppDbContext();

        var eventObj = await db.Events.FindAsync(id);
        if (eventObj == null)
        {
            response.StatusCode = 404;
            response.Close();
            return;
        }

        db.Events.Remove(eventObj);
        await db.SaveChangesAsync();

        response.StatusCode = 204;
        response.Close();
    }

    public async Task PatchEventAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        try
        {
            int id = GetIdFromRequest(request, response);
            if (id == -1) return;

            using var reader = new StreamReader(request.InputStream);
            string json = await reader.ReadToEndAsync();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            await using var db = new AppDbContext();
            var eventObj = await db.Events.FindAsync(id);

            if (eventObj == null)
            {
                response.StatusCode = 404;
                return;
            }

            if (root.TryGetProperty("title", out var title))
                eventObj.Title = title.GetString() ?? eventObj.Title;

            if (root.TryGetProperty("price", out var price))
                eventObj.Price = price.GetDecimal();

            if (root.TryGetProperty("totalSeats", out var seats))
                eventObj.TotalSeats = seats.GetInt32();

            if (root.TryGetProperty("description", out var desc))
                eventObj.Description = desc.GetString() ?? eventObj.Description;

            if (root.TryGetProperty("startDate", out var sd))
            {
                var date = sd.GetDateTime();
                eventObj.StartDate = DateTime.SpecifyKind(date, DateTimeKind.Utc);
            }

            await db.SaveChangesAsync();
            response.StatusCode = 204;
        }
        catch (Exception ex)
        {
            ConcurrentLogger.Log($"🔥 Помилка Patch Event: {ex.Message}", ConsoleColor.Red);
            response.StatusCode = 500;
        }
        finally
        {
            response.Close();
        }
    }

    private int GetIdFromRequest(HttpListenerRequest request, HttpListenerResponse response)
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
            ConcurrentLogger.Log("❌ Invalid ID in request!", ConsoleColor.Red);
            return -1;
        }

        return id;
    }
}