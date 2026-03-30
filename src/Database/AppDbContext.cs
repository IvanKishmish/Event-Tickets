using DotNetEnv;
using EventTickets.Database.Entities;
using EventTickets.Logs;
using Microsoft.EntityFrameworkCore;

namespace EventTickets.Database;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    // Порожній конструктор потрібен для деяких інструментів EF Core або спрощених викликів
    public AppDbContext() { }
    
    public DbSet<Event> Events { get; set; }
    public DbSet<TicketOrder> TicketOrders { get; set; }
    public DbSet<Admin> Admins { get; set; }
    
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        Env.Load();
        string? connectionString = Environment.GetEnvironmentVariable("DB_CONNECTION_STRING");
        if (connectionString == null)
        {
            // Console.ForegroundColor = ConsoleColor.Red;
            // Console.WriteLine("No DB_CONNECTION_STRING found");
            // Console.ResetColor();
            ConcurrentLogger.Log("No DB_CONNECTION_STRING found", ConsoleColor.Red);
        }

        optionsBuilder.UseNpgsql(connectionString);
    }
}