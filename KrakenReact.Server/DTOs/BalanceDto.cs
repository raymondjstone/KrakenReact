namespace KrakenReact.Server.DTOs;

public class BalanceDto
{
    public string Asset { get; set; } = "";
    public decimal Total { get; set; }
    public decimal Locked { get; set; }
    public decimal Available { get; set; }
    public decimal LatestPrice { get; set; }
    public decimal LatestValue { get; set; }
    public decimal LatestValueGbp { get; set; }
    public decimal PortfolioPercentage { get; set; }
    public decimal OrderCoveredQty { get; set; }
    public decimal OrderUncoveredQty { get; set; }
    public decimal OrderCoveredValue { get; set; }
    public decimal OrderUncoveredValue { get; set; }
    public decimal TotalCostBasis { get; set; }
    public decimal TotalFees { get; set; }
    public decimal NetProfitLoss { get; set; }
    public decimal NetProfitLossPercentage { get; set; }
}
