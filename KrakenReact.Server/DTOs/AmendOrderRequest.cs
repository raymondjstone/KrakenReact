namespace KrakenReact.Server.DTOs;

public class AmendOrderRequest
{
    public decimal Price { get; set; }
    public decimal Quantity { get; set; }
    /// <summary>Set by the client after the user has confirmed a price well through the market (see OrderPriceGuard).</summary>
    public bool ConfirmPriceDeviation { get; set; }
}
