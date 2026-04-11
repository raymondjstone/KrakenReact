namespace KrakenReact.Server.DTOs;

public class PriceDto
{
    public string Symbol { get; set; } = "";
    public string DisplaySymbol { get; set; } = "";
    public string Base { get; set; } = "";
    public string CCY { get; set; } = "";
    public string CoinType { get; set; } = "";
    public decimal? ClosePrice { get; set; }
    public decimal? OpenPrice { get; set; }
    public decimal? HighPrice { get; set; }
    public decimal? LowPrice { get; set; }
    public decimal? Volume { get; set; }
    public decimal? VolumeWeightedAveragePrice { get; set; }
    public int? TradeCount { get; set; }
    public DateTime? OpenTime { get; set; }
    public string Age { get; set; } = "Unknown";
    public string KrakenNewPricesLoaded { get; set; } = "no";
    public decimal? ClosePriceMovement { get; set; }
    public decimal? ClosePriceMovementWeek { get; set; }
    public decimal? ClosePriceMovementMonth { get; set; }
    public decimal? ClosePriceDifference { get; set; }
    public decimal? ClosePriceDifferenceWeek { get; set; }
    public decimal? ClosePriceDifferenceMonth { get; set; }
    public decimal? AvgPriceDay { get; set; }
    public decimal? AvgPriceWeek { get; set; }
    public decimal? AvgPriceMonth { get; set; }
    public decimal? AvgPriceYear { get; set; }
    public decimal? WeightedPrice { get; set; }
    public decimal? WeightedPricePercentage { get; set; }
    public decimal AverageBuyPrice { get; set; }
    public bool PriceLowerThanBuy { get; set; }
    public decimal? BestBid { get; set; }
    public decimal? BestAsk { get; set; }
}
