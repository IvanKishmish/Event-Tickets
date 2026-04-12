using System.ComponentModel.DataAnnotations;
using EventTickets.Enums.Conditions;

namespace EventTickets.Database.Entities;

public class TicketOrder
{
    public int Id { get; set; }
    public int EventId { get; set; }
    public Event? Event { get; set; }
    public int Quantity { get; set; }
    [MaxLength(100)]
    public string ClientEmail { get; set; } = string.Empty;
    public decimal TotalPrice { get; set; }
    public DateTime CreatedAt{get;set;} =  DateTime.Now;
    public Status Status { get; set; } = Status.Pending;
    
    [Timestamp]
    public byte[] RowVersion { get; set; } = [];

    public List<int> SeatIds { get; set; } = new();
}