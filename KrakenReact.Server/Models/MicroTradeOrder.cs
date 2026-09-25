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
    /// <summary>Placing|Buying|Selling|Sold|Cancelled|DryRun. "Placing" = row saved BEFORE the buy is sent to Kraken
    /// (so a timed-out/lost response can never leave a real order with no record); resolved by UserRef lookup.</summary>
    public string Status { get; set; } = "Buying";
    public bool DryRun { get; set; }
    /// <summary>Kraken userref tagged on the buy so it can be found again if the placement response is lost (timeout)</summary>
    public long? BuyUserRef { get; set; }
    /// <summary>Kraken userref tagged on the current sell placement attempt (same purpose as BuyUserRef)</summary>
    public long? SellUserRef { get; set; }
    /// <summary>True once the resting sell has been repriced down by the stop-loss check (fires at most once per order)</summary>
    public bool StopLossTriggered { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? BuyFilledAt { get; set; }
    public DateTime? SoldAt { get; set; }
    public string Note { get; set; } = "";
}
