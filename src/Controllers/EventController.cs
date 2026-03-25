using System.Net;
using System.Text.Json;
using EventTickets.Database;
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
            query = query.Where(e => e.Price >= maxPriceDecimal);
        
        if(int.TryParse(request.QueryString["id"], out int idParsed))
            query  = query.Where(p => p.Id == idParsed);

        var events = await query.ToListAsync();
        
        response.ContentType = "application/json";
        response.StatusCode = 200;
        
        await JsonSerializer.SerializeAsync(response.OutputStream, events, new JsonSerializerOptions { WriteIndented = true });
        response.OutputStream.Close();
    }
}