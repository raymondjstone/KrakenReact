namespace KrakenReact.Server.Models;

public class RebalanceSchedule
{
    public int Id { get; set; }
    /// <summary>Comma-separated "ASSET:PCT" pairs, e.g. "BTC:40,ETH:30,USD:30"</summary>
    public string Targets { get; set; } = "";
    public string CronExpression { get; set; } = "0 9 * * 1";
    public bool Active { get; set; } = true;
    /// <summary>Minimum drift % that triggers action/notification</summary>
    public decimal DriftMinPct { get; set; } = 5m;
    /// <summary>If true, automatically place buy/sell orders to rebalance; otherwise send Pushover alert only</summary>
    public bool AutoExecute { get; set; }
    public string Note { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastRunAt { get; set; }
    public string LastRunResult { get; set; } = "";
}
