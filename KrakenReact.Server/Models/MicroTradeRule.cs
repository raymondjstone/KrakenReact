namespace KrakenReact.Server.Models;

public class MicroTradeRule
{
    public int Id { get; set; }
    public string Symbol { get; set; } = "";
    /// <summary>Trigger a buy when the 24h change is at or below -DropPct (e.g. 5 = trigger at -5%)</summary>
    public decimal DropPct { get; set; }
    /// <summary>% above the buy fill price at which the automatic sell is placed</summary>
    public decimal RisePct { get; set; }
    /// <summary>Quote-currency amount to spend per buy order</summary>
    public decimal BuyOrderTotal { get; set; }
    /// <summary>Maximum number of buy orders this rule may place within WindowHours</summary>
    public int MaxOrdersPerWindow { get; set; } = 2;
    /// <summary>Rolling window (hours) the MaxOrdersPerWindow limit applies over</summary>
    public int WindowHours { get; set; } = 2;
    /// <summary>Minimum hours that must pass since the last order on this pair (any rule) before another buy can be placed</summary>
    public int CooldownHours { get; set; } = 1;
    public bool Active { get; set; } = true;
    /// <summary>When true, this rule simulates orders and notifies instead of placing real orders</summary>
    public bool DryRun { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastCheckedAt { get; set; }
    public string LastResult { get; set; } = "";
}
