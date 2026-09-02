namespace KrakenReact.Server.Analysis;

/// <summary>
/// A candidate entry into a fallen market: the low it keys off (<see cref="Index"/>, <see cref="Price"/>,
/// <see cref="Time"/>) and the earliest bar a live rule could have acted on it
/// (<see cref="ConfirmationIndex"/>, <see cref="ConfirmationTime"/>).
/// <para>
/// The two are always separate, because a low is only ever identifiable after price has moved away
/// from it. Every detector below reads only candles at or before the confirmation index it reports,
/// so a rule built on one can be replayed live without ever touching a value it could not have known.
/// </para>
/// </summary>
public sealed record EntrySignal(int Index, decimal Price, DateTime Time, int ConfirmationIndex, DateTime ConfirmationTime)
{
    /// <summary>Candles between the low and its confirmation — the unavoidable lag any rule pays.</summary>
    public int ConfirmationLagBars => ConfirmationIndex - Index;
}

/// <summary>
/// Finds the places a fall can be bought at. Ported from the KrakenPlusPlus WPF study's entry-detector
/// family, which the earlier analysis port left behind: each method here is one answer to "when do you
/// buy a falling market?" — a drawdown, a momentum crossing, a band reclaim, a volume climax, a curve
/// turning, a swept level — and <see cref="EntrySignalBacktest"/> replays every one of them over this
/// market's own history so they can be compared rather than believed.
/// </summary>
public static class EntryDetection
{
    // ── Drawdown ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every candle whose close sits at least <paramref name="minimumDrawdownFraction"/> below the
    /// highest high of the trailing window. The plain "buy it once it is down this much" control the
    /// rest of the family has to beat — it carries no view about whether the fall has stopped.
    /// </summary>
    public static List<EntrySignal> Drawdown(
        IReadOnlyList<AnalysisCandle> candles, int lookbackBars, decimal minimumDrawdownFraction, int referenceLowWindowBars)
    {
        var signals = new List<EntrySignal>();
        if (candles == null || lookbackBars < 1 || minimumDrawdownFraction <= 0m || referenceLowWindowBars < 1) return signals;
        var highs = RollingHighestHigh(candles, lookbackBars);
        var lowIndexes = RollingLowestLowIndex(candles, referenceLowWindowBars);

        for (int i = lookbackBars; i < candles.Count; i++)
            if (highs[i] > 0m && candles[i].Close > 0m && 1m - candles[i].Close / highs[i] >= minimumDrawdownFraction)
                signals.Add(Build(candles, i, lowIndexes[i]));
        return signals;
    }

    // ── RSI ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every candle whose relative strength index has just risen back to or above
    /// <paramref name="oversoldLevel"/> from below it — the fall beginning to give ground, one bar
    /// after the reading that says so.
    /// </summary>
    public static List<EntrySignal> RsiReclaim(
        IReadOnlyList<AnalysisCandle> candles, int period, decimal oversoldLevel, int referenceLowWindowBars)
    {
        var signals = new List<EntrySignal>();
        if (candles == null || period < 2 || oversoldLevel <= 0m || referenceLowWindowBars < 1) return signals;
        var rsi = Indicators.RelativeStrengthIndex(candles.Select(c => c.Close).ToList(), period);
        var lowIndexes = RollingLowestLowIndex(candles, referenceLowWindowBars);

        for (int i = period + 1; i < candles.Count; i++)
            if (rsi[i - 1] < oversoldLevel && rsi[i] >= oversoldLevel)
                signals.Add(Build(candles, i, lowIndexes[i]));
        return signals;
    }

    // ── Consecutive down closes ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every candle that closes up after a run of consecutive lower closes long enough, and deep
    /// enough, to count as a fall exhausting itself. The crudest reading of "the selling has stopped",
    /// and the one whose run length is counted rather than calibrated.
    /// </summary>
    public static List<EntrySignal> DownCloseExhaustion(
        IReadOnlyList<AnalysisCandle> candles, int minimumConsecutiveDownCloses, decimal minimumFallFraction, int referenceLowWindowBars)
    {
        var signals = new List<EntrySignal>();
        if (candles == null || minimumConsecutiveDownCloses < 1 || minimumFallFraction < 0m || referenceLowWindowBars < 1) return signals;
        var lowIndexes = RollingLowestLowIndex(candles, referenceLowWindowBars);
        int run = 0, runStart = 0;

        for (int i = 1; i < candles.Count; i++)
        {
            if (candles[i].Close < candles[i - 1].Close)
            {
                if (run == 0) runStart = i - 1;
                run++;
                continue;
            }
            if (candles[i].Close > candles[i - 1].Close && run >= minimumConsecutiveDownCloses && candles[runStart].Close > 0m
                && 1m - candles[i - 1].Close / candles[runStart].Close >= minimumFallFraction)
                signals.Add(Build(candles, i, lowIndexes[i]));
            run = 0;
        }
        return signals;
    }

    // ── MACD line crossing up through its signal ─────────────────────────────────────────────────

    /// <summary>
    /// Every candle at which the MACD line closed above its signal line having closed below it the
    /// candle before, optionally only while the line is still beneath zero (a crossing inside a fall
    /// rather than one continuing a rise) and only once price has already fallen far enough.
    /// </summary>
    public static List<EntrySignal> MacdCrossUp(
        IReadOnlyList<AnalysisCandle> candles, TrendDetectionParameters parameters, bool requireBelowZero,
        decimal minimumPriorFallFraction, int priorFallLookbackBars, int referenceLowWindowBars, bool onlyFirstPerFall = false)
    {
        var signals = new List<EntrySignal>();
        if (candles == null || parameters == null || priorFallLookbackBars < 1 || referenceLowWindowBars < 1) return signals;
        var (macd, signal) = Indicators.MacdLineAndSignal(
            candles.Select(c => c.Close).ToList(),
            parameters.FastPeriod, parameters.SlowPeriod, parameters.SignalPeriod, parameters.UseZeroLagMovingAverages);
        var highestHigh = RollingHighestHigh(candles, priorFallLookbackBars);
        var lowIndexes = RollingLowestLowIndex(candles, referenceLowWindowBars);
        int warmup = Math.Max(parameters.SlowPeriod + parameters.SignalPeriod, priorFallLookbackBars);
        bool armed = true;

        for (int i = warmup; i < candles.Count; i++)
        {
            if (macd[i] < 0m && macd[i - 1] >= 0m) armed = true;
            if (macd[i - 1] >= signal[i - 1] || macd[i] <= signal[i]) continue;
            if (requireBelowZero && macd[i] >= 0m) continue;
            if (onlyFirstPerFall && !armed) continue;
            if (minimumPriorFallFraction > 0m && (highestHigh[i] <= 0m || 1m - candles[i].Low / highestHigh[i] < minimumPriorFallFraction)) continue;
            armed = false;
            signals.Add(Build(candles, i, lowIndexes[i]));
        }
        return signals;
    }

    // ── Bollinger lower-band reclaim ────────────────────────────────────────────────────────────

    /// <summary>
    /// Every candle at which the close has come back inside the lower Bollinger band after closing
    /// outside it — the earliest reading of a fall that has stopped stretching away from its own
    /// average, one bar after the extreme rather than after a percentage rebound.
    /// </summary>
    public static List<EntrySignal> LowerBandReclaim(
        IReadOnlyList<AnalysisCandle> candles, int periodBars, decimal standardDeviationMultiple,
        decimal minimumPriorFallFraction, int priorFallLookbackBars)
    {
        var signals = new List<EntrySignal>();
        if (candles == null || periodBars < 2 || standardDeviationMultiple <= 0m || priorFallLookbackBars < 1) return signals;
        var (_, lower, _, _) = BollingerBands(candles, periodBars, standardDeviationMultiple);
        var highestClose = RollingHighestHigh(candles, priorFallLookbackBars);

        for (int i = Math.Max(periodBars, priorFallLookbackBars) + 1; i < candles.Count; i++)
        {
            if (lower[i] <= 0m || lower[i - 1] <= 0m) continue;
            if (candles[i - 1].Close >= lower[i - 1] || candles[i].Close <= lower[i]) continue;
            if (minimumPriorFallFraction > 0m && (highestClose[i] <= 0m || 1m - candles[i - 1].Low / highestClose[i] < minimumPriorFallFraction)) continue;
            signals.Add(BuildLowestOfWindow(candles, i, periodBars));
        }
        return signals;
    }

    // ── Selling-climax absorption from a wide-range candle ──────────────────────────────────────

    /// <summary>
    /// Every candle that fell violently against its own recent range and then closed high inside
    /// itself, its low then held for <paramref name="followThroughBars"/> candles — the shape of
    /// sellers being met rather than sellers being finished.
    /// </summary>
    public static List<EntrySignal> ClimaxAbsorption(
        IReadOnlyList<AnalysisCandle> candles, decimal[] averageTrueRanges, decimal climaxAtrMultiple,
        decimal minimumCloseLocation, decimal minimumPriorFallFraction, int priorFallLookbackBars, int followThroughBars = 1)
    {
        var signals = new List<EntrySignal>();
        if (candles == null || averageTrueRanges == null || climaxAtrMultiple <= 0m || priorFallLookbackBars < 1 || followThroughBars < 0) return signals;
        var highestHigh = RollingHighestHigh(candles, priorFallLookbackBars);

        for (int i = Math.Max(1, priorFallLookbackBars); i + followThroughBars < candles.Count; i++)
        {
            decimal range = candles[i].High - candles[i].Low, atr = averageTrueRanges[i];
            if (atr <= 0m || range <= 0m || candles[i - 1].Close - candles[i].Low < climaxAtrMultiple * atr) continue;
            if ((candles[i].Close - candles[i].Low) / range < minimumCloseLocation) continue;
            if (minimumPriorFallFraction > 0m && (highestHigh[i] <= 0m || 1m - candles[i].Low / highestHigh[i] < minimumPriorFallFraction)) continue;
            bool held = true;
            for (int j = i + 1; j <= i + followThroughBars && held; j++) held = candles[j].Low >= candles[i].Low;
            if (!held || (followThroughBars > 0 && candles[i + followThroughBars].Close <= candles[i].Close)) continue;
            signals.Add(new EntrySignal(i, candles[i].Low, candles[i].OpenTime, i + followThroughBars, candles[i + followThroughBars].OpenTime));
        }
        return signals;
    }

    // ── Volume: selling climax absorbed ─────────────────────────────────────────────────────────

    /// <summary>
    /// Every candle at which a selling climax has been absorbed: a heavy down candle on unusually
    /// large volume that took out the low of the fall, then <paramref name="absorptionBars"/> candles
    /// that all held above its low.
    /// </summary>
    public static List<EntrySignal> SellingClimax(
        IReadOnlyList<AnalysisCandle> candles, decimal[] trailingMedianVolumes, decimal[] averageTrueRanges,
        decimal volumeSpikeMultiple, decimal minimumRangeAtrMultiple, decimal minimumCloseLocationInRange,
        int absorptionBars, decimal minimumPriorFallFraction, int priorFallLookbackBars)
    {
        var signals = new List<EntrySignal>();
        if (candles == null || trailingMedianVolumes == null || averageTrueRanges == null || volumeSpikeMultiple <= 0m || absorptionBars < 1 || priorFallLookbackBars < 2) return signals;
        var priorHighIndexes = RollingHighestHighIndex(candles, priorFallLookbackBars);
        var priorLowIndexes = RollingLowestLowIndex(candles, priorFallLookbackBars);

        for (int i = priorFallLookbackBars + absorptionBars; i < candles.Count; i++)
        {
            int climaxIndex = i - absorptionBars;
            if (trailingMedianVolumes[climaxIndex] <= 0m || averageTrueRanges[climaxIndex] <= 0m) continue;
            var climax = candles[climaxIndex];
            if (climax.Low <= 0m || climax.High <= climax.Low || climax.Volume < volumeSpikeMultiple * trailingMedianVolumes[climaxIndex]) continue;
            if (climax.High - climax.Low < minimumRangeAtrMultiple * averageTrueRanges[climaxIndex]) continue;
            if ((climax.Close - climax.Low) / (climax.High - climax.Low) < minimumCloseLocationInRange) continue;
            if (priorLowIndexes[climaxIndex] != climaxIndex || candles[priorHighIndexes[climaxIndex]].High / climax.Low - 1m < minimumPriorFallFraction) continue;
            bool absorbed = true;
            for (int j = climaxIndex + 1; j <= i && absorbed; j++) absorbed = candles[j].Low > climax.Low;
            if (absorbed) signals.Add(new EntrySignal(climaxIndex, climax.Low, climax.OpenTime, i, candles[i].OpenTime));
        }
        return signals;
    }

    // ── Volume: dry-up while price stops making lows ────────────────────────────────────────────

    /// <summary>
    /// Every candle at which a fall's volume has dried up while its price has stopped making new lows:
    /// the last <paramref name="quietBars"/> candles traded a fraction of what the pair traded before
    /// them, and their lowest low stands above the low of the fall.
    /// </summary>
    public static List<EntrySignal> VolumeDryUp(
        IReadOnlyList<AnalysisCandle> candles, decimal[] trailingMedianVolumes, decimal maximumQuietVolumeFraction,
        int quietBars, decimal minimumPriorFallFraction, int priorFallLookbackBars)
    {
        var signals = new List<EntrySignal>();
        if (candles == null || trailingMedianVolumes == null || maximumQuietVolumeFraction <= 0m || quietBars < 2 || priorFallLookbackBars < 2) return signals;
        var priorHighIndexes = RollingHighestHighIndex(candles, priorFallLookbackBars);
        var priorLowIndexes = RollingLowestLowIndex(candles, priorFallLookbackBars);
        var quietLowIndexes = RollingLowestLowIndex(candles, quietBars);
        var quietMedianVolumes = ComputeTrailingMedianVolumes(candles, quietBars);

        for (int i = priorFallLookbackBars + quietBars; i < candles.Count - 1; i++)
        {
            int baselineIndex = i - quietBars + 1;
            if (trailingMedianVolumes[baselineIndex] <= 0m) continue;
            if (quietMedianVolumes[i + 1] > maximumQuietVolumeFraction * trailingMedianVolumes[baselineIndex]) continue;
            int lowIndex = priorLowIndexes[i];
            decimal fallLow = candles[lowIndex].Low;
            if (fallLow <= 0m || lowIndex >= i - quietBars + 1 || candles[quietLowIndexes[i]].Low <= fallLow) continue;
            if (candles[priorHighIndexes[i]].High / fallLow - 1m < minimumPriorFallFraction) continue;
            signals.Add(new EntrySignal(lowIndex, fallLow, candles[lowIndex].OpenTime, i, candles[i].OpenTime));
        }
        return signals;
    }

    // ── Volume: reclaim of the fall's own VWAP ──────────────────────────────────────────────────

    /// <summary>
    /// Every candle that has closed back above the volume-weighted average price of the fall it is in,
    /// having closed below it the candle before — the moment the fall's sellers are collectively under
    /// water and its buyers are not.
    /// </summary>
    public static List<EntrySignal> VwapOfFallReclaim(
        IReadOnlyList<AnalysisCandle> candles, int fallLookbackBars, decimal minimumPriorFallFraction)
    {
        var signals = new List<EntrySignal>();
        if (candles == null || fallLookbackBars < 2) return signals;
        var fallHighIndexes = RollingHighestHighIndex(candles, fallLookbackBars);
        var turnovers = new decimal[candles.Count + 1];
        var volumes = new decimal[candles.Count + 1];
        for (int i = 0; i < candles.Count; i++)
        {
            turnovers[i + 1] = turnovers[i] + (candles[i].High + candles[i].Low + candles[i].Close) / 3m * candles[i].Volume;
            volumes[i + 1] = volumes[i] + candles[i].Volume;
        }

        for (int i = fallLookbackBars + 1; i < candles.Count; i++)
        {
            int highIndex = fallHighIndexes[i];
            if (highIndex >= i - 1 || volumes[i + 1] - volumes[highIndex] <= 0m || volumes[i] - volumes[highIndex] <= 0m) continue;
            decimal averagePrice = (turnovers[i + 1] - turnovers[highIndex]) / (volumes[i + 1] - volumes[highIndex]);
            decimal previousAveragePrice = (turnovers[i] - turnovers[highIndex]) / (volumes[i] - volumes[highIndex]);
            if (candles[i].Close <= averagePrice || candles[i - 1].Close > previousAveragePrice) continue;
            int lowIndex = highIndex;
            for (int j = highIndex; j <= i; j++) if (candles[j].Low < candles[lowIndex].Low) lowIndex = j;
            decimal fallLow = candles[lowIndex].Low;
            if (fallLow <= 0m || candles[highIndex].High / fallLow - 1m < minimumPriorFallFraction) continue;
            signals.Add(new EntrySignal(lowIndex, fallLow, candles[lowIndex].OpenTime, i, candles[i].OpenTime));
        }
        return signals;
    }

    // ── Curve geometry: parabola vertex ────────────────────────────────────────────────────────

    /// <summary>
    /// Every candle at which a least-squares parabola fitted to the window of smoothed closes ending
    /// there opens upward strongly enough and places its vertex within the allowed distance of the
    /// candle itself, after a real fall into it — the point where the drop evens out before price
    /// heads back out.
    /// </summary>
    public static List<EntrySignal> CurveVertex(
        IReadOnlyList<AnalysisCandle> candles, int smoothingPeriodBars, bool useZeroLagSmoother, int fitWindowBars,
        decimal minimumCurvatureFraction, decimal minimumPriorFallFraction, int priorFallLookbackBars, int minimumFallBars,
        decimal maximumVertexAgeBars, decimal maximumVertexLeadBars)
    {
        var signals = new List<EntrySignal>();
        if (candles == null || candles.Count == 0 || smoothingPeriodBars < 1 || fitWindowBars < 3 || priorFallLookbackBars < 2 || minimumPriorFallFraction < 0m) return signals;
        var closes = candles.Select(c => c.Close).ToList();
        var smoothed = useZeroLagSmoother
            ? Indicators.ZeroLagExponentialMovingAverage(closes, smoothingPeriodBars)
            : Indicators.ExponentialMovingAverage(closes, smoothingPeriodBars);
        var (peakIndexes, troughIndexes) = RollingExtremeIndexes(smoothed, priorFallLookbackBars);
        int warmup = Math.Max(3 * smoothingPeriodBars, Math.Max(fitWindowBars, priorFallLookbackBars));

        for (int i = warmup; i < candles.Count; i++)
        {
            if (!HasFallenInto(smoothed, peakIndexes[i], troughIndexes[i], minimumPriorFallFraction, minimumFallBars)) continue;
            var (curvatureFraction, vertexOffsetBars, _) = FitParabola(smoothed, i, fitWindowBars);
            if (curvatureFraction < minimumCurvatureFraction || curvatureFraction <= 0m) continue;
            if (vertexOffsetBars > maximumVertexLeadBars || -vertexOffsetBars > maximumVertexAgeBars) continue;
            signals.Add(BuildLowestOfWindow(candles, i, fitWindowBars));
        }
        return signals;
    }

    // ── Structure: a swept swing low, then reclaimed ───────────────────────────────────────────

    /// <summary>
    /// Every candle at which price has traded below a previously confirmed swing low and then closed
    /// back above it — the shape of a level being taken out and rejected rather than broken. The
    /// order rests at the trough itself, so it carries no distance above the low beyond the sweep.
    /// </summary>
    public static List<EntrySignal> LiquiditySweepReclaim(
        IReadOnlyList<AnalysisCandle> candles, IReadOnlyList<TrendPivot> pivots, decimal minimumSweepFraction, int reclaimWindowBars)
    {
        var signals = new List<EntrySignal>();
        if (candles == null || pivots == null || minimumSweepFraction <= 0m || reclaimWindowBars < 1) return signals;

        // A pivot is only usable once confirmed, and FindTrendPivots reports the extreme's own index.
        // The reversal that confirms a low is at least atrPeriod bars of travel away, so registering
        // the level a fixed lag after the extreme keeps it to something a live bot would have drawn.
        const int ConfirmationLagBars = 14;
        var levels = new decimal[candles.Count];
        var troughs = pivots.Where(p => !p.IsHigh && p.Price > 0m).OrderBy(p => p.Index).ToList();
        int next = 0;
        decimal level = 0m;
        for (int i = 0; i < candles.Count; i++)
        {
            while (next < troughs.Count && troughs[next].Index + ConfirmationLagBars <= i) level = troughs[next++].Price;
            levels[i] = level;
        }

        int sweepIndex = -1, lowIndex = -1;
        decimal armed = 0m;
        for (int i = 1; i < candles.Count; i++)
        {
            if (levels[i - 1] != armed) { armed = levels[i - 1]; sweepIndex = -1; lowIndex = -1; }
            if (armed <= 0m) continue;
            if (sweepIndex >= 0 && i - sweepIndex > reclaimWindowBars) { sweepIndex = -1; lowIndex = -1; }
            if (sweepIndex < 0 && candles[i].Low <= armed * (1m - minimumSweepFraction)) { sweepIndex = i; lowIndex = i; }
            if (sweepIndex < 0) continue;
            if (candles[i].Low < candles[lowIndex].Low) lowIndex = i;
            if (candles[i].Close <= armed) continue;
            signals.Add(new EntrySignal(lowIndex, candles[lowIndex].Low, candles[lowIndex].OpenTime, i, candles[i].OpenTime));
            sweepIndex = -1;
            lowIndex = -1;
        }
        return signals;
    }

    // ── Structure: fair-value gap retest ───────────────────────────────────────────────────────

    /// <summary>
    /// Every three-candle upward imbalance — a candle whose low stands clear above the high of the
    /// candle before last — with the untouched lower edge of that gap as the price to rest a bid at.
    /// </summary>
    public static List<EntrySignal> FairValueGapRetest(IReadOnlyList<AnalysisCandle> candles, decimal minimumGapFraction)
    {
        var signals = new List<EntrySignal>();
        if (candles == null || minimumGapFraction <= 0m) return signals;

        for (int i = 2; i < candles.Count; i++)
        {
            decimal edge = candles[i - 2].High;
            if (edge <= 0m || candles[i].Low / edge - 1m < minimumGapFraction) continue;
            signals.Add(new EntrySignal(i - 2, edge, candles[i - 2].OpenTime, i, candles[i].OpenTime));
        }
        return signals;
    }

    // ── Shared helpers ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The trailing median volume of the candles strictly before each index, over the window — the
    /// baseline every "unusual" or "quiet" volume here is judged against. The current candle is left
    /// out of its own baseline, and a median is used because volume is heavy-tailed.
    /// </summary>
    public static decimal[] ComputeTrailingMedianVolumes(IReadOnlyList<AnalysisCandle> candles, int windowBars)
    {
        var medians = new decimal[candles?.Count ?? 0];
        if (candles == null || windowBars < 1) return medians;
        var window = new List<decimal>(windowBars + 1);

        for (int i = 0; i < candles.Count; i++)
        {
            if (i >= windowBars) medians[i] = window.Count % 2 == 1 ? window[window.Count / 2] : (window[window.Count / 2 - 1] + window[window.Count / 2]) / 2m;
            int insertAt = window.BinarySearch(candles[i].Volume);
            window.Insert(insertAt < 0 ? ~insertAt : insertAt, candles[i].Volume);
            if (window.Count <= windowBars) continue;
            int removeAt = window.BinarySearch(candles[i - windowBars].Volume);
            window.RemoveAt(removeAt < 0 ? ~removeAt : removeAt);
        }
        return medians;
    }

    /// <summary>
    /// The Bollinger bands of the closes and the band width as a fraction of the middle. Running sums
    /// are kept in double precision because a variance from sums of squares loses most of its
    /// significant figures to cancellation.
    /// </summary>
    public static (decimal[] Middle, decimal[] Lower, decimal[] Upper, decimal[] WidthFraction) BollingerBands(
        IReadOnlyList<AnalysisCandle> candles, int periodBars, decimal standardDeviationMultiple)
    {
        int count = candles?.Count ?? 0;
        decimal[] middle = new decimal[count], lower = new decimal[count], upper = new decimal[count], width = new decimal[count];
        if (candles == null || periodBars < 2 || standardDeviationMultiple <= 0m) return (middle, lower, upper, width);
        double sum = 0d, squareSum = 0d, multiple = (double)standardDeviationMultiple;

        for (int i = 0; i < count; i++)
        {
            double close = (double)candles[i].Close;
            sum += close;
            squareSum += close * close;
            if (i >= periodBars) { double leaving = (double)candles[i - periodBars].Close; sum -= leaving; squareSum -= leaving * leaving; }
            if (i < periodBars - 1) continue;
            double mean = sum / periodBars, deviation = Math.Sqrt(Math.Max(0d, squareSum / periodBars - mean * mean));
            middle[i] = (decimal)mean;
            lower[i] = (decimal)(mean - multiple * deviation);
            upper[i] = (decimal)(mean + multiple * deviation);
            width[i] = mean <= 0d ? 0m : (decimal)(2d * multiple * deviation / mean);
        }
        return (middle, lower, upper, width);
    }

    /// <summary>
    /// Fits, by least squares, the parabola that best describes the window of smoothed values ending
    /// at <paramref name="index"/>, and reports its shape: the fraction of price the curve gains over
    /// a whole window, the vertex position in bars relative to the judged bar (negative is behind it),
    /// and the fraction of the window's variation the fit explains.
    /// </summary>
    public static (decimal CurvatureFraction, decimal VertexOffsetBars, decimal FitQuality) FitParabola(
        IReadOnlyList<decimal> smoothedValues, int index, int windowBars)
    {
        if (smoothedValues == null || windowBars < 3 || index < windowBars - 1 || index >= smoothedValues.Count || smoothedValues[index] <= 0m) return (0m, 0m, 0m);
        decimal reference = smoothedValues[index], centre = (windowBars - 1) / 2m;
        decimal squareSum = 0m, quarticSum = 0m, valueSum = 0m, firstMomentSum = 0m, secondMomentSum = 0m, squaredValueSum = 0m;

        for (int t = 0; t < windowBars; t++)
        {
            decimal position = t - centre, value = smoothedValues[index - windowBars + 1 + t] / reference - 1m, square = position * position;
            squareSum += square;
            quarticSum += square * square;
            valueSum += value;
            firstMomentSum += position * value;
            secondMomentSum += square * value;
            squaredValueSum += value * value;
        }
        decimal denominator = windowBars * quarticSum - squareSum * squareSum;
        if (denominator <= 0m || squareSum <= 0m) return (0m, 0m, 0m);
        decimal curvature = (windowBars * secondMomentSum - squareSum * valueSum) / denominator, slope = firstMomentSum / squareSum;
        if (curvature <= 0m) return (0m, 0m, 0m);
        decimal constant = (valueSum - curvature * squareSum) / windowBars, totalVariation = squaredValueSum - valueSum * valueSum / windowBars, residualVariation = 0m;

        for (int t = 0; t < windowBars; t++)
        {
            decimal position = t - centre;
            decimal residual = smoothedValues[index - windowBars + 1 + t] / reference - 1m - (curvature * position * position + slope * position + constant);
            residualVariation += residual * residual;
        }
        return (curvature * windowBars * windowBars, -slope / (2m * curvature) - centre, totalVariation <= 0m ? 0m : Math.Max(0m, 1m - residualVariation / totalVariation));
    }

    private static bool HasFallenInto(decimal[] smoothed, int peakIndex, int troughIndex, decimal minimumFallFraction, int minimumFallBars) =>
        peakIndex < troughIndex && troughIndex - peakIndex >= minimumFallBars && smoothed[peakIndex] > 0m
        && 1m - smoothed[troughIndex] / smoothed[peakIndex] >= minimumFallFraction;

    private static (int[] PeakIndexes, int[] TroughIndexes) RollingExtremeIndexes(decimal[] values, int windowBars)
    {
        var peaks = new int[values.Length];
        var troughs = new int[values.Length];
        var peakQueue = new LinkedList<int>();
        var troughQueue = new LinkedList<int>();

        for (int i = 0; i < values.Length; i++)
        {
            while (peakQueue.Count > 0 && values[peakQueue.Last!.Value] < values[i]) peakQueue.RemoveLast();
            peakQueue.AddLast(i);
            if (peakQueue.First!.Value <= i - windowBars) peakQueue.RemoveFirst();
            while (troughQueue.Count > 0 && values[troughQueue.Last!.Value] >= values[i]) troughQueue.RemoveLast();
            troughQueue.AddLast(i);
            if (troughQueue.First!.Value <= i - windowBars) troughQueue.RemoveFirst();
            peaks[i] = peakQueue.First!.Value;
            troughs[i] = troughQueue.First!.Value;
        }
        return (peaks, troughs);
    }

    /// <summary>Index of the candle holding the highest high over the window ending at each index.</summary>
    public static int[] RollingHighestHighIndex(IReadOnlyList<AnalysisCandle> candles, int windowBars)
    {
        var indexes = new int[candles?.Count ?? 0];
        if (candles == null || windowBars < 1) return indexes;
        var candidates = new LinkedList<int>();
        for (int i = 0; i < candles.Count; i++)
        {
            while (candidates.Count > 0 && candles[candidates.Last!.Value].High <= candles[i].High) candidates.RemoveLast();
            candidates.AddLast(i);
            if (candidates.First!.Value <= i - windowBars) candidates.RemoveFirst();
            indexes[i] = candidates.First!.Value;
        }
        return indexes;
    }

    /// <summary>Index of the candle holding the lowest low over the window ending at each index.</summary>
    public static int[] RollingLowestLowIndex(IReadOnlyList<AnalysisCandle> candles, int windowBars)
    {
        var indexes = new int[candles?.Count ?? 0];
        if (candles == null || windowBars < 1) return indexes;
        var candidates = new LinkedList<int>();
        for (int i = 0; i < candles.Count; i++)
        {
            while (candidates.Count > 0 && candles[candidates.Last!.Value].Low >= candles[i].Low) candidates.RemoveLast();
            candidates.AddLast(i);
            if (candidates.First!.Value <= i - windowBars) candidates.RemoveFirst();
            indexes[i] = candidates.First!.Value;
        }
        return indexes;
    }

    /// <summary>Highest high over the window ending at each index, zero until the window has filled.</summary>
    public static decimal[] RollingHighestHigh(IReadOnlyList<AnalysisCandle> candles, int windowBars)
    {
        int count = candles?.Count ?? 0;
        var result = new decimal[count];
        var indexes = RollingHighestHighIndex(candles!, windowBars);
        for (int i = windowBars - 1; i < count; i++) result[i] = candles![indexes[i]].High;
        return result;
    }

    private static EntrySignal Build(IReadOnlyList<AnalysisCandle> candles, int confirmationIndex, int lowIndex) =>
        new(lowIndex, candles[lowIndex].Low, candles[lowIndex].OpenTime, confirmationIndex, candles[confirmationIndex].OpenTime);

    private static EntrySignal BuildLowestOfWindow(IReadOnlyList<AnalysisCandle> candles, int confirmationIndex, int lowWindowBars)
    {
        int lowIndex = confirmationIndex;
        for (int j = Math.Max(0, confirmationIndex - Math.Max(1, lowWindowBars) + 1); j <= confirmationIndex; j++)
            if (candles[j].Low < candles[lowIndex].Low) lowIndex = j;
        return Build(candles, confirmationIndex, lowIndex);
    }
}
