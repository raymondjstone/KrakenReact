namespace KrakenReact.Server.Analysis;

/// <summary>One horizontal level to draw on a price chart, with the extent of the zone the market actually turned in.</summary>
/// <param name="Price">Where the line is drawn: the mean of the turns that formed the level.</param>
/// <param name="LowPrice">The lowest turn that landed in the level.</param>
/// <param name="HighPrice">The highest turn that landed in the level.</param>
/// <param name="TouchCount">How many confirmed swing turns have landed in the level.</param>
/// <param name="Side">"Support" when the level is below the last closed price, "Resistance" when above.</param>
/// <param name="IsNearest">True for the closest support and closest resistance, which are labelled on the chart.</param>
public sealed record ChartLevel(decimal Price, decimal LowPrice, decimal HighPrice, int TouchCount, string Side, bool IsNearest);

/// <summary>
/// Chooses which of the levels the support/resistance study has built are worth drawing on a chart:
/// the nearest confirmed (touched at least twice) support and resistance to price, plus every level touched often enough to be major.
/// </summary>
public static class ChartLevels
{
    /// <summary>The touches a level needs to be drawn when it is not one of the two nearest to price.</summary>
    public const int MajorLevelMinimumTouches = 10;

    /// <summary>
    /// The touches a level needs to be chosen as the nearest support or resistance. A single pivot is a
    /// swing point, not yet a level, and the ZigZag also emits one at the arbitrary bar the series
    /// happens to start on, so a lone pivot would otherwise be labelled as support next to real ones.
    /// </summary>
    public const int NearestLevelMinimumTouches = 2;

    /// <summary>
    /// Builds the levels for a chart from a candle series.
    /// <para>
    /// A candle still forming at <paramref name="nowUtc"/> is left out: its high, low and close are
    /// still moving, and a pivot or an average true range built on it would shift the lines every
    /// time the chart refreshed rather than only when a bar closed.
    /// </para>
    /// </summary>
    public static List<ChartLevel> Build(
        IReadOnlyList<AnalysisCandle> candles,
        int intervalMinutes,
        DateTime nowUtc,
        SupportResistanceStudyParameters? parameters = null,
        int majorMinimumTouches = MajorLevelMinimumTouches)
    {
        var result = new List<ChartLevel>();
        if (candles == null || candles.Count == 0) return result;

        var closed = intervalMinutes > 0 && candles[^1].OpenTime.AddMinutes(intervalMinutes) > nowUtc
            ? candles.Take(candles.Count - 1).ToList()
            : candles;
        if (closed.Count == 0) return result;

        var levels = SupportResistance.BuildLevels(closed, parameters ?? SupportResistanceStudyParameters.Default);
        if (levels.Count == 0) return result;

        decimal lastClose = closed[^1].Close;
        var support = levels.Where(l => l.Price < lastClose && l.TouchCount >= NearestLevelMinimumTouches).OrderByDescending(l => l.Price).FirstOrDefault();
        var resistance = levels.Where(l => l.Price > lastClose && l.TouchCount >= NearestLevelMinimumTouches).OrderBy(l => l.Price).FirstOrDefault();

        foreach (var level in levels.OrderBy(l => l.Price))
        {
            bool nearest = ReferenceEquals(level, support) || ReferenceEquals(level, resistance);
            if (!nearest && level.TouchCount < majorMinimumTouches) continue;
            result.Add(new ChartLevel(
                level.Price, level.LowestTouchPrice, level.HighestTouchPrice, level.TouchCount,
                level.Price > lastClose ? "Resistance" : "Support", nearest));
        }
        return result;
    }
}
