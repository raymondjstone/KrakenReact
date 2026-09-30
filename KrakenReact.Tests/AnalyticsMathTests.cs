using KrakenReact.Server.Models;
using KrakenReact.Server.Services;

namespace KrakenReact.Tests;

public class CorrelationMathTests
{
    private static List<DerivedKline> Series(DateTime start, IEnumerable<decimal> closes, IEnumerable<int>? skipDays = null)
    {
        var skip = (skipDays ?? []).ToHashSet();
        var list = new List<DerivedKline>();
        var day = 0;
        foreach (var c in closes)
        {
            while (skip.Contains(day)) day++;
            list.Add(new DerivedKline { Interval = "OneDay", OpenTime = start.AddDays(day), Close = c });
            day++;
        }
        return list;
    }

    private static readonly DateTime D0 = new(2025, 1, 1);
    private static readonly decimal[] Wave = [100, 105, 102, 108, 104, 110, 107, 112, 109, 115, 111, 118];

    [Fact]
    public void IdenticalSeries_CorrelatePerfectly()
    {
        var a = CorrelationMath.DailyReturns(Series(D0, Wave));
        Assert.Equal(1.0, CorrelationMath.Pearson(a, a));
    }

    [Fact]
    public void ADoubledSeries_StillCorrelatesPerfectly_ButNotIfDaysAreShifted()
    {
        var same = CorrelationMath.DailyReturns(Series(D0, Wave));
        var scaled = CorrelationMath.DailyReturns(Series(D0, Wave.Select(c => c * 2)));
        Assert.Equal(1.0, CorrelationMath.Pearson(same, scaled));
    }

    [Fact]
    public void AMissingDay_DoesNotShiftOneSeriesAgainstTheOther()
    {
        // Same prices, but b has no candle on day 4. Aligned by date, the days they share still match exactly; taking the last
        // n of each list would have paired different days and given a wrong figure.
        var a = CorrelationMath.DailyReturns(Series(D0, Wave));
        var gapped = Series(D0, Wave).Where(k => k.OpenTime != D0.AddDays(4)).ToList();

        Assert.Equal(1.0, CorrelationMath.Pearson(a, CorrelationMath.DailyReturns(gapped)));
    }

    [Fact]
    public void AReturnAcrossAGap_IsNotRecorded()
    {
        var gapped = Series(D0, Wave).Where(k => k.OpenTime != D0.AddDays(4)).ToList();
        var r = CorrelationMath.DailyReturns(gapped);

        Assert.DoesNotContain(D0.AddDays(4), r.Keys);
        Assert.DoesNotContain(D0.AddDays(5), r.Keys);   // day 5's change is measured from the missing day 4
    }

    [Fact]
    public void TooFewSharedDays_GivesZero()
    {
        var a = CorrelationMath.DailyReturns(Series(D0, Wave.Take(4)));
        var b = CorrelationMath.DailyReturns(Series(D0.AddDays(30), Wave));
        Assert.Equal(0, CorrelationMath.Pearson(a, b));
    }

    [Fact]
    public void AFlatSeries_GivesZero_NotNaN()
    {
        var flat = CorrelationMath.DailyReturns(Series(D0, Enumerable.Repeat(100m, 12)));
        var wavy = CorrelationMath.DailyReturns(Series(D0, Wave));
        Assert.Equal(0, CorrelationMath.Pearson(flat, wavy));
    }
}

public class PortfolioMetricsCalculationTests
{
    private static List<(DateTime, double)> Daily(int days, Func<int, double> value) =>
        Enumerable.Range(0, days).Select(i => (new DateTime(2025, 1, 1).AddDays(i), value(i))).ToList();

    [Fact]
    public void ShortHistory_DoesNotReportAnAnnualisedReturn()
    {
        var m = PortfolioMetrics.Compute(Daily(14, i => 1000 + i * 4));   // about +5% in two weeks
        Assert.Null(m.AnnualReturnPct);
        Assert.Equal(14, m.SampleDays);
    }

    [Fact]
    public void ALongHistory_AnnualisesOverTheRealSpan()
    {
        // Doubles over exactly 365 days
        var m = PortfolioMetrics.Compute(Daily(366, i => 1000 * Math.Pow(2, i / 365.0)));
        Assert.NotNull(m.AnnualReturnPct);
        Assert.InRange(m.AnnualReturnPct!.Value, 99.0, 101.0);
        Assert.Equal(365, m.SpanDays);
    }

    [Fact]
    public void GapsInTheSnapshots_DoNotInflateTheAnnualisedReturn()
    {
        // Only every 10th day was captured: 40 snapshots covering 390 days. Counting snapshots as days would annualise over 40.
        var snaps = Enumerable.Range(0, 40).Select(i => (new DateTime(2025, 1, 1).AddDays(i * 10), 1000 * Math.Pow(2, i * 10 / 365.0))).ToList();
        var m = PortfolioMetrics.Compute(snaps);

        Assert.InRange(m.AnnualReturnPct!.Value, 90.0, 110.0);   // about doubling per year, not a compounded 40-day figure
    }

    [Fact]
    public void MaxDrawdown_IsPeakToTrough()
    {
        var values = new double[] { 1000, 1100, 1200, 1100, 900, 800, 700, 600 };
        var m = PortfolioMetrics.Compute(Daily(values.Length, i => values[i]));
        Assert.Equal(50.0, m.MaxDrawdownPct, 1);
    }

    [Fact]
    public void Sharpe_IgnoresReturnsAcrossGaps()
    {
        // A steady climb with one missing week: the jump across the gap must not count as a single huge day
        var snaps = Daily(30, i => 1000 + i * 10).Where((_, i) => i < 10 || i > 16).ToList();
        var m = PortfolioMetrics.Compute(snaps);
        Assert.True(m.Sharpe > 0);
    }

    [Fact]
    public void Unsorted_InputIsHandled()
    {
        var snaps = Daily(30, i => 1000 + i * 10);
        snaps.Reverse();
        Assert.Equal(PortfolioMetrics.Compute(Daily(30, i => 1000 + i * 10)).MaxDrawdownPct, PortfolioMetrics.Compute(snaps).MaxDrawdownPct);
    }
}
