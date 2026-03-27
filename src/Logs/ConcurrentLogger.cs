namespace EventTickets.Logs;

public static class ConcurrentLogger
{
    private static readonly object Lock =  new();

    public static void Log(string message, ConsoleColor color = ConsoleColor.White)
    {
        lock (Lock)
        {
            Console.ForegroundColor = color;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
            Console.ResetColor();
        }
    }
}