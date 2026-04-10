namespace KrakenReact.Server.DTOs;

public class DelistedPairDto
{
    public string Symbol { get; set; } = "";
    public string Status { get; set; } = ""; // "delisted" or "active"
    public DateTime? LastPriceDate { get; set; }
    public decimal? LastPrice { get; set; }
    public bool HasHistoricalData { get; set; }
}
