using KrakenReact.Server.Analysis;

namespace KrakenReact.Tests;

/// <summary>
/// A backtester that is wrong in the optimistic direction is worse than none, so these cases are
/// built to catch flattery specifically: gaps that should fill at the open, targets reached without
/// the entry ever filling, and trades scored on a window the data cannot cover.
/// </summary>
public class TradeSimulationTests
{
    private static readonly DateTime Origin = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const int IntervalMinutes = 60;

    /// <summary>Builds candles from explicit OHLC tuples so each bar's shape is exactly as intended.</summary>
    private static List<AnalysisCandle> Bars(params (decimal Open, decimal High, decimal Low, decimal Close)[] bars) =>
        bars.Select((b, i) => new AnalysisCandle(
            Origin.AddMinutes(i * IntervalMinutes), b.Open, b.High, b.Low, b.Close, 100m, 10)).ToList();

    private static StrategyRules Immediate(decimal target = 0.5m, decimal stop = 0.10m) =>
        new() { Entry = EntryStyle.Immediate, TargetFraction = target, StopLossFraction = stop, WindowBars = 10 };

    // ── Exits ───────────────────────────────────────────────────────────────

    [Fact]
    public void Immediate_TakesTheTargetWhenPriceReachesIt()
    {
        // Trigger closes at 100 with a low of 100; reference high 200, so half the fall is 150.
        var candles = Bars(
            (100m, 100m, 100m, 100m),
            (100m, 150m, 100m, 150m));

        var outcome = TradeSimulation.Simulate(candles, 0, 200m, Immediate());
        Assert.Equal(TradeExitReason.Target, outcome.ExitReason);
        Assert.Equal(150m, outcome.ExitPrice);
        Assert.Equal(0.5m, outcome.ReturnFraction);
    }

    [Fact]
    public void Immediate_TakesTheStopWhenPriceFallsToIt()
    {
        var candles = Bars(
            (100m, 100m, 100m, 100m),
            (100m, 100m, 85m, 88m));

        var outcome = TradeSimulation.Simulate(candles, 0, 200m, Immediate());
        Assert.Equal(TradeExitReason.StopLoss, outcome.ExitReason);
        Assert.Equal(90m, outcome.ExitPrice);   // the stop price, not the bar's low
    }

    [Fact]
    public void Immediate_FillsAGapDownAtTheOpenNotTheStop()
    {
        // The bar opens at 70, straight through a stop at 90. A backtest that reports 90 has invented
        // a fill nobody could have had — this is where optimistic backtests do their worst lying.
        var candles = Bars(
            (100m, 100m, 100m, 100m),
            (70m, 72m, 68m, 70m));

        var outcome = TradeSimulation.Simulate(candles, 0, 200m, Immediate());
        Assert.Equal(TradeExitReason.StopLoss, outcome.ExitReason);
        Assert.Equal(70m, outcome.ExitPrice);
        Assert.True(outcome.ReturnFraction < -0.25m, "a gap through the stop must cost more than the stop distance");
    }

    [Fact]
    public void Immediate_FillsAGapUpAtTheOpenNotTheTarget()
    {
        var candles = Bars(
            (100m, 100m, 100m, 100m),
            (170m, 180m, 165m, 175m));

        var outcome = TradeSimulation.Simulate(candles, 0, 200m, Immediate());
        Assert.Equal(TradeExitReason.Target, outcome.ExitReason);
        Assert.Equal(170m, outcome.ExitPrice);   // better than the 150 target, and correctly so
    }

    [Fact]
    public void Immediate_ChecksTheStopBeforeTheTargetOnABarThatSpansBoth()
    {
        // One bar that touches both. Resolving it as a win would be the most flattering possible
        // reading of a bar whose internal order is unknowable; the pessimistic one is the honest one.
        var candles = Bars(
            (100m, 100m, 100m, 100m),
            (100m, 160m, 85m, 120m));

        var outcome = TradeSimulation.Simulate(candles, 0, 200m, Immediate());
        Assert.Equal(TradeExitReason.StopLoss, outcome.ExitReason);
    }

    [Fact]
    public void Immediate_ExitsAtTheCloseWhenTheWindowRunsOut()
    {
        var candles = Bars(
            (100m, 100m, 100m, 100m),
            (100m, 105m, 98m, 102m),
            (102m, 106m, 99m, 104m));

        var rules = Immediate() with { WindowBars = 2 };
        var outcome = TradeSimulation.Simulate(candles, 0, 200m, rules);
        Assert.Equal(TradeExitReason.WindowEnd, outcome.ExitReason);
        Assert.Equal(104m, outcome.ExitPrice);
    }

    [Fact]
    public void Immediate_HonoursNoStopAtAll()
    {
        // With the stop disabled the trade must ride the drawdown out rather than exiting.
        var candles = Bars(
            (100m, 100m, 100m, 100m),
            (100m, 100m, 50m, 60m),
            (60m, 70m, 60m, 68m));

        var rules = Immediate(stop: 0m) with { WindowBars = 2 };
        var outcome = TradeSimulation.Simulate(candles, 0, 200m, rules);
        Assert.Equal(TradeExitReason.WindowEnd, outcome.ExitReason);
    }

    [Fact]
    public void Immediate_MovesTheTargetDownWithANewLow()
    {
        // The fall deepens to 50, so half of it is now 125, not 150. Holding the original target
        // would report a trade that never closed as one that did.
        var candles = Bars(
            (100m, 100m, 100m, 100m),
            (100m, 100m, 50m, 60m),
            (60m, 130m, 60m, 128m));

        var rules = Immediate(stop: 0m) with { WindowBars = 5 };
        var outcome = TradeSimulation.Simulate(candles, 0, 200m, rules);
        Assert.Equal(TradeExitReason.Target, outcome.ExitReason);
        Assert.Equal(125m, outcome.ExitPrice);
    }

    // ── Ratchet ─────────────────────────────────────────────────────────────

    [Fact]
    public void Ratchet_PullsTheStopToBreakEvenOnceHalfwayToTarget()
    {
        // Halfway to a 150 target is 125. After touching it the stop sits at entry, so the pullback
        // to 95 closes the trade flat rather than at the original 90 stop.
        var candles = Bars(
            (100m, 100m, 100m, 100m),
            (100m, 130m, 100m, 128m),
            (128m, 128m, 95m, 96m));

        var rules = Immediate() with { RatchetProgressFraction = 0.5m, RatchetLockFraction = 0m, WindowBars = 5 };
        var outcome = TradeSimulation.Simulate(candles, 0, 200m, rules);
        Assert.Equal(TradeExitReason.StopLoss, outcome.ExitReason);
        Assert.Equal(100m, outcome.ExitPrice);
        Assert.Equal(0m, outcome.ReturnFraction);
    }

    // ── Trailing stop ───────────────────────────────────────────────────────

    [Fact]
    public void Trailing_LetsAWinnerRunPastTheFixedTarget()
    {
        // Trailing arms at +5% and trails 8% below the best price. Price runs to 200 then falls back;
        // the exit is near 184, not at the 150 fixed target.
        var candles = Bars(
            (100m, 100m, 100m, 100m),
            (100m, 200m, 100m, 198m),
            (198m, 198m, 180m, 182m));

        var rules = new StrategyRules
        {
            Entry = EntryStyle.Immediate, TargetFraction = 5m, StopLossFraction = 0.10m,
            TrailingStopFraction = 0.08m, TrailingArmFraction = 0.05m, WindowBars = 5,
        };

        var outcome = TradeSimulation.Simulate(candles, 0, 200m, rules);
        Assert.Equal(TradeExitReason.TrailingStop, outcome.ExitReason);
        Assert.Equal(184m, outcome.ExitPrice);   // 200 less 8%
    }

    [Fact]
    public void Trailing_StaysDisarmedUntilTheGainReachesTheArmLevel()
    {
        // Price never gets 5% above entry, so the trail never arms and the original stop applies.
        var candles = Bars(
            (100m, 100m, 100m, 100m),
            (100m, 103m, 100m, 102m),
            (102m, 102m, 88m, 89m));

        var rules = new StrategyRules
        {
            Entry = EntryStyle.Immediate, TargetFraction = 5m, StopLossFraction = 0.10m,
            TrailingStopFraction = 0.08m, TrailingArmFraction = 0.05m, WindowBars = 5,
        };

        var outcome = TradeSimulation.Simulate(candles, 0, 200m, rules);
        Assert.Equal(TradeExitReason.StopLoss, outcome.ExitReason);
        Assert.Equal(90m, outcome.ExitPrice);
    }

    [Fact]
    public void Trailing_NeverLetsTheStopSlideBackDown()
    {
        // The trail must ratchet. If it followed price down it would be a stop that never triggers,
        // and every trade would run to the window end.
        var candles = Bars(
            (100m, 100m, 100m, 100m),
            (100m, 200m, 100m, 190m),
            (190m, 190m, 186m, 187m),
            (187m, 188m, 182m, 183m));

        var rules = new StrategyRules
        {
            Entry = EntryStyle.Immediate, TargetFraction = 5m, StopLossFraction = 0.10m,
            TrailingStopFraction = 0.08m, TrailingArmFraction = 0.05m, WindowBars = 5,
        };

        var outcome = TradeSimulation.Simulate(candles, 0, 200m, rules);
        Assert.Equal(TradeExitReason.TrailingStop, outcome.ExitReason);
        Assert.Equal(184m, outcome.ExitPrice);   // still anchored to the 200 high
    }

    // ── Regime filter ───────────────────────────────────────────────────────

    [Fact]
    public void TrendShield_OpensInASustainedRise()
    {
        var rising = Bars(Enumerable.Range(0, 200)
            .Select(i => { decimal p = 100m + i; return (p, p + 1m, p - 1m, p); }).ToArray());

        var shield = Indicators.TrendShield(rising, 50, 20);
        Assert.True(shield[^1], "a steady climb should pass the filter");
    }

    [Fact]
    public void TrendShield_ClosesInASustainedFall()
    {
        var falling = Bars(Enumerable.Range(0, 200)
            .Select(i => { decimal p = 300m - i; return (p, p + 1m, p - 1m, p); }).ToArray());

        var shield = Indicators.TrendShield(falling, 50, 20);
        Assert.False(shield[^1], "a steady decline should not pass the filter");
    }

    [Fact]
    public void TrendShield_StaysClosedAcrossTheWarmUp()
    {
        var rising = Bars(Enumerable.Range(0, 200)
            .Select(i => { decimal p = 100m + i; return (p, p + 1m, p - 1m, p); }).ToArray());

        var shield = Indicators.TrendShield(rising, 50, 20);
        Assert.All(shield.Take(50), open => Assert.False(open));
    }

    [Fact]
    public void TrendShield_InvertsUnderTheFallingAverageMode()
    {
        var falling = Bars(Enumerable.Range(0, 200)
            .Select(i => { decimal p = 300m - i; return (p, p + 1m, p - 1m, p); }).ToArray());

        Assert.True(Indicators.TrendShield(falling, 50, 20, Indicators.TrendShieldMode.AverageFalling)[^1]);
    }

    [Fact]
    public void Backtest_SkipsSignalsTheRegimeFilterRejects()
    {
        // A market falling throughout: the trend-gated rule must decline every signal while the
        // ungated ones still take them, and the rejected trades must show as untaken rather than
        // quietly vanishing from the signal count.
        var falling = Bars(Enumerable.Range(0, 300)
            .Select(i => { decimal p = 400m - i; return (p, p + 2m, p - 2m, p); }).ToArray());

        var events = Enumerable.Range(1, 12)
            .Select(i => new PlummetEvent { TriggerTime = falling[i * 15].OpenTime, ReferenceHigh = 500m, EventLow = 300m })
            .ToList();

        var report = StrategyBacktest.Run(falling, events, 10, 1000m, 0.0026m, 0.001m);
        var gated = report.Strategies.Single(r => r.Name.Contains("only in an uptrend"));
        var ungated = report.Strategies.Single(r => r.Name == "Buy the low, half back");

        Assert.Equal(0, gated.Filled);
        Assert.True(ungated.Filled > 0, "the ungated rule should still have traded");
        Assert.Equal(ungated.Signals, gated.Signals);
    }

    // ── Resting limit ───────────────────────────────────────────────────────

    [Fact]
    public void RestingLimit_FillsWhenPriceComesDownToIt()
    {
        // Limit sits 10% of the 100-wide fall below the 100 close, i.e. at 90.
        var candles = Bars(
            (100m, 100m, 100m, 100m),
            (100m, 100m, 88m, 92m),
            (92m, 160m, 92m, 155m));

        var rules = new StrategyRules { Entry = EntryStyle.RestingLimit, TargetFraction = 0.5m, StopLossFraction = 0m, LimitDepthFraction = 0.10m, WindowBars = 5 };
        var outcome = TradeSimulation.Simulate(candles, 0, 200m, rules);
        Assert.Equal(TradeExitReason.Target, outcome.ExitReason);
        Assert.Equal(90m, outcome.EntryPrice);
    }

    [Fact]
    public void RestingLimit_CountsAMissedEntryAsNotFilledRatherThanAWin()
    {
        // Price rebounds straight to target without ever revisiting the limit. Scoring this as a win
        // is the classic way a resting-limit backtest invents profit it could never have earned.
        var candles = Bars(
            (100m, 100m, 100m, 100m),
            (100m, 160m, 99m, 158m));

        var rules = new StrategyRules { Entry = EntryStyle.RestingLimit, TargetFraction = 0.5m, LimitDepthFraction = 0.10m, WindowBars = 5 };
        var outcome = TradeSimulation.Simulate(candles, 0, 200m, rules);
        Assert.Equal(TradeExitReason.NotFilled, outcome.ExitReason);
        Assert.False(outcome.Filled);
        Assert.Equal(0m, outcome.ReturnFraction);
    }

    // ── Stabilised and onset entries ────────────────────────────────────────

    [Fact]
    public void Stabilised_WaitsForTheLowToStopMoving()
    {
        // New lows on bars 1 and 2; bars 3 and 4 hold, so entry comes on bar 4 at its close.
        var candles = Bars(
            (100m, 100m, 100m, 100m),
            (100m, 100m, 90m, 92m),
            (92m, 94m, 80m, 82m),
            (82m, 86m, 81m, 85m),
            (85m, 88m, 82m, 87m),
            (87m, 200m, 87m, 195m));

        var rules = new StrategyRules { Entry = EntryStyle.Stabilised, TargetFraction = 0.5m, StopLossFraction = 0m, StabilisationBars = 2, WindowBars = 8 };
        var outcome = TradeSimulation.Simulate(candles, 0, 200m, rules);
        Assert.True(outcome.Filled);
        Assert.Equal(87m, outcome.EntryPrice);
    }

    [Fact]
    public void ReboundOnset_WaitsForTheBounceToBegin()
    {
        // The fall bottoms at 100 against a high of 200; a tenth of that fall is an entry at 110.
        var candles = Bars(
            (100m, 100m, 100m, 100m),
            (100m, 105m, 100m, 103m),
            (103m, 120m, 102m, 118m));

        var rules = new StrategyRules { Entry = EntryStyle.ReboundOnset, TargetFraction = 0.5m, StopLossFraction = 0m, OnsetFraction = 0.10m, WindowBars = 5 };
        var outcome = TradeSimulation.Simulate(candles, 0, 200m, rules);
        Assert.True(outcome.Filled);
        Assert.Equal(110m, outcome.EntryPrice);
    }

    [Fact]
    public void ReboundOnset_NeverFillsIfTheBounceNeverComes()
    {
        var candles = Bars(
            (100m, 100m, 100m, 100m),
            (100m, 101m, 90m, 91m),
            (91m, 92m, 85m, 86m));

        var rules = new StrategyRules { Entry = EntryStyle.ReboundOnset, TargetFraction = 0.5m, OnsetFraction = 0.5m, WindowBars = 5 };
        Assert.Equal(TradeExitReason.NotFilled, TradeSimulation.Simulate(candles, 0, 200m, rules).ExitReason);
    }

    // ── Costs and capital ───────────────────────────────────────────────────

    [Fact]
    public void NetProfit_TurnsASmallGrossGainIntoALoss()
    {
        // A 0.4% gross gain does not survive 0.26% a side plus a 0.1% spread allowance.
        decimal net = TradeSimulation.NetProfit(1000m, 0.004m, 0.0026m, 0.001m);
        Assert.True(net < 0m, $"costs should swallow a 0.4% gain, got {net}");
    }

    [Fact]
    public void NetProfit_LeavesALargeGainStandingAfterCosts()
    {
        decimal net = TradeSimulation.NetProfit(1000m, 0.50m, 0.0026m, 0.001m);
        Assert.InRange(net, 480m, 500m);
    }

    [Fact]
    public void NetProfit_IsZeroCostFree()
    {
        Assert.Equal(100m, TradeSimulation.NetProfit(1000m, 0.10m, 0m, 0m));
    }

    [Fact]
    public void PeakOpenCapital_CountsOverlappingPositionsOnceEach()
    {
        // Three positions but only two ever open together, so the strategy needed 2,000, not 3,000.
        var positions = new List<(DateTime, DateTime, decimal)>
        {
            (Origin, Origin.AddHours(5), 1000m),
            (Origin.AddHours(1), Origin.AddHours(3), 1000m),
            (Origin.AddHours(10), Origin.AddHours(12), 1000m),
        };
        Assert.Equal(2000m, TradeSimulation.PeakOpenCapital(positions));
    }

    [Fact]
    public void PeakOpenCapital_IsZeroWithNoPositions()
    {
        Assert.Equal(0m, TradeSimulation.PeakOpenCapital([]));
    }

    [Fact]
    public void Concurrency_ReportsTheWorstOverlap()
    {
        var positions = new List<(DateTime, DateTime)>
        {
            (Origin, Origin.AddHours(10)),
            (Origin.AddHours(1), Origin.AddHours(9)),
            (Origin.AddHours(2), Origin.AddHours(8)),
        };
        var (maximum, mean) = TradeSimulation.Concurrency(positions);
        Assert.Equal(3, maximum);
        Assert.True(mean > 1m);
    }

    // ── Backtest harness ────────────────────────────────────────────────────

    [Fact]
    public void Backtest_ExcludesFallsWithoutAFullWindowAhead()
    {
        // A fall two bars from the end of the data cannot have been held for ten. Scoring it would
        // judge the strategy on a life it never got to live.
        var candles = Bars(Enumerable.Range(0, 12).Select(i => (100m, 101m, 99m, 100m)).ToArray());
        var events = new List<PlummetEvent>
        {
            new() { TriggerTime = candles[1].OpenTime, ReferenceHigh = 200m, EventLow = 100m },
            new() { TriggerTime = candles[10].OpenTime, ReferenceHigh = 200m, EventLow = 100m },
        };

        var report = StrategyBacktest.Run(candles, events, windowBars: 10, stake: 1000m, feeFractionPerSide: 0m, spreadAllowanceFraction: 0m);
        Assert.Equal(2, report.EventCount);
        Assert.Equal(1, report.SettledEventCount);
    }

    [Fact]
    public void Backtest_SaysSoWhenTheSampleIsTooThinToMeanAnything()
    {
        var candles = Bars(Enumerable.Range(0, 40).Select(i => (100m, 101m, 99m, 100m)).ToArray());
        var events = new List<PlummetEvent>
        {
            new() { TriggerTime = candles[1].OpenTime, ReferenceHigh = 200m, EventLow = 100m },
        };

        var report = StrategyBacktest.Run(candles, events, 10, 1000m, 0m, 0m);
        Assert.NotNull(report.Caveat);
        Assert.Contains("too few", report.Caveat!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Backtest_ReportsEveryStrategyEvenWithNothingToTrade()
    {
        var candles = Bars(Enumerable.Range(0, 20).Select(i => (100m, 101m, 99m, 100m)).ToArray());
        var report = StrategyBacktest.Run(candles, [], 10, 1000m, 0.0026m, 0.001m);

        Assert.Equal(0, report.SettledEventCount);
        Assert.NotEmpty(report.Strategies);
        Assert.All(report.Strategies, r => Assert.Equal(0, r.Filled));
        Assert.NotNull(report.Caveat);
    }

    [Fact]
    public void Backtest_SplitsProfitAcrossTheTwoHalvesOfThePeriod()
    {
        // Every profit must land in one half or the other and the two must reconstitute the total —
        // the split is what exposes a strategy whose edge has since disappeared.
        var bars = new List<(decimal, decimal, decimal, decimal)>();
        for (int i = 0; i < 200; i++) bars.Add((100m, 160m, 95m, 120m));
        var candles = Bars(bars.ToArray());

        var events = Enumerable.Range(1, 20)
            .Select(i => new PlummetEvent { TriggerTime = candles[i * 5].OpenTime, ReferenceHigh = 200m, EventLow = 100m })
            .ToList();

        var report = StrategyBacktest.Run(candles, events, 10, 1000m, 0.0026m, 0.001m);
        foreach (var strategy in report.Strategies)
            Assert.Equal(strategy.NetProfit, Math.Round(strategy.FirstHalfNetProfit + strategy.SecondHalfNetProfit, 2));
    }

    [Fact]
    public void Backtest_NeverReportsMoreFillsThanSignals()
    {
        var bars = new List<(decimal, decimal, decimal, decimal)>();
        for (int i = 0; i < 200; i++) bars.Add((100m, 160m, 80m, 120m));
        var candles = Bars(bars.ToArray());

        var events = Enumerable.Range(1, 15)
            .Select(i => new PlummetEvent { TriggerTime = candles[i * 8].OpenTime, ReferenceHigh = 200m, EventLow = 100m })
            .ToList();

        var report = StrategyBacktest.Run(candles, events, 10, 1000m, 0.0026m, 0.001m);
        Assert.All(report.Strategies, r =>
        {
            Assert.True(r.Filled <= r.Signals, $"{r.Name} filled {r.Filled} of {r.Signals} signals");
            Assert.True(r.Wins <= r.Filled, $"{r.Name} won {r.Wins} of {r.Filled} fills");
        });
    }
}
