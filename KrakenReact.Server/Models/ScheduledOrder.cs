namespace KrakenReact.Server.Models;

public class ScheduledOrder
{
    public int Id { get; set; }
    public string Symbol { get; set; } = "";
    public string Side { get; set; } = "Buy";   // Buy | Sell
    public decimal Price { get; set; }
    public decimal Quantity { get; set; }
    public DateTime ScheduledAt { get; set; }
    public DateTime? ExecutedAt { get; set; }
    public string Status { get; set; } = "Pending";  // Pending | Executed | Failed | Cancelled
    public string Note { get; set; } = "";
    public string ErrorMessage { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
