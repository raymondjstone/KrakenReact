using KrakenReact.Server.Models;

namespace KrakenReact.Server.Analysis;

/// <summary>
/// A single OHLCV bar in the shape the analysis routines want: immutable, allocation-cheap
/// and free of the persistence concerns that <see cref="DerivedKline"/> carries.
/// </summary>
public sealed record AnalysisCandle(
    DateTime OpenTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    int TradeCount)
{
    public bool IsUp => Close >= Open;

    /// <summary>Projects stored klines onto analysis candles, in chronological order.</summary>
    public static List<AnalysisCandle> FromKlines(IEnumerable<DerivedKline> klines) =>
        klines
            .OrderBy(k => k.OpenTime)
            .Select(k => new AnalysisCandle(k.OpenTime, k.Open, k.High, k.Low, k.Close, k.Volume, k.TradeCount))
            .ToList();
}
