using System.Net;
using EventTickets.Controllers;
using EventTickets.Database;
using EventTickets.Database.Entities;
using EventTickets.Enums.Categories;
using EventTickets.Logs;
using EventTickets.Services.Implementations;
using EventTickets.Telegram;

// Bondarenko text
string token = Environment.GetEnvironmentVariable("TELEGRAM_TOKEN")!;
long adminId = long.Parse(Environment.GetEnvironmentVariable("ADMIN_ID")!);
string email = Environment.GetEnvironmentVariable("GMAIL_USER")!;
string appPassword = Environment.GetEnvironmentVariable("GMAIL_APP_CODE")!;

await using (var db = new AppDbContext())
{
    db.Database.EnsureCreated();
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
}

var mailSender = new GmailSender(email, appPassword);
var bot = new TelegramBot(token, adminId, mailSender);
var orderController = new OrderController(mailSender, bot);
var eventController = new EventController();

bot.Start();

var listener = new HttpListener();
listener.Prefixes.Add("http://localhost:5000/");
listener.Start();
// Console.WriteLine("🚀 Сервер запущено на http://localhost:5000/");
ConcurrentLogger.Log("🚀 Сервер запущено на http://localhost:5000/",  ConsoleColor.Green);

while (true)
{
    var context = await listener.GetContextAsync();
    var request = context.Request;
    var response = context.Response;
    var path = request.Url?.AbsolutePath.TrimEnd('/').ToLower();
    
    // Console.WriteLine($"[DEBUG] RAW PATH: '{request.Url?.AbsolutePath}'");
    // Console.WriteLine($"[DEBUG] NORMALIZED PATH: '{path}'");
    // Console.WriteLine($"[DEBUG] METHOD: {request.HttpMethod}");
    
    ConcurrentLogger.Log($"[DEBUG] RAW PATH: '{request.Url?.AbsolutePath}'\n[DEBUG] NORMALIZED PATH: '{{path}}'\n[DEBUG] METHOD: {{request.HttpMethod}}");
    
    response.Headers.Add("Access-Control-Allow-Origin", "*");
    response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
    response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
    
    if (request.HttpMethod == "OPTIONS")
    {
        response.StatusCode = 204;
        response.Close();
        continue;
    }

    try 
    {
        // Console.WriteLine($"[Request]: {request.HttpMethod} {path}");
        ConcurrentLogger.Log($"[Request]: {request.HttpMethod} {path}");

        if (path == "/api/events" && request.HttpMethod == "GET")
        {
            await eventController.GetEventsAsync(request, response);
        }
        else if (path == "/api/events" && request.HttpMethod == "POST")
        {
            await eventController.CreateEventAsync(request, response);
        }
        else if (path == "/api/orders/public" && request.HttpMethod == "GET")
        {
            await orderController.GetOrdersPublicAsync(request, response);
        }
        else if (path == "/api/orders" && request.HttpMethod == "POST")
        {
            await orderController.CreateOrderAsync(request, response);
        }
        else if (path!.StartsWith("/api/orders/") && request.HttpMethod == "GET")
        {
            await orderController.GetOrderStatusAsync(request, response);
        }
        else
        {
            // Console.WriteLine($"⚠️ Шлях не знайдено: {path}");
            ConcurrentLogger.Log($"⚠️ Шлях не знайдено: {path}",  ConsoleColor.Red);
            response.StatusCode = 404;
            response.Close();
        }
    }
    catch (Exception ex)
    {
        // Console.WriteLine($"🔥 Помилка сервера: {ex.Message}");
        ConcurrentLogger.Log($"🔥 Помилка сервера: {ex.Message}", ConsoleColor.Red);
        response.StatusCode = 500;
        response.Close();
    }
}