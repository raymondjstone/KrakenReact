namespace KrakenReact.Server.Models;

public class DcaRule
{
    public int Id { get; set; }
    public string Symbol { get; set; } = "";
    public decimal AmountUsd { get; set; }
    public string CronExpression { get; set; } = "0 9 * * 1";
    public bool Active { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastRunAt { get; set; }
    public string LastRunResult { get; set; } = "";
    public bool ConditionalEnabled { get; set; }
    public int ConditionalMaPeriod { get; set; } = 20;
    // Feature: ATR-adjusted position sizing
    public bool AtrSizingEnabled { get; set; }
    public decimal AtrRiskUsd { get; set; } = 50;
    // Feature: Fear & Greed index gate
    public bool FearGreedEnabled { get; set; }
    public int FearGreedMaxIndex { get; set; } = 75;
}
