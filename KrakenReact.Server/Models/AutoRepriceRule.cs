namespace KrakenReact.Server.Models;

public class AutoRepriceRule
{
    public int Id { get; set; }
    public string Symbol { get; set; } = "";
    public decimal MaxDeviationPct { get; set; } = 2.0m;
    public int MinAgeMinutes { get; set; } = 15;
    // 0 = no max age limit
    public int MaxAgeMinutes { get; set; } = 0;
    // Which sides to reprice
    public bool RepriceBuys { get; set; } = true;
    public bool RepriceSells { get; set; } = false;
    // 0 = quasi-market (0.1% inside); > 0 = place this % below market for buys, above for sells
    public decimal NewPriceOffsetPct { get; set; } = 0m;
    public bool Active { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string LastResult { get; set; } = "";
    public DateTime? LastRunAt { get; set; }
}
