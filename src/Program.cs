using System.Net;
using EventTickets.Controllers;
using EventTickets.Database;
using EventTickets.Database.Entities;
using EventTickets.Enums.Categories;
using EventTickets.Logs;
using EventTickets.Services.Implementations;
using EventTickets.Telegram;
using Microsoft.EntityFrameworkCore;

DotNetEnv.Env.Load();

// CONFIG
string token = Environment.GetEnvironmentVariable("TELEGRAM_TOKEN") ?? "";
string email = Environment.GetEnvironmentVariable("GMAIL_USER") ?? "";
string appPassword = Environment.GetEnvironmentVariable("GMAIL_APP_CODE") ?? "";

var envAdminIds = Environment.GetEnvironmentVariable("ADMIN_IDS")
    ?.Split(',')
    .Select(id => long.TryParse(id.Trim(), out var result) ? result : 0)
    .Where(id => id != 0)
    .ToList() ?? new List<long>();

List<long> finalAdminIds;

// DB INIT
await using (var db = new AppDbContext())
{
    await db.Database.MigrateAsync();

    if (!db.Events.Any())
    {
        db.Events.Add(new Event
        {
            Title = "Standup evening",
            Price = 250,
            TotalSeats = 40,
            StartDate = DateTime.UtcNow.AddDays(5),
            Category = Category.Standup
        });
        await db.SaveChangesAsync();
    }

    var dbAdminIds = await db.Admins.Select(a => a.TelegramId).ToListAsync();
    finalAdminIds = dbAdminIds.Union(envAdminIds).ToList();
}

// SERVICES
var mailSender = new GmailSender(email, appPassword);
var bot = new TelegramBot(token, finalAdminIds, mailSender);
var orderController = new OrderController(mailSender, bot);
var eventController = new EventController();

// START
bot.Start();

var listener = new HttpListener();
listener.Prefixes.Add("http://localhost:5000/");
listener.Start();

ConcurrentLogger.Log("🚀 API + Bot started", ConsoleColor.Green);

while (true)
{
    var context = await listener.GetContextAsync();
    var request = context.Request;
    var response = context.Response;

    string path = request.Url?.AbsolutePath.TrimEnd('/').ToLower() ?? "";

    ConcurrentLogger.Log($"[Request] {request.HttpMethod} {path}");

    response.Headers.Add("Access-Control-Allow-Origin", "*");
    response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, PATCH, DELETE, OPTIONS");
    response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

    if (request.HttpMethod == "OPTIONS")
    {
        response.StatusCode = 204;
        response.Close();
        continue;
    }
    
    // =========================
    // SWAGGER
    // =========================
    if (path == "/swagger.json")
    {
        var json = File.ReadAllText("swagger.json");

        response.ContentType = "application/json";
        response.StatusCode = 200;

        var buffer = System.Text.Encoding.UTF8.GetBytes(json);
        await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);

        response.Close();
        continue;
    }

    if (path == "/docs" || path == "/")
    {
        var html = File.ReadAllText("docs.html");

        response.ContentType = "text/html";
        response.StatusCode = 200;

        var buffer = System.Text.Encoding.UTF8.GetBytes(html);
        await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);

        response.Close();
        continue;
    }
    
    try
    {
        // =========================
        // EVENTS
        // =========================

        if (path == "/api/events" && request.HttpMethod == "GET")
        {
            await eventController.GetEventsAsync(request, response);
        }
        else if (path == "/api/events/categories" && request.HttpMethod == "GET")
        {
            await eventController.GetEventsCategoriesAsync(request, response);
        }
        else if (path == "/api/events" && request.HttpMethod == "POST")
        {
            await eventController.CreateEventAsync(request, response);
        }
        else if (path.StartsWith("/api/events/"))
        {
            if (request.HttpMethod == "GET")
                await eventController.GetEventByIdAsync(request, response);

            else if (request.HttpMethod == "PATCH")
                await eventController.PatchEventAsync(request, response);

            else if (request.HttpMethod == "DELETE")
                await eventController.DeleteEventByIdAsync(request, response);

            else
                NotFound(response);
        }

        // =========================
        // ORDERS
        // =========================

        else if (path == "/api/orders" && request.HttpMethod == "POST")
        {
            await orderController.CreateOrderAsync(request, response);
        }
        else if (path == "/api/orders" && request.HttpMethod == "GET")
        {
            await orderController.GetOrdersPublicAsync(request, response);
        }
        else if (path.StartsWith("/api/orders/my") && request.HttpMethod == "GET")
        {
            await orderController.GetMyOrdersAsync(request, response);
        }
        else if (path.StartsWith("/api/orders/") && path.EndsWith("/cancel") && request.HttpMethod == "PATCH")
        {
            await orderController.CancelOrderAsync(request, response);
        }
        else if (path.StartsWith("/api/orders/") && request.HttpMethod == "GET")
        {
            await orderController.GetOrderStatusAsync(request, response);
        }

        // =========================
        // 404
        // =========================

        else
        {
            NotFound(response);
        }
    }
    catch (Exception ex)
    {
        ConcurrentLogger.Log($"🔥 Server error: {ex}", ConsoleColor.Red);
        response.StatusCode = 500;
        response.Close();
    }
}

// helper
void NotFound(HttpListenerResponse response)
{
    response.StatusCode = 404;
    response.Close();
}