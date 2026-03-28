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

// 1. Отримуємо конфігурацію з середовища
string token = Environment.GetEnvironmentVariable("TELEGRAM_TOKEN") ?? "";
string email = Environment.GetEnvironmentVariable("GMAIL_USER") ?? "";
string appPassword = Environment.GetEnvironmentVariable("GMAIL_APP_CODE") ?? "";

// Список ID з параметрів запуску (env)
var envAdminIds = Environment.GetEnvironmentVariable("ADMIN_IDS")
    ?.Split(',')
    .Select(id => long.TryParse(id.Trim(), out var result) ? result : 0)
    .Where(id => id != 0)
    .ToList() ?? new List<long>();

List<long> finalAdminIds;

// 2. Робота з базою даних при старті
await using (var db = new AppDbContext())
{
    // Використовуємо MigrateAsync замість EnsureCreated, щоб працювали міграції (таблиця Admins)
    await db.Database.MigrateAsync();
    ConcurrentLogger.Log("📂 Базу даних синхронізовано (Migrations applied).", ConsoleColor.Blue);

    // Додаємо тестову подію, якщо база порожня
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
        ConcurrentLogger.Log("🎭 Тестову подію додано.", ConsoleColor.Magenta);
    }

    // Дістаємо адмінів, яких ти додав вручну в таблицю Admins (через Rider)
    var dbAdminIds = await db.Admins.Select(a => a.TelegramId).ToListAsync();
    
    // Об'єднуємо ID з бази та ID з параметрів запуску (унікальні значення)
    finalAdminIds = dbAdminIds.Union(envAdminIds).ToList();
    
    ConcurrentLogger.Log($"👥 Завантажено адмінів: {finalAdminIds.Count} (з БД: {dbAdminIds.Count}, з ENV: {envAdminIds.Count})", ConsoleColor.Cyan);
}

// 3. Ініціалізація сервісів
var mailSender = new GmailSender(email, appPassword);
var bot = new TelegramBot(token, finalAdminIds, mailSender);
var orderController = new OrderController(mailSender, bot);
var eventController = new EventController();

// 4. Запуск бота
bot.Start();

// 5. Запуск HTTP сервера
var listener = new HttpListener();
listener.Prefixes.Add("http://localhost:5000/");
listener.Start();
ConcurrentLogger.Log("🚀 Сервер запущено на http://localhost:5000/", ConsoleColor.Green);

while (true)
{
    var context = await listener.GetContextAsync();
    var request = context.Request;
    var response = context.Response;
    var path = request.Url?.AbsolutePath.TrimEnd('/').ToLower();
    
    // Логування запиту
    ConcurrentLogger.Log($"[Request]: {request.HttpMethod} {path}");
    
    // Налаштування CORS
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
        else if (path != null && path.StartsWith("/api/orders/"))
        {
            await orderController.GetOrderStatusAsync(request, response);
        }
        else
        {
            ConcurrentLogger.Log($"⚠️ Шлях не знайдено: {path}", ConsoleColor.Red);
            response.StatusCode = 404;
            response.Close();
        }
    }
    catch (Exception ex)
    {
        ConcurrentLogger.Log($"🔥 Помилка сервера: {ex.Message}", ConsoleColor.Red);
        response.StatusCode = 500;
        response.Close();
    }
}