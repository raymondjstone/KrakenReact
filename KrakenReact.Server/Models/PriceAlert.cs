namespace KrakenReact.Server.Models;

public class PriceAlert
{
    public int Id { get; set; }
    public string Symbol { get; set; } = "";
    public decimal TargetPrice { get; set; }
    public string Direction { get; set; } = "above"; // above | below
    public bool Active { get; set; } = true;
    public DateTime? TriggeredAt { get; set; }
    public string Note { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
