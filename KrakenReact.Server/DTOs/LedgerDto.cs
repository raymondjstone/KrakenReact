namespace KrakenReact.Server.DTOs;

public class LedgerDto
{
    public string Id { get; set; } = "";
    public string ReferenceId { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public string Type { get; set; } = "";
    public string? SubType { get; set; }
    public string Asset { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal Fee { get; set; }
    public decimal BalanceAfter { get; set; }
    public decimal FeePercentage { get; set; }
    public string AssetClass { get; set; } = "";
}
