namespace KrakenReact.Server.Analysis;

/// <summary>How a simulated trade ended.</summary>
public enum TradeExitReason
{
    /// <summary>The rebound target was reached.</summary>
    Target,

    /// <summary>Price fell to the stop below the entry.</summary>
    StopLoss,

    /// <summary>The trailing stop triggered below the running high.</summary>
    TrailingStop,

    /// <summary>The window ran out with the position still open; it exits at the final close.</summary>
    WindowEnd,

    /// <summary>The entry never filled, so no position was ever opened.</summary>
    NotFilled,
}

/// <summary>What one simulated trade did.</summary>
public sealed record TradeOutcome(
    decimal EntryPrice,
    int EntryMinutes,
    decimal ExitPrice,
    int ExitMinutes,
    TradeExitReason ExitReason)
{
    public bool Filled => ExitReason != TradeExitReason.NotFilled;

    /// <summary>The gross return before costs, as a fraction of the entry price.</summary>
    public decimal ReturnFraction => EntryPrice > 0m ? (ExitPrice - EntryPrice) / EntryPrice : 0m;

    /// <summary>How long the position was held.</summary>
    public int HeldMinutes => Math.Max(0, ExitMinutes - EntryMinutes);

    public static TradeOutcome NotFilled => new(0m, 0, 0m, 0, TradeExitReason.NotFilled);
}

/// <summary>How a strategy decides when to get in after a fall is detected.</summary>
public enum EntryStyle
{
    /// <summary>Buy at the close of the bar that triggered the detection.</summary>
    Immediate,

    /// <summary>Rest a limit below the trigger price and buy only if price comes down to it.</summary>
    RestingLimit,

    /// <summary>Wait for the price to stop making new lows for a set number of bars.</summary>
    Stabilised,

    /// <summary>Wait until the rebound has already begun by a set share of the fall.</summary>
    ReboundOnset,
}

/// <summary>The rules one simulated strategy plays by.</summary>
public sealed record StrategyRules
{
    public EntryStyle Entry { get; init; } = EntryStyle.Immediate;

    /// <summary>The share of the fall to aim to recover, measured from the running low.</summary>
    public decimal TargetFraction { get; init; } = 0.50m;

    /// <summary>The drop below entry that closes the trade; zero means no stop at all.</summary>
    public decimal StopLossFraction { get; init; } = 0.10m;

    /// <summary>For a resting limit: how much further below the trigger, as a share of the fall.</summary>
    public decimal LimitDepthFraction { get; init; } = 0.10m;

    /// <summary>For a stabilised entry: bars without a new low before buying.</summary>
    public int StabilisationBars { get; init; } = 3;

    /// <summary>For a rebound-onset entry: the share of the fall that must already be recovered.</summary>
    public decimal OnsetFraction { get; init; } = 0.10m;

    /// <summary>Bars to hold before giving up and exiting at the close.</summary>
    public int WindowBars { get; init; } = 72;

    /// <summary>
    /// The share of the way to target at which the stop is pulled up to break even; zero disables it.
    /// </summary>
    public decimal RatchetProgressFraction { get; init; }

    /// <summary>Where the ratcheted stop lands, as a fraction above entry.</summary>
    public decimal RatchetLockFraction { get; init; }

    /// <summary>
    /// Once armed, the stop trails this far below the running high; zero disables trailing. A trailing
    /// exit gives up a fixed slice of the best price seen in exchange for not capping the upside at a
    /// fixed target.
    /// </summary>
    public decimal TrailingStopFraction { get; init; }

    /// <summary>The gain above entry at which trailing arms. Zero arms it immediately.</summary>
    public decimal TrailingArmFraction { get; init; }
}

/// <summary>
/// Replays what a rebound strategy would have done on the falls a market has actually had.
/// <para>
/// Every walk is bar-by-bar and forward-only: a stop is checked against the bar's own open before its
/// low, and a target against its open before its high, so a bar that spans both is resolved the way
/// that costs the trade rather than the way that flatters it. The simulation never sees a price it
/// could not have seen at the time.
/// </para>
/// </summary>
public static class TradeSimulation
{
    /// <summary>Runs one strategy against one detected fall.</summary>
    public static TradeOutcome Simulate(IReadOnlyList<AnalysisCandle> candles, int triggerIndex, decimal referenceHigh, StrategyRules rules)
    {
        if (triggerIndex < 0 || triggerIndex >= candles.Count - 1) return TradeOutcome.NotFilled;
        int windowEnd = Math.Min(triggerIndex + rules.WindowBars, candles.Count - 1);

        return rules.Entry switch
        {
            EntryStyle.Immediate => SimulateImmediate(candles, triggerIndex, referenceHigh, rules, windowEnd),
            EntryStyle.RestingLimit => SimulateRestingLimit(candles, triggerIndex, referenceHigh, rules, windowEnd),
            EntryStyle.Stabilised => SimulateStabilised(candles, triggerIndex, referenceHigh, rules, windowEnd),
            EntryStyle.ReboundOnset => SimulateReboundOnset(candles, triggerIndex, referenceHigh, rules, windowEnd),
            _ => TradeOutcome.NotFilled,
        };
    }

    private static TradeOutcome SimulateImmediate(IReadOnlyList<AnalysisCandle> candles, int triggerIndex, decimal referenceHigh, StrategyRules rules, int windowEnd)
    {
        decimal entryPrice = candles[triggerIndex].Close;
        decimal low = candles[triggerIndex].Low;
        decimal stopPrice = rules.StopLossFraction > 0m ? entryPrice * (1m - rules.StopLossFraction) : 0m;
        decimal targetPrice = low + rules.TargetFraction * (referenceHigh - low);
        bool ratcheted = false, trailingArmed = false;
        decimal runningHigh = entryPrice;

        for (int j = triggerIndex + 1; j <= windowEnd; j++)
        {
            int minutes = MinutesFrom(candles, triggerIndex, j);

            // A gap through the stop fills at the open, not at the stop price — the difference is
            // slippage, and pretending it away is what makes a backtest lie.
            var stopReason = trailingArmed ? TradeExitReason.TrailingStop : TradeExitReason.StopLoss;
            if (stopPrice > 0m && candles[j].Open <= stopPrice) return new TradeOutcome(entryPrice, 0, candles[j].Open, minutes, stopReason);
            if (stopPrice > 0m && candles[j].Low <= stopPrice) return new TradeOutcome(entryPrice, 0, stopPrice, minutes, stopReason);
            if (candles[j].Open >= targetPrice) return new TradeOutcome(entryPrice, 0, candles[j].Open, minutes, TradeExitReason.Target);
            if (candles[j].High >= targetPrice) return new TradeOutcome(entryPrice, 0, targetPrice, minutes, TradeExitReason.Target);

            if (!ratcheted && rules.RatchetProgressFraction > 0m && targetPrice > entryPrice &&
                candles[j].High >= entryPrice + rules.RatchetProgressFraction * (targetPrice - entryPrice))
            {
                stopPrice = entryPrice * (1m + rules.RatchetLockFraction);
                ratcheted = true;
            }

            if (rules.TrailingStopFraction > 0m)
            {
                if (!trailingArmed && candles[j].High >= entryPrice * (1m + rules.TrailingArmFraction))
                    trailingArmed = true;
                if (trailingArmed)
                {
                    if (candles[j].High > runningHigh) runningHigh = candles[j].High;
                    // The trail only ever rises: letting it fall back with the price would turn it
                    // into a stop that never triggers.
                    stopPrice = Math.Max(stopPrice, runningHigh * (1m - rules.TrailingStopFraction));
                }
            }

            // A new low moves the target down with it: the trade is chasing a share of the fall from
            // wherever the bottom turns out to be, not from where it first looked like being.
            if (candles[j].Low < low)
            {
                low = candles[j].Low;
                targetPrice = low + rules.TargetFraction * (referenceHigh - low);
            }
        }
        return CloseAtWindowEnd(candles, triggerIndex, windowEnd, entryPrice, 0);
    }

    private static TradeOutcome SimulateRestingLimit(IReadOnlyList<AnalysisCandle> candles, int triggerIndex, decimal referenceHigh, StrategyRules rules, int windowEnd)
    {
        decimal low = candles[triggerIndex].Low;
        decimal limitPrice = candles[triggerIndex].Close - rules.LimitDepthFraction * (referenceHigh - low);
        if (limitPrice <= 0m) return TradeOutcome.NotFilled;

        for (int j = triggerIndex + 1; j <= windowEnd; j++)
        {
            int minutes = MinutesFrom(candles, triggerIndex, j);
            if (candles[j].Open <= limitPrice)
                return WalkToTarget(candles, j, candles[j].Open, minutes, Math.Min(low, candles[j].Low), referenceHigh, rules, triggerIndex, windowEnd);
            if (candles[j].Low <= limitPrice)
                return WalkToTarget(candles, j, limitPrice, minutes, Math.Min(low, candles[j].Low), referenceHigh, rules, triggerIndex, windowEnd);

            // The rebound reached target without ever coming back for the limit: the trade is missed,
            // not won. Counting it as a win is the classic way a resting-limit backtest flatters itself.
            if (candles[j].High >= low + rules.TargetFraction * (referenceHigh - low)) return TradeOutcome.NotFilled;
            if (candles[j].Low < low) low = candles[j].Low;
        }
        return TradeOutcome.NotFilled;
    }

    private static TradeOutcome SimulateStabilised(IReadOnlyList<AnalysisCandle> candles, int triggerIndex, decimal referenceHigh, StrategyRules rules, int windowEnd)
    {
        decimal low = candles[triggerIndex].Low;
        int lowIndex = triggerIndex;

        for (int j = triggerIndex + 1; j <= windowEnd; j++)
        {
            if (candles[j].Low < low) { low = candles[j].Low; lowIndex = j; continue; }
            if (j - lowIndex >= rules.StabilisationBars)
                return WalkToTarget(candles, j, candles[j].Close, MinutesFrom(candles, triggerIndex, j), low, referenceHigh, rules, triggerIndex, windowEnd);
        }
        return TradeOutcome.NotFilled;
    }

    private static TradeOutcome SimulateReboundOnset(IReadOnlyList<AnalysisCandle> candles, int triggerIndex, decimal referenceHigh, StrategyRules rules, int windowEnd)
    {
        decimal low = candles[triggerIndex].Low;

        for (int j = triggerIndex + 1; j <= windowEnd; j++)
        {
            int minutes = MinutesFrom(candles, triggerIndex, j);
            decimal entryTrigger = low + rules.OnsetFraction * (referenceHigh - low);
            if (candles[j].Open >= entryTrigger)
                return WalkToTarget(candles, j, candles[j].Open, minutes, Math.Min(low, candles[j].Low), referenceHigh, rules, triggerIndex, windowEnd);
            if (candles[j].High >= entryTrigger)
                return WalkToTarget(candles, j, entryTrigger, minutes, Math.Min(low, candles[j].Low), referenceHigh, rules, triggerIndex, windowEnd);
            if (candles[j].Low < low) low = candles[j].Low;
        }
        return TradeOutcome.NotFilled;
    }

    /// <summary>Walks forward from a filled entry to whichever of target, stop or window end comes first.</summary>
    private static TradeOutcome WalkToTarget(
        IReadOnlyList<AnalysisCandle> candles, int entryIndex, decimal entryPrice, int entryMinutes,
        decimal low, decimal referenceHigh, StrategyRules rules, int triggerIndex, int windowEnd)
    {
        decimal stopPrice = rules.StopLossFraction > 0m ? entryPrice * (1m - rules.StopLossFraction) : 0m;

        for (int j = entryIndex + 1; j <= windowEnd; j++)
        {
            int minutes = MinutesFrom(candles, triggerIndex, j);
            decimal targetPrice = low + rules.TargetFraction * (referenceHigh - low);

            if (stopPrice > 0m && candles[j].Open <= stopPrice) return new TradeOutcome(entryPrice, entryMinutes, candles[j].Open, minutes, TradeExitReason.StopLoss);
            if (stopPrice > 0m && candles[j].Low <= stopPrice) return new TradeOutcome(entryPrice, entryMinutes, stopPrice, minutes, TradeExitReason.StopLoss);
            if (candles[j].Open >= targetPrice) return new TradeOutcome(entryPrice, entryMinutes, candles[j].Open, minutes, TradeExitReason.Target);
            if (candles[j].High >= targetPrice) return new TradeOutcome(entryPrice, entryMinutes, targetPrice, minutes, TradeExitReason.Target);
            if (candles[j].Low < low) low = candles[j].Low;
        }
        return CloseAtWindowEnd(candles, triggerIndex, windowEnd, entryPrice, entryMinutes);
    }

    private static TradeOutcome CloseAtWindowEnd(IReadOnlyList<AnalysisCandle> candles, int triggerIndex, int windowEnd, decimal entryPrice, int entryMinutes) =>
        new(entryPrice, entryMinutes, candles[windowEnd].Close, MinutesFrom(candles, triggerIndex, windowEnd), TradeExitReason.WindowEnd);

    private static int MinutesFrom(IReadOnlyList<AnalysisCandle> candles, int fromIndex, int toIndex) =>
        (int)(candles[toIndex].OpenTime - candles[fromIndex].OpenTime).TotalMinutes;

    /// <summary>
    /// The profit on a stake after both sides' fees and an allowance for crossing the spread. Costs
    /// are what turn a strategy that looks profitable on paper into one that is not.
    /// </summary>
    public static decimal NetProfit(decimal stake, decimal returnFraction, decimal feeFractionPerSide, decimal spreadAllowanceFraction) =>
        stake * ((1m + returnFraction - spreadAllowanceFraction) * (1m - feeFractionPerSide) - (1m + feeFractionPerSide));

    /// <summary>
    /// The most capital the strategy ever had committed at once. A strategy's return has to be judged
    /// against this, not against the sum of its stakes: ten trades of a hundred that never overlap
    /// need a hundred, not a thousand.
    /// </summary>
    public static decimal PeakOpenCapital(IReadOnlyList<(DateTime Start, DateTime End, decimal Stake)> positions)
    {
        if (positions.Count == 0) return 0m;

        var boundaries = positions
            .SelectMany(p => new[] { (Time: p.Start, Delta: p.Stake), (Time: p.End, Delta: -p.Stake) })
            .OrderBy(b => b.Time).ThenBy(b => b.Delta)
            .ToList();

        decimal current = 0m, maximum = 0m;
        foreach (var b in boundaries)
        {
            current += b.Delta;
            if (current > maximum) maximum = current;
        }
        return maximum;
    }

    /// <summary>How many positions were open at once, at the worst and on average.</summary>
    public static (int Maximum, decimal Mean) Concurrency(IReadOnlyList<(DateTime Start, DateTime End)> positions)
    {
        if (positions.Count == 0) return (0, 0m);

        var boundaries = positions
            .SelectMany(p => new[] { (Time: p.Start, Delta: 1), (Time: p.End, Delta: -1) })
            .OrderBy(b => b.Time).ThenBy(b => b.Delta)
            .ToList();

        int current = 0, maximum = 0;
        foreach (var b in boundaries)
        {
            current += b.Delta;
            if (current > maximum) maximum = current;
        }

        double totalMinutes = positions.Sum(p => (p.End - p.Start).TotalMinutes);
        double spanMinutes = (positions.Max(p => p.End) - positions.Min(p => p.Start)).TotalMinutes;
        return (maximum, spanMinutes > 0d ? (decimal)(totalMinutes / spanMinutes) : positions.Count);
    }
}
