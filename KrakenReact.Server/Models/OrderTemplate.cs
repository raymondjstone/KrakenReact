namespace KrakenReact.Server.Models;

public class OrderTemplate
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Side { get; set; } = "Buy";
    public decimal? PriceOffsetPct { get; set; }   // null = use current price
    public decimal? Quantity { get; set; }          // null = manual entry
    public decimal? QtyPct { get; set; }            // % of available balance
    public string Note { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
