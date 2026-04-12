namespace EventTickets.Database.Entities;

public class Seat
{
    public int Id { get; set; }
    public int Number { get; set; }
    public bool IsFree { get; set; } = true;
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;
}