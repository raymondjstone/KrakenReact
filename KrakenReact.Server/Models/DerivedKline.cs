namespace KrakenReact.Server.Models;

public class DerivedKline
{
    public string Key { get; set; }
    public string Asset { get; set; }
    public DateTime OpenTime { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public decimal Volume { get; set; }
    public decimal VolumeWeightedAveragePrice { get; set; }
    public int TradeCount { get; set; }
    public string Interval { get; set; } = "OneMinute";

    public DerivedKline()
    {
        Asset = string.Empty;
        Key = $"{Asset}_{OpenTime.Ticks}";
    }

    public DerivedKline(Kraken.Net.Objects.Models.KrakenKline kline, string asset, Kraken.Net.Enums.KlineInterval interval)
    {
        Interval = interval.ToString();
        Asset = asset;
        OpenTime = kline.OpenTime;
        Open = kline.OpenPrice;
        High = kline.HighPrice;
        Low = kline.LowPrice;
        Close = kline.ClosePrice;
        Volume = kline.Volume;
        VolumeWeightedAveragePrice = kline.VolumeWeightedAveragePrice;
        TradeCount = kline.TradeCount;
        Key = $"{Asset}{Interval}{OpenTime.Ticks}";
    }
}
