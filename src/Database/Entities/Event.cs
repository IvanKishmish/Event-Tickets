using System.ComponentModel.DataAnnotations;
using EventTickets.Enums.Categories;

namespace EventTickets.Database.Entities;

public class Event
{
    public int Id { get; set; }
    [MaxLength(75)]
    public string Title { get; set; } = string.Empty;
    [MaxLength(1000)]
    public string Description { get; set; } = string.Empty;
    public DateTime StartDate { get; set; } 
    public decimal Price { get; set; }
    public Category Category { get; set; } 
    public int TotalSeats { get; set; }
}