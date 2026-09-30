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

    /// <summary>The stored key for a candle. Everything that persists candles uses this, so the same candle always has the same key.</summary>
    public static string MakeKey(string asset, string interval, DateTime openTime) => $"{asset}{interval}{openTime.Ticks}";

    public DerivedKline()
    {
        Asset = string.Empty;
        Key = string.Empty;   // set from MakeKey when the candle is saved (the fields are not filled in yet at this point)
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
        Key = MakeKey(Asset, Interval, OpenTime);
    }
}
