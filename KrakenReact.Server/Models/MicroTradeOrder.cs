namespace KrakenReact.Server.Models;

public class MicroTradeOrder
{
    public int Id { get; set; }
    public int RuleId { get; set; }
    public string Symbol { get; set; } = "";
    public string? BuyOrderId { get; set; }
    public decimal BuyPrice { get; set; }
    public decimal Quantity { get; set; }
    public string? SellOrderId { get; set; }
    public decimal SellPrice { get; set; }
    /// <summary>Buying|Selling|Sold|Cancelled|DryRun</summary>
    public string Status { get; set; } = "Buying";
    public bool DryRun { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? BuyFilledAt { get; set; }
    public DateTime? SoldAt { get; set; }
    public string Note { get; set; } = "";
}
