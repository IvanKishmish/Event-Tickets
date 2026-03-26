namespace EventTickets.Attributes;

[AttributeUsage(AttributeTargets.Class)]
public class TelegramCommandHandlerAttribute(string command) : Attribute
{
    public string Command { get; } = command;
}