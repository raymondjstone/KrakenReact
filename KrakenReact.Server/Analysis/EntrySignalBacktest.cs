namespace KrakenReact.Server.Analysis;

/// <summary>What one entry-detector family did across a market's history.</summary>
public sealed record EntryFamilyResult(
    string Name,
    string Description,
    int Signals,
    int Scored,
    int Wins,
    decimal WinRatePercent,
    decimal AverageForwardReturnPercent,
    decimal MedianMaxFavourablePercent,
    decimal MedianMaxAdversePercent,
    decimal? MedianBarsToTarget,
    decimal MedianConfirmationLagBars,
    decimal NetProfit,
    decimal NetReturnOnStakePercent,
    decimal FirstHalfNetProfit,
    decimal SecondHalfNetProfit);

/// <summary>The comparison across entry-detector families, plus the honest reading of it.</summary>
public sealed record EntrySignalsReport(
    int ScoredWindowBars,
    decimal TargetPercent,
    decimal StopPercent,
    decimal Stake,
    decimal FeePercentPerSide,
    decimal SpreadAllowancePercent,
    IReadOnlyList<EntryFamilyResult> Families,
    string? Caveat);

/// <summary>
/// Replays every <see cref="EntryDetection"/> family over one market's candles as a stream of
/// non-overlapping trades — enter at the confirming close, take a fixed percentage target, stop a
/// fixed percentage below, hold at most a window — so the families can be ranked on what they would
/// have done rather than on how plausible each one sounds.
/// <para>
/// It is written to err pessimistically, for the same reason <see cref="StrategyBacktest"/> is: a bar
/// that gaps through the stop fills at its open, a bar that touches both the stop and the target is a
/// loss, and a family re-arms only once its current trade has closed, so a burst of signals around one
/// low is scored as the single trade it really was. Results are split into the first and second half
/// of the period: a family that made everything early and nothing late has found a past market
/// condition, not an edge.
/// </para>
/// </summary>
public static class EntrySignalBacktest
{
    /// <summary>
    /// Runs the families. Bar-count parameters are scaled from one-minute calibration by
    /// <paramref name="barScale"/> = 60 / intervalMinutes, the same convention the plummet study uses,
    /// so a "120-bar prior fall" stays two hours of history on every timeframe.
    /// </summary>
    public static EntrySignalsReport Run(
        IReadOnlyList<AnalysisCandle> candles,
        TrendDetectionParameters trendParameters,
        decimal[] averageTrueRanges,
        IReadOnlyList<TrendPivot> pivots,
        int windowBars,
        decimal targetFraction,
        decimal stopFraction,
        decimal stake,
        decimal feeFractionPerSide,
        decimal spreadAllowanceFraction)
    {
        int count = candles.Count;
        int intervalMinutes = Math.Max(1, Indicators.InferIntervalMinutes(candles));
        double barScale = 60.0 / intervalMinutes;
        int Bars(int oneMinuteBars) => Math.Max(2, (int)Math.Round(oneMinuteBars * barScale));

        int priorFallLookback = Bars(720);   // half a day of minute bars
        int referenceLow = Bars(120);
        int volumeMedianWindow = Bars(1440); // a day
        var trailingMedianVolumes = EntryDetection.ComputeTrailingMedianVolumes(candles, volumeMedianWindow);

        var families = new (string Name, string Description, List<EntrySignal> Signals)[]
        {
            ("Drawdown from the high",
             "Buy once the close is 12% below the highest high of the trailing window — the control the rest must beat.",
             EntryDetection.Drawdown(candles, priorFallLookback, 0.12m, referenceLow)),

            ("RSI reclaim",
             "Buy the bar the 14-period RSI rises back through 30 from below it.",
             EntryDetection.RsiReclaim(candles, 14, 30m, referenceLow)),

            ("Down-close exhaustion",
             "Buy the first up close after four or more consecutive lower closes that shed at least 5%.",
             EntryDetection.DownCloseExhaustion(candles, 4, 0.05m, referenceLow)),

            ("MACD cross up below zero",
             "Buy when the MACD line crosses up through its signal while still beneath zero, after a 10% fall.",
             EntryDetection.MacdCrossUp(candles, trendParameters, requireBelowZero: true, 0.10m, priorFallLookback, referenceLow, onlyFirstPerFall: true)),

            ("Lower Bollinger-band reclaim",
             "Buy the bar the close comes back inside the 20-period, 2σ lower band after closing outside it, following a 10% fall.",
             EntryDetection.LowerBandReclaim(candles, 20, 2m, 0.10m, priorFallLookback)),

            ("Climax candle absorbed",
             "Buy a candle that fell 2.5×ATR from the prior close, closed in its top half, and held its low the next bar.",
             EntryDetection.ClimaxAbsorption(candles, averageTrueRanges, 2.5m, 0.5m, 0.08m, priorFallLookback, followThroughBars: 1)),

            ("Selling climax on volume",
             "Buy after a 3× median-volume down candle takes out the fall's low and the next two bars hold above it.",
             EntryDetection.SellingClimax(candles, trailingMedianVolumes, averageTrueRanges, 3m, 1.5m, 0.4m, 2, 0.10m, priorFallLookback)),

            ("Volume dry-up at the low",
             "Buy when the last stretch of bars traded under 40% of the earlier median while making no new low.",
             EntryDetection.VolumeDryUp(candles, trailingMedianVolumes, 0.4m, Bars(240), 0.10m, priorFallLookback)),

            ("Reclaim of the fall's VWAP",
             "Buy the bar the close crosses back above the volume-weighted average price of the fall it is in.",
             EntryDetection.VwapOfFallReclaim(candles, priorFallLookback, 0.10m)),

            ("Curve vertex",
             "Buy when a parabola fitted to the smoothed closes opens upward with its vertex at the current bar, after a 10% fall.",
             EntryDetection.CurveVertex(candles, Bars(120), useZeroLagSmoother: false, Bars(240), 0.005m, 0.10m, priorFallLookback, Bars(120), maximumVertexAgeBars: 3m, maximumVertexLeadBars: 1m)),

            ("Swept swing low reclaimed",
             "Buy when price trades 0.5% below a confirmed swing low and then closes back above it.",
             EntryDetection.LiquiditySweepReclaim(candles, pivots, 0.005m, referenceLow)),

            ("Fair-value gap retest",
             "Rest a bid at the lower edge of a three-candle upward imbalance of at least 0.4%.",
             EntryDetection.FairValueGapRetest(candles, 0.004m)),
        };

        var results = families
            .Select(f => Evaluate(f.Name, f.Description, candles, f.Signals, windowBars, targetFraction, stopFraction, stake, feeFractionPerSide, spreadAllowanceFraction))
            .OrderByDescending(r => r.NetProfit)
            .ToList();

        int maxScored = results.Count == 0 ? 0 : results.Max(r => r.Scored);
        string? caveat = maxScored switch
        {
            0 => "No family produced a signal with a full forward window behind it in the stored candles.",
            < 10 => $"The busiest family had only {maxScored} scored trades. That is far too few to tell an edge from luck — read this as anecdote.",
            < 30 => $"{maxScored} scored trades at most is a thin sample. Treat the ranking as a hint about which reading suits this market, not a result.",
            _ => null,
        };

        return new EntrySignalsReport(
            windowBars,
            Math.Round(targetFraction * 100m, 2),
            Math.Round(stopFraction * 100m, 2),
            stake,
            Math.Round(feeFractionPerSide * 100m, 3),
            Math.Round(spreadAllowanceFraction * 100m, 3),
            results,
            caveat);
    }

    private static EntryFamilyResult Evaluate(
        string name, string description,
        IReadOnlyList<AnalysisCandle> candles,
        IReadOnlyList<EntrySignal> signals,
        int windowBars, decimal targetFraction, decimal stopFraction,
        decimal stake, decimal feeFractionPerSide, decimal spreadAllowanceFraction)
    {
        var ordered = signals.OrderBy(s => s.ConfirmationIndex).ToList();
        DateTime midpoint = ordered.Count == 0
            ? DateTime.MinValue
            : ordered[0].ConfirmationTime.AddMinutes((ordered[^1].ConfirmationTime - ordered[0].ConfirmationTime).TotalMinutes / 2);

        var forwardReturns = new List<decimal>();
        var maxFavourable = new List<decimal>();
        var maxAdverse = new List<decimal>();
        var barsToTarget = new List<decimal>();
        var lags = new List<decimal>();
        decimal netProfit = 0m, firstHalfNet = 0m;
        int scored = 0, wins = 0, nextFreeIndex = 0;

        foreach (var signal in ordered)
        {
            int entryIndex = signal.ConfirmationIndex;
            if (entryIndex < nextFreeIndex) continue;                    // a trade is already open
            if (entryIndex + windowBars >= candles.Count) continue;      // no full forward window

            decimal entry = candles[entryIndex].Close;
            if (entry <= 0m) continue;
            decimal target = entry * (1m + targetFraction);
            decimal stop = entry * (1m - stopFraction);

            decimal returnFraction = 0m, favourable = 0m, adverse = 0m;
            int exitIndex = entryIndex + windowBars;
            int? hitTargetBar = null;

            for (int j = entryIndex + 1; j <= entryIndex + windowBars; j++)
            {
                var bar = candles[j];
                favourable = Math.Max(favourable, entry > 0m ? bar.High / entry - 1m : 0m);
                adverse = Math.Min(adverse, entry > 0m ? bar.Low / entry - 1m : 0m);

                if (bar.Open <= stop) { returnFraction = bar.Open / entry - 1m; exitIndex = j; break; }
                if (bar.Open >= target) { returnFraction = bar.Open / entry - 1m; exitIndex = j; hitTargetBar = j; break; }
                bool hitStop = bar.Low <= stop, hitTarget = bar.High >= target;
                if (hitStop) { returnFraction = -stopFraction; exitIndex = j; break; }       // both in one bar → loss
                if (hitTarget) { returnFraction = targetFraction; exitIndex = j; hitTargetBar = j; break; }
                if (j == entryIndex + windowBars) returnFraction = bar.Close / entry - 1m;
            }

            decimal profit = TradeSimulation.NetProfit(stake, returnFraction, feeFractionPerSide, spreadAllowanceFraction);
            scored++;
            netProfit += profit;
            if (profit > 0m) wins++;
            if (signal.ConfirmationTime < midpoint) firstHalfNet += profit;

            forwardReturns.Add(returnFraction);
            maxFavourable.Add(favourable);
            maxAdverse.Add(adverse);
            lags.Add(signal.ConfirmationLagBars);
            if (hitTargetBar is int bar2) barsToTarget.Add(bar2 - entryIndex);

            nextFreeIndex = exitIndex + 1;
        }

        decimal roundedNet = Math.Round(netProfit, 2);
        decimal roundedFirstHalf = Math.Round(firstHalfNet, 2);

        return new EntryFamilyResult(
            name, description,
            signals.Count, scored, wins,
            scored == 0 ? 0m : Math.Round((decimal)wins / scored * 100m, 1),
            forwardReturns.Count == 0 ? 0m : Math.Round(forwardReturns.Average() * 100m, 2),
            maxFavourable.Count == 0 ? 0m : Math.Round(Indicators.Median(maxFavourable) * 100m, 2),
            maxAdverse.Count == 0 ? 0m : Math.Round(Indicators.Median(maxAdverse) * 100m, 2),
            barsToTarget.Count == 0 ? null : Math.Round(Indicators.Median(barsToTarget), 1),
            lags.Count == 0 ? 0m : Math.Round(Indicators.Median(lags), 1),
            roundedNet,
            Math.Round(stake > 0m ? netProfit / stake * 100m : 0m, 2),
            roundedFirstHalf,
            roundedNet - roundedFirstHalf);
    }
}
