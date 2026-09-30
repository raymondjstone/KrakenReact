namespace KrakenReact.Server.Services;

public sealed record PortfolioMetricsResult(double Sharpe, double MaxDrawdownPct, double? AnnualReturnPct, int SampleDays, int SpanDays);

/// <summary>
/// Risk and return figures from the daily portfolio value snapshots.
/// <para>
/// These are computed from total value alone, so money paid in or taken out shows up as gain or loss - they describe how the
/// account value moved, not the skill of the trading.
/// </para>
/// </summary>
public static class PortfolioMetrics
{
    /// <summary>Crypto trades every day of the year, so a daily figure is scaled by 365 (not the 252 of stock markets).</summary>
    public const double PeriodsPerYear = 365.0;

    /// <summary>Shortest span an annualised return is reported for. Compounding a couple of weeks up to a year turns ordinary
    /// noise into a huge number (5% in 14 days would read as roughly 250% a year).</summary>
    public const int MinimumSpanForAnnualReturnDays = 90;

    public static PortfolioMetricsResult Compute(IReadOnlyList<(DateTime Date, double Value)> snapshots)
    {
        var ordered = snapshots.OrderBy(s => s.Date).ToList();

        // Daily returns, but only between snapshots one day apart: across a gap the change covers several days and would be
        // counted as one day's move, inflating the volatility
        var returns = new List<double>();
        for (var i = 1; i < ordered.Count; i++)
        {
            if ((ordered[i].Date.Date - ordered[i - 1].Date.Date).TotalDays != 1) continue;
            var prev = ordered[i - 1].Value;
            returns.Add(prev > 0 ? (ordered[i].Value - prev) / prev : 0);
        }

        var sharpe = 0.0;
        if (returns.Count > 1)
        {
            var mean = returns.Average();
            var std = Math.Sqrt(returns.Select(r => (r - mean) * (r - mean)).Average());
            sharpe = std > 0 ? Math.Round(mean / std * Math.Sqrt(PeriodsPerYear), 3) : 0;
        }

        double peak = ordered[0].Value, maxDd = 0;
        foreach (var (_, v) in ordered)
        {
            if (v > peak) peak = v;
            if (peak > 0) maxDd = Math.Max(maxDd, (peak - v) / peak);
        }

        // Annualise over the real time between the first and last snapshot, not over how many snapshots there are
        var spanDays = (int)(ordered[^1].Date.Date - ordered[0].Date.Date).TotalDays;
        double? annual = null;
        if (spanDays >= MinimumSpanForAnnualReturnDays && ordered[0].Value > 0)
        {
            var total = (ordered[^1].Value - ordered[0].Value) / ordered[0].Value;
            annual = Math.Round((Math.Pow(1 + total, PeriodsPerYear / spanDays) - 1) * 100, 2);
        }

        return new PortfolioMetricsResult(sharpe, Math.Round(maxDd * 100, 2), annual, ordered.Count, spanDays);
    }
}
