namespace KrakenReact.Server.Models;

public class BracketOrder
{
    public int Id { get; set; }
    public string KrakenOrderId { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Side { get; set; } = "Buy";
    public decimal Quantity { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal StopPrice { get; set; }
    public decimal TakeProfitPrice { get; set; }
    public string Status { get; set; } = "Watching"; // Watching|Active|TookProfit|Stopped|Cancelled
    public string? StopOrderId { get; set; }
    public string? TakeProfitOrderId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ActivatedAt { get; set; }
    public string Note { get; set; } = "";
}
