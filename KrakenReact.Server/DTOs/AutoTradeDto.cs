namespace KrakenReact.Server.DTOs;

public class AutoTradeDto
{
    public string Symbol { get; set; } = "";
    public string Base { get; set; } = "";
    public string CCY { get; set; } = "";
    public string CoinType { get; set; } = "";
    public decimal? ClosePriceMovement { get; set; }
    public decimal? ClosePriceMovementWeek { get; set; }
    public decimal? ClosePriceMovementMonth { get; set; }
    public string Reason { get; set; } = "";
    public int OrderRanking { get; set; }
    public bool OrderWanted { get; set; }
    public bool OrderMade { get; set; }
}
