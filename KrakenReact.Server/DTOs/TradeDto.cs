namespace KrakenReact.Server.DTOs;

public class TradeDto
{
    public string Id { get; set; } = "";
    public string OrderId { get; set; } = "";
    public string Symbol { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public string Side { get; set; } = "";
    public string Type { get; set; } = "";
    public decimal Price { get; set; }
    public decimal Quantity { get; set; }
    public decimal QuoteQuantity { get; set; }
    public decimal Fee { get; set; }
    public decimal Margin { get; set; }
    public decimal NettTotal { get; set; }
    public string? PositionStatus { get; set; }
    public decimal? ClosedQuantity { get; set; }
    public decimal? ClosedProfitLoss { get; set; }
    public decimal? ClosedAveragePrice { get; set; }
    public decimal? ClosedCost { get; set; }
    public decimal? ClosedFee { get; set; }
    public decimal? ClosedMargin { get; set; }
    public List<LedgerDto> LedgerItems { get; set; } = new();
    public List<TradeDto>? ConstituentTrades { get; set; }
}
