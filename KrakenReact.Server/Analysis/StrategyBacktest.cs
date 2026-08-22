namespace KrakenReact.Server.Analysis;

/// <summary>What one strategy did across every fall a market has had.</summary>
public sealed record StrategyResult(
    string Name,
    string Description,
    int Signals,
    int Filled,
    int Wins,
    decimal WinRatePercent,
    decimal GrossReturnPercent,
    decimal NetProfit,
    decimal NetReturnOnPeakCapitalPercent,
    decimal PeakOpenCapital,
    decimal MedianHeldHours,
    int MaxConcurrent,
    decimal FirstHalfNetProfit,
    decimal SecondHalfNetProfit,
    IReadOnlyDictionary<string, int> ExitReasons);

/// <summary>The comparison across strategies, plus what the honest reading of it is.</summary>
public sealed record BacktestReport(
    int EventCount,
    int SettledEventCount,
    decimal Stake,
    decimal FeePercentPerSide,
    decimal SpreadAllowancePercent,
    IReadOnlyList<StrategyResult> Strategies,
    string? Caveat);

/// <summary>
/// Runs a handful of rebound strategies over the falls a market has actually had, so the analysis
/// stops describing history and starts answering what a rule would have done to a balance.
/// <para>
/// Results are split into the first and second half of the period on purpose. A strategy that made
/// all its money in the first half and none in the second has not found an edge; it has found a
/// market condition that has since gone.
/// </para>
/// </summary>
public static class StrategyBacktest
{
    /// <summary>The rule sets compared. Each is a different answer to "when do you buy a falling market?".</summary>
    /// <summary>Bars in the moving average the regime filter is built on, and its slope lookback.</summary>
    private const int ShieldAveragePeriodBars = 50;
    private const int ShieldSlopeLookbackBars = 20;

    private static readonly (string Name, string Description, StrategyRules Rules, bool RequireRisingTrend)[] Strategies =
    [
        ("Buy the low, half back",
         "Buy at the trigger close, aim to recover half the fall, stop 10% below entry.",
         new StrategyRules { Entry = EntryStyle.Immediate, TargetFraction = 0.50m, StopLossFraction = 0.10m }, false),

        ("Buy the low, quarter back",
         "The same, but taking a quarter of the fall — smaller target, hit more often.",
         new StrategyRules { Entry = EntryStyle.Immediate, TargetFraction = 0.25m, StopLossFraction = 0.10m }, false),

        ("Buy the low, no stop",
         "Buy at the trigger close and sit through the drawdown until half is recovered or the window ends.",
         new StrategyRules { Entry = EntryStyle.Immediate, TargetFraction = 0.50m, StopLossFraction = 0m }, false),

        ("Wait for a lower price",
         "Rest a limit 10% of the fall below the trigger; skip the trade if it never comes back.",
         new StrategyRules { Entry = EntryStyle.RestingLimit, TargetFraction = 0.50m, StopLossFraction = 0.10m, LimitDepthFraction = 0.10m }, false),

        ("Wait for it to settle",
         "Buy once the price has gone three bars without a new low.",
         new StrategyRules { Entry = EntryStyle.Stabilised, TargetFraction = 0.50m, StopLossFraction = 0.10m, StabilisationBars = 3 }, false),

        ("Wait for the bounce",
         "Buy only after 10% of the fall has already been recovered — later entry, more confirmation.",
         new StrategyRules { Entry = EntryStyle.ReboundOnset, TargetFraction = 0.50m, StopLossFraction = 0.10m, OnsetFraction = 0.10m }, false),

        ("Buy the low, lock at break even",
         "Buy at the trigger close; once halfway to target, pull the stop up to entry.",
         new StrategyRules { Entry = EntryStyle.Immediate, TargetFraction = 0.50m, StopLossFraction = 0.10m, RatchetProgressFraction = 0.50m, RatchetLockFraction = 0m }, false),

        ("Buy the low, trail the high",
         "Buy at the trigger close and never take a fixed profit: once 5% up, trail the stop 8% below the best price and let the rest run.",
         // The target is set past any plausible rebound on purpose. Leaving it at half the fall would
         // exit there every time and the trail would never get to do anything.
         new StrategyRules { Entry = EntryStyle.Immediate, TargetFraction = 100m, StopLossFraction = 0.10m, TrailingStopFraction = 0.08m, TrailingArmFraction = 0.05m }, false),

        ("Buy the low, only in an uptrend",
         "The same as buying the low, but skipped unless the 50-bar average is still rising — dips in an uptrend only.",
         new StrategyRules { Entry = EntryStyle.Immediate, TargetFraction = 0.50m, StopLossFraction = 0.10m }, true),
    ];

    /// <summary>
    /// Replays every strategy over the detected falls.
    /// </summary>
    /// <param name="candles">The market's candles, chronological.</param>
    /// <param name="events">The falls detected in that series.</param>
    /// <param name="windowBars">Bars to hold before giving up on a trade.</param>
    /// <param name="stake">The notional put into each trade.</param>
    /// <param name="feeFractionPerSide">Exchange fee on each side, as a fraction.</param>
    /// <param name="spreadAllowanceFraction">An allowance for crossing the spread, as a fraction.</param>
    public static BacktestReport Run(
        IReadOnlyList<AnalysisCandle> candles,
        IReadOnlyList<PlummetEvent> events,
        int windowBars,
        decimal stake,
        decimal feeFractionPerSide,
        decimal spreadAllowanceFraction)
    {
        // A trade needs the whole window ahead of it to have finished. One that runs off the end of
        // the data would be scored on a truncated life, and truncation always favours the strategy
        // that has not yet had time to be stopped out.
        var settled = events
            .Select(e => (Event: e, Index: Indicators.FindLastIndexAtOrBefore(candles, e.TriggerTime)))
            .Where(x => x.Index >= 0 && x.Index + windowBars < candles.Count)
            .ToList();

        // The regime filter is built once from the whole series; each bar's value depends only on
        // bars up to it, so gating a signal on it uses nothing that was not knowable at the time.
        // AverageRising, not PriceAboveRisingAverage: a fall deep enough to be detected has by
        // definition put price under its average, so the stricter gate would reject every signal and
        // the rule would never trade. What is wanted is a dip inside an uptrend, not a breakout.
        var shield = Indicators.TrendShield(
            candles, ShieldAveragePeriodBars, ShieldSlopeLookbackBars, Indicators.TrendShieldMode.AverageRising);

        var results = new List<StrategyResult>();
        foreach (var (name, description, baseRules, requireRisingTrend) in Strategies)
        {
            var rules = baseRules with { WindowBars = windowBars };
            var eligible = requireRisingTrend
                ? settled.Where(x => shield[x.Index]).ToList()
                : settled;
            results.Add(Evaluate(name, description, candles, eligible, rules, stake, feeFractionPerSide, spreadAllowanceFraction, settled.Count));
        }

        string falls = settled.Count == 1 ? "1 fall" : $"{settled.Count} falls";
        string? caveat = settled.Count switch
        {
            0 => "No fall has a full holding window behind it in the stored candles, so nothing could be simulated.",
            < 10 => $"Only {falls} had a complete window to trade. That is far too few to separate a real edge from luck — read the table as anecdote, not evidence.",
            < 30 => $"{falls} is a thin sample. Treat the ranking as a hint about which rule suits this market, not a result.",
            _ => null,
        };

        return new BacktestReport(
            events.Count,
            settled.Count,
            stake,
            feeFractionPerSide * 100m,
            spreadAllowanceFraction * 100m,
            results.OrderByDescending(r => r.NetProfit).ToList(),
            caveat);
    }

    private static StrategyResult Evaluate(
        string name, string description,
        IReadOnlyList<AnalysisCandle> candles,
        List<(PlummetEvent Event, int Index)> settled,
        StrategyRules rules,
        decimal stake, decimal feeFractionPerSide, decimal spreadAllowanceFraction,
        int totalSignals)
    {
        var positions = new List<(DateTime Start, DateTime End, decimal Stake)>();
        var exitReasons = new Dictionary<string, int>();
        var heldHours = new List<decimal>();
        decimal netProfit = 0m, firstHalfNet = 0m, grossReturn = 0m;
        int filled = 0, wins = 0;

        // The midpoint splits the period so the two halves can be compared for consistency.
        DateTime midpoint = settled.Count == 0
            ? DateTime.MinValue
            : settled[0].Event.TriggerTime.AddMinutes(
                (settled[^1].Event.TriggerTime - settled[0].Event.TriggerTime).TotalMinutes / 2);

        foreach (var (fall, index) in settled)
        {
            var outcome = TradeSimulation.Simulate(candles, index, fall.ReferenceHigh, rules);
            string reason = outcome.ExitReason.ToString();
            exitReasons[reason] = exitReasons.GetValueOrDefault(reason) + 1;

            if (!outcome.Filled) continue;

            filled++;
            grossReturn += outcome.ReturnFraction;
            decimal profit = TradeSimulation.NetProfit(stake, outcome.ReturnFraction, feeFractionPerSide, spreadAllowanceFraction);
            netProfit += profit;
            if (profit > 0m) wins++;

            DateTime entryTime = fall.TriggerTime.AddMinutes(outcome.EntryMinutes);
            DateTime exitTime = fall.TriggerTime.AddMinutes(outcome.ExitMinutes);
            positions.Add((entryTime, exitTime, stake));
            heldHours.Add((decimal)(exitTime - entryTime).TotalHours);
            if (fall.TriggerTime < midpoint) firstHalfNet += profit;
        }

        decimal peakCapital = TradeSimulation.PeakOpenCapital(positions);
        var (maxConcurrent, _) = positions.Count == 0
            ? (0, 0m)
            : TradeSimulation.Concurrency(positions.Select(p => (p.Start, p.End)).ToList());

        // Round the total and the first half, then derive the second from them. Rounding all three
        // independently lets the halves miss the total by a penny, and a table whose parts do not add
        // up invites the reader to distrust the parts that matter.
        decimal roundedNet = Math.Round(netProfit, 2);
        decimal roundedFirstHalf = Math.Round(firstHalfNet, 2);

        return new StrategyResult(
            name, description,
            totalSignals, filled, wins,
            filled == 0 ? 0m : Math.Round((decimal)wins / filled * 100m, 1),
            filled == 0 ? 0m : Math.Round(grossReturn / filled * 100m, 2),
            roundedNet,
            peakCapital > 0m ? Math.Round(netProfit / peakCapital * 100m, 2) : 0m,
            Math.Round(peakCapital, 2),
            heldHours.Count == 0 ? 0m : Math.Round(Indicators.Median(heldHours), 1),
            maxConcurrent,
            roundedFirstHalf,
            roundedNet - roundedFirstHalf,
            exitReasons);
    }
}
