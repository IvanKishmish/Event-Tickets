using System.Net;
using System.Text.Json;
using EventTickets.Database;
using EventTickets.Database.Entities;
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
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
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
            Console.WriteLine($"🔥 Помилка створення івенту: {ex.Message}");
            response.StatusCode = 500;
        }
        finally
        {
            response.OutputStream.Close();
        }
    }
}