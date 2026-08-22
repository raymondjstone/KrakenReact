using KrakenReact.Server.Models;

namespace KrakenReact.Server.Tax;

/// <summary>
/// Historical USD-to-GBP rates, taken from the stored GBP/USD daily candles.
/// <para>
/// HMRC requires a disposal to be valued in sterling at the time it happened, so a report cannot use
/// today's rate for a trade from two years ago. Where a date has no stored candle — a weekend, a gap
/// in the download — the most recent earlier close is carried forward, and the fact that it was
/// carried is recorded so the report can say how much of it rests on an exact rate.
/// </para>
/// </summary>
public sealed class GbpRateTable
{
    /// <summary>The pair whose candles carry the rate. Its close is USD per GBP.</summary>
    public const string RateSymbol = "GBP/USD";

    private readonly DateTime[] _dates;
    private readonly decimal[] _usdToGbp;

    private GbpRateTable(DateTime[] dates, decimal[] usdToGbp)
    {
        _dates = dates;
        _usdToGbp = usdToGbp;
    }

    public bool IsEmpty => _dates.Length == 0;
    public DateTime? EarliestDate => _dates.Length == 0 ? null : _dates[0];
    public DateTime? LatestDate => _dates.Length == 0 ? null : _dates[^1];
    public int DayCount => _dates.Length;

    /// <summary>Builds the table from GBP/USD daily candles, whose close is USD per GBP.</summary>
    public static GbpRateTable FromKlines(IEnumerable<DerivedKline> gbpUsdDaily)
    {
        var byDate = gbpUsdDaily
            .Where(k => k.Close > 0)
            .GroupBy(k => UkTime.ToUkDate(k.OpenTime))
            .OrderBy(g => g.Key)
            // More than one candle on a UK day (a DST boundary can do it) — the last close wins.
            .Select(g => (Date: g.Key, Close: g.OrderBy(k => k.OpenTime).Last().Close))
            .ToList();

        return new GbpRateTable(
            byDate.Select(x => x.Date).ToArray(),
            byDate.Select(x => 1m / x.Close).ToArray());
    }

    /// <summary>How a rate was arrived at, so a report can be candid about its own precision.</summary>
    public enum RateSource { Exact, CarriedForward, Unavailable }

    /// <summary>
    /// The USD-to-GBP rate for a UK date. Returns the exact day's rate where there is one, otherwise
    /// the most recent earlier one, otherwise nothing at all.
    /// </summary>
    public (decimal Rate, RateSource Source) UsdToGbpOn(DateTime ukDate)
    {
        if (_dates.Length == 0) return (0m, RateSource.Unavailable);

        int index = Array.BinarySearch(_dates, ukDate.Date);
        if (index >= 0) return (_usdToGbp[index], RateSource.Exact);

        // BinarySearch returns the bitwise complement of the next larger element's index, so the
        // element before that is the most recent date at or before the one asked for.
        int previous = ~index - 1;
        return previous < 0
            ? (0m, RateSource.Unavailable)
            : (_usdToGbp[previous], RateSource.CarriedForward);
    }

    /// <summary>
    /// Converts an amount in a quote currency to GBP on a given UK date.
    /// <para>
    /// GBP passes through untouched. USD and the dollar stablecoins convert at the day's rate — the
    /// stablecoins are treated as one dollar each, which is what HMRC would expect but is an
    /// approximation on any day one of them has slipped its peg.
    /// </para>
    /// </summary>
    public (decimal Gbp, RateSource Source) ToGbp(decimal amount, string quoteAsset, DateTime ukDate)
    {
        if (amount == 0m) return (0m, RateSource.Exact);

        if (string.Equals(quoteAsset, "GBP", StringComparison.OrdinalIgnoreCase))
            return (amount, RateSource.Exact);

        if (IsDollarLike(quoteAsset))
        {
            var (rate, source) = UsdToGbpOn(ukDate);
            return source == RateSource.Unavailable ? (0m, source) : (amount * rate, source);
        }

        return (0m, RateSource.Unavailable);
    }

    /// <summary>Whether a quote currency is a dollar or a dollar stablecoin worth about one.</summary>
    public static bool IsDollarLike(string asset) =>
        asset.Equals("USD", StringComparison.OrdinalIgnoreCase) ||
        asset.Equals("USDT", StringComparison.OrdinalIgnoreCase) ||
        asset.Equals("USDC", StringComparison.OrdinalIgnoreCase) ||
        asset.Equals("USDQ", StringComparison.OrdinalIgnoreCase) ||
        asset.Equals("DAI", StringComparison.OrdinalIgnoreCase) ||
        asset.Equals("PYUSD", StringComparison.OrdinalIgnoreCase);
}
