namespace KrakenReact.Server.DTOs;

public class CreateOrderRequest
{
    public string Symbol { get; set; } = "";
    public string Side { get; set; } = "Buy";
    public decimal Price { get; set; }
    public decimal Quantity { get; set; }
    // Optional bracket order fields
    public decimal? BracketStopPct { get; set; }
    public decimal? BracketTakeProfitPct { get; set; }
    public string? BracketNote { get; set; }
    /// <summary>Set by the client after the user has confirmed an order priced well through the market (see OrderPriceGuard).</summary>
    public bool ConfirmPriceDeviation { get; set; }
}
