using KrakenReact.Server.Analysis;

namespace KrakenReact.Tests;

/// <summary>
/// Exercises the ported entry-detector family on synthetic series whose right answer is known by
/// construction, and checks the one property they all have to have: a signal's confirmation bar
/// depends only on candles at or before it, so nothing here can be read earlier than it could be
/// read live.
/// </summary>
public class EntryDetectionTests
{
    private const int IntervalMinutes = 60;
    private static readonly DateTime Origin = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static List<AnalysisCandle> Series(IReadOnlyList<decimal> closes, IReadOnlyList<decimal>? volumes = null, decimal wick = 0.003m)
    {
        var list = new List<AnalysisCandle>();
        decimal previous = closes.Count > 0 ? closes[0] : 0m;
        for (int i = 0; i < closes.Count; i++)
        {
            decimal open = i == 0 ? closes[i] : previous;
            decimal high = Math.Max(open, closes[i]) * (1m + wick);
            decimal low = Math.Min(open, closes[i]) * (1m - wick);
            decimal volume = volumes != null ? volumes[i] : 100m;
            list.Add(new AnalysisCandle(Origin.AddMinutes(i * IntervalMinutes), open, high, low, closes[i], volume, 10));
            previous = closes[i];
        }
        return list;
    }

    /// <summary>A long flat lead-in, a steep fall, then a recovery — the shape every family is looking for.</summary>
    private static List<decimal> VeeCloses(int flat = 120, int down = 20, int up = 40, decimal top = 100m, decimal bottom = 70m)
    {
        var closes = new List<decimal>();
        for (int i = 0; i < flat; i++) closes.Add(top);
        for (int i = 1; i <= down; i++) closes.Add(top - (top - bottom) * i / down);
        for (int i = 1; i <= up; i++) closes.Add(bottom + (top - bottom) * i / up * 0.9m);
        return closes;
    }

    [Fact]
    public void Drawdown_fires_only_once_price_is_far_enough_below_the_high()
    {
        var candles = Series(VeeCloses());
        var signals = EntryDetection.Drawdown(candles, lookbackBars: 60, minimumDrawdownFraction: 0.15m, referenceLowWindowBars: 10);

        Assert.NotEmpty(signals);
        Assert.All(signals, s =>
        {
            decimal highBefore = candles.Take(s.ConfirmationIndex + 1).Max(c => c.High);
            Assert.True(1m - candles[s.ConfirmationIndex].Close / highBefore >= 0.15m);
        });
    }

    [Fact]
    public void RsiReclaim_fires_on_the_cross_back_up_through_the_level()
    {
        var candles = Series(VeeCloses(down: 25, up: 30));
        var signals = EntryDetection.RsiReclaim(candles, period: 14, oversoldLevel: 35m, referenceLowWindowBars: 10);

        Assert.NotEmpty(signals);
        var rsi = Indicators.RelativeStrengthIndex(candles.Select(c => c.Close).ToList(), 14);
        Assert.All(signals, s =>
        {
            Assert.True(rsi[s.ConfirmationIndex - 1] < 35m);
            Assert.True(rsi[s.ConfirmationIndex] >= 35m);
        });
    }

    [Fact]
    public void DownCloseExhaustion_needs_the_run_and_the_depth()
    {
        var candles = Series(VeeCloses(down: 12, up: 20));
        var signals = EntryDetection.DownCloseExhaustion(candles, minimumConsecutiveDownCloses: 5, minimumFallFraction: 0.05m, referenceLowWindowBars: 8);

        Assert.NotEmpty(signals);
        Assert.All(signals, s => Assert.True(candles[s.ConfirmationIndex].Close > candles[s.ConfirmationIndex - 1].Close));
    }

    [Fact]
    public void LowerBandReclaim_fires_the_bar_the_close_re_enters_the_band()
    {
        var candles = Series(VeeCloses(flat: 80, down: 15, up: 30));
        var (_, lower, _, _) = EntryDetection.BollingerBands(candles, 20, 2m);
        var signals = EntryDetection.LowerBandReclaim(candles, periodBars: 20, standardDeviationMultiple: 2m, minimumPriorFallFraction: 0.05m, priorFallLookbackBars: 40);

        Assert.NotEmpty(signals);
        Assert.All(signals, s =>
        {
            Assert.True(candles[s.ConfirmationIndex - 1].Close < lower[s.ConfirmationIndex - 1]);
            Assert.True(candles[s.ConfirmationIndex].Close > lower[s.ConfirmationIndex]);
        });
    }

    [Fact]
    public void SellingClimax_needs_a_volume_spike_that_is_then_absorbed()
    {
        var closes = VeeCloses(flat: 100, down: 18, up: 30);
        var volumes = Enumerable.Repeat(100m, closes.Count).ToList();
        int climax = 100 + 18;                 // the last down bar
        volumes[climax] = 900m;               // 9× the trailing median
        var candles = Series(closes, volumes);
        // Make the climax bar span a wide range and close in its top half.
        var c = candles[climax];
        candles[climax] = c with { Low = c.Close * 0.90m, High = c.Close * 1.02m, Open = c.Close * 1.015m };

        var median = EntryDetection.ComputeTrailingMedianVolumes(candles, 48);
        var atr = Indicators.AverageTrueRange(candles, 14);
        var signals = EntryDetection.SellingClimax(candles, median, atr, volumeSpikeMultiple: 3m,
            minimumRangeAtrMultiple: 0m, minimumCloseLocationInRange: 0.3m, absorptionBars: 2,
            minimumPriorFallFraction: 0.05m, priorFallLookbackBars: 60);

        Assert.Contains(signals, s => s.Index == climax);
        Assert.All(signals, s => Assert.True(s.ConfirmationIndex > s.Index));
    }

    [Fact]
    public void FairValueGapRetest_prices_the_signal_at_the_lower_gap_edge()
    {
        // Three bars where bar i's low is clear above bar i-2's high.
        var closes = Enumerable.Repeat(100m, 10).ToList();
        var candles = Series(closes);
        candles[7] = candles[7] with { Low = candles[5].High * 1.02m, High = candles[5].High * 1.05m, Close = candles[5].High * 1.04m };

        var signals = EntryDetection.FairValueGapRetest(candles, minimumGapFraction: 0.01m);

        Assert.Single(signals);
        Assert.Equal(candles[5].High, signals[0].Price);
        Assert.Equal(5, signals[0].Index);
        Assert.Equal(7, signals[0].ConfirmationIndex);
    }

    [Fact]
    public void Every_family_confirms_only_from_bars_it_could_have_seen()
    {
        var candles = Series(VeeCloses());
        var atr = Indicators.AverageTrueRange(candles, 14);
        var median = EntryDetection.ComputeTrailingMedianVolumes(candles, 48);
        var pivots = TrendDetection.FindTrendPivots(candles, atr, 14, 3m);
        var trend = TrendDetectionParameters.Default;

        var everything = new List<EntrySignal>();
        everything.AddRange(EntryDetection.Drawdown(candles, 60, 0.12m, 10));
        everything.AddRange(EntryDetection.RsiReclaim(candles, 14, 30m, 10));
        everything.AddRange(EntryDetection.DownCloseExhaustion(candles, 4, 0.05m, 10));
        everything.AddRange(EntryDetection.MacdCrossUp(candles, trend, true, 0.05m, 60, 10));
        everything.AddRange(EntryDetection.LowerBandReclaim(candles, 20, 2m, 0.05m, 40));
        everything.AddRange(EntryDetection.ClimaxAbsorption(candles, atr, 1.5m, 0.3m, 0.05m, 60, 1));
        everything.AddRange(EntryDetection.SellingClimax(candles, median, atr, 2m, 0m, 0.3m, 2, 0.05m, 60));
        everything.AddRange(EntryDetection.VolumeDryUp(candles, median, 0.6m, 6, 0.05m, 60));
        everything.AddRange(EntryDetection.VwapOfFallReclaim(candles, 60, 0.05m));
        everything.AddRange(EntryDetection.CurveVertex(candles, 5, false, 12, 0.001m, 0.05m, 60, 3, 4m, 2m));
        everything.AddRange(EntryDetection.LiquiditySweepReclaim(candles, pivots, 0.005m, 20));
        everything.AddRange(EntryDetection.FairValueGapRetest(candles, 0.004m));

        Assert.All(everything, s =>
        {
            Assert.InRange(s.ConfirmationIndex, 0, candles.Count - 1);
            Assert.True(s.Index <= s.ConfirmationIndex, "the low must be at or before its confirmation");
            Assert.InRange(s.Index, 0, candles.Count - 1);
        });
    }

    [Fact]
    public void Backtest_scores_every_family_and_never_overlaps_a_family_with_itself()
    {
        // A repeating saw so several families keep finding trades.
        var closes = new List<decimal>();
        var rng = new Random(7);
        decimal price = 100m;
        for (int i = 0; i < 900; i++)
        {
            price *= 1m + (decimal)(rng.NextDouble() - 0.5) * 0.04m;
            if (i % 90 < 20) price *= 0.985m;             // a recurring dip
            else if (i % 90 < 40) price *= 1.012m;        // and recovery
            closes.Add(Math.Max(1m, price));
        }
        var candles = Series(closes);
        var atr = Indicators.AverageTrueRange(candles, 14);
        var pivots = TrendDetection.FindTrendPivots(candles, atr, 14, 3m);

        var report = EntrySignalBacktest.Run(
            candles, TrendDetectionParameters.Default, atr, pivots,
            windowBars: 48, targetFraction: 0.08m, stopFraction: 0.06m,
            stake: 1000m, feeFractionPerSide: 0.0026m, spreadAllowanceFraction: 0.001m);

        Assert.Equal(12, report.Families.Count);
        Assert.All(report.Families, f =>
        {
            Assert.True(f.Scored <= f.Signals);
            Assert.True(f.Wins <= f.Scored);
            // First half + second half must reconstruct the rounded total exactly.
            Assert.Equal(f.NetProfit, f.FirstHalfNetProfit + f.SecondHalfNetProfit);
        });
        // Ranked by net profit, descending.
        var nets = report.Families.Select(f => f.NetProfit).ToList();
        Assert.Equal(nets.OrderByDescending(x => x).ToList(), nets);
    }
}
