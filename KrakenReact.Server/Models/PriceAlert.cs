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

    // Optional: place a limit order automatically when this alert triggers
    public bool AutoOrderEnabled { get; set; }
    public string AutoOrderSide { get; set; } = "Buy";   // Buy | Sell
    public decimal AutoOrderQty { get; set; }
    public decimal AutoOrderOffsetPct { get; set; }       // % offset from trigger price (e.g. -1 = 1% below)
}
