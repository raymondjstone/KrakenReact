namespace KrakenReact.Server.Models;

public class ProfitLadderRule
{
    public int Id { get; set; }
    public string Symbol { get; set; } = "";
    /// <summary>% above cost basis at which to trigger the sell</summary>
    public decimal TriggerPct { get; set; }
    /// <summary>% of available balance to sell (1–100)</summary>
    public decimal SellPct { get; set; }
    public bool Active { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastTriggeredAt { get; set; }
    public string LastResult { get; set; } = "";
    /// <summary>Minimum hours between triggers for the same rule to prevent rapid re-firing</summary>
    public int CooldownHours { get; set; } = 24;
}
