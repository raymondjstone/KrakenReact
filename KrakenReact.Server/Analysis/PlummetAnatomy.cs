namespace KrakenReact.Server.Analysis;

/// <summary>Ways of asking whether a price is still climbing.</summary>
public enum RisingTest
{
    None,
    HigherThanTheWindowStart,
    MakingHigherLows,
    AboveTheWindowMean,
    NewHighWithinTheWindow,
    TheLineOfBestFitClimbs,
    TheLineOfBestFitClimbsFastEnough,
    TheHighestHighCameAfterTheLowestLow,
}

/// <summary>Windows the anatomy readings are measured over.</summary>
public sealed record PlummetAnatomyParameters
{
    public static PlummetAnatomyParameters Default { get; } = new();

    /// <summary>Bars immediately before the high that are excluded from the quiet baseline.</summary>
    public int ApproachCharacterWindowMinutes { get; init; } = 240;

    /// <summary>How often a reading is taken while watching for the bottom.</summary>
    public int BottomSampleEveryMinutes { get; init; } = 5;

    /// <summary>How long after the trigger the watch continues.</summary>
    public int BottomSampleForMinutes { get; init; } = 1440;
}

/// <summary>
/// One reading taken while a fall is still going, asking whether it has finished falling.
/// <para>
/// Every field is measurable from candles up to and including the one being judged. That is what
/// lets the same reading be taken live, on a fall in progress, as was taken on history when the
/// weights were fitted — a reading that quietly needed the future would fit beautifully and then be
/// useless the moment it mattered.
/// </para>
/// </summary>
public sealed class PlummetBottomObservation
{
    public DateTime ObservedAt { get; set; }
    public int MinutesSinceTheTrigger { get; set; }
    public decimal FallenBelowTheTriggerInDrops { get; set; }
    public decimal AboveTheRunningLowInRanges { get; set; }
    public int MinutesSinceTheLastNewLow { get; set; }
    public decimal RecentVolumeMultiple { get; set; }
    public decimal SlopeRangesPerHour { get; set; }
    public bool IsMakingHigherLows { get; set; }
    public bool TheHighCameAfterTheLow { get; set; }
    public decimal FallAgainstTheBaselineRange { get; set; }

    /// <summary>How much further it fell after this reading. Hindsight — for scoring only, never as input.</summary>
    public decimal FurtherFallInDrops { get; set; }

    /// <summary>Whether the forward window was long enough to settle the outcome above.</summary>
    public bool HasOutcome { get; set; }
}

/// <summary>
/// One reading taken while a rebound is running, asking whether it is still alive.
/// The same forward-only discipline applies as for <see cref="PlummetBottomObservation"/>.
/// </summary>
public sealed class PlummetRiseObservation
{
    public DateTime ObservedAt { get; set; }
    public decimal GainSoFarInDrops { get; set; }
    public decimal DrawdownInRanges { get; set; }
    public decimal DrawdownShareOfTheGain { get; set; }
    public int MinutesSinceThePeak { get; set; }
    public decimal DeepestFallAlreadySurvivedInRanges { get; set; }
    public bool IsMakingHigherLows { get; set; }
    public bool TheHighCameAfterTheLow { get; set; }
    public decimal SlopeRangesPerHourOverHalfAnHour { get; set; }
    public decimal SlopeRangesPerHourOverTwoHours { get; set; }
    public decimal RecentVolumeMultiple { get; set; }
    public decimal RangeFractionOfPrice { get; set; }

    /// <summary>Whether a new peak followed. Hindsight — for scoring only, never as input.</summary>
    public bool MadeANewPeakAfterwards { get; set; }

    public bool HasOutcome { get; set; }
}

/// <summary>
/// Reads the shape of a fall as it happens: how far below the trigger price has gone, how long since
/// the last new low, whether higher lows are forming, how briskly it is trading.
/// <para>
/// This is the live decision path from the source study — the readings a rule needs while a fall is
/// in progress. The research half that produced the fitted weights (descent-shape classification,
/// envelopes, market-condition voting, trade replay) is deliberately not carried across: it exists to
/// run experiments offline, not to make a decision now.
/// </para>
/// <para>
/// <b>Calibrated on one-minute candles.</b> The windows are expressed in minutes and divided by the
/// bar interval, so coarser bars collapse them to one or two bars and the readings stop meaning what
/// the fitted weights expect.
/// </para>
/// </summary>
public static class PlummetAnatomy
{
    /// <summary>Whether a bar actually traded, so untraded bars do not drag a volume average down.</summary>
    public static bool IsTraded(AnalysisCandle candle) => candle.TradeCount > 0 || candle.Volume > 0m;

    /// <summary>The index of the last candle opening at or before the time, or -1.</summary>
    public static int FindIndexAt(IReadOnlyList<AnalysisCandle> candles, DateTime time)
    {
        int low = 0, high = candles.Count - 1, found = -1;
        while (low <= high)
        {
            int mid = (low + high) / 2;
            if (candles[mid].OpenTime <= time) { found = mid; low = mid + 1; }
            else high = mid - 1;
        }
        return found;
    }

    /// <summary>
    /// The slope of a least-squares line through the closes, per minute. Reading a trend from a fitted
    /// line rather than from two endpoints stops a single spike at either end deciding the answer.
    /// </summary>
    public static decimal ClosingSlopePerMinute(IReadOnlyList<AnalysisCandle> candles, int fromIndex, int toIndex, int intervalMinutes)
    {
        if (intervalMinutes <= 0) return 0m;
        int from = Math.Max(0, fromIndex), to = Math.Min(candles.Count - 1, toIndex);
        int count = to - from + 1;
        if (count < 2) return 0m;

        decimal sumX = 0m, sumY = 0m, sumXY = 0m, sumXX = 0m;
        for (int i = 0; i < count; i++)
        {
            decimal x = i, y = candles[from + i].Close;
            sumX += x;
            sumY += y;
            sumXY += x * y;
            sumXX += x * x;
        }
        decimal denominator = count * sumXX - sumX * sumX;
        return denominator == 0m ? 0m : (count * sumXY - sumX * sumY) / denominator / intervalMinutes;
    }

    /// <summary>Applies one of the rising tests at an index.</summary>
    public static bool IsStillRising(
        IReadOnlyList<AnalysisCandle> candles, int index, int peakIndex, RisingTest test,
        int windowMinutes, decimal slopeRangesPerHour, int intervalMinutes,
        IReadOnlyList<decimal>? averageTrueRange = null)
    {
        if (test == RisingTest.None) return false;
        int window = Math.Max(1, windowMinutes / Math.Max(1, intervalMinutes));
        if (index - window < 0 || index >= candles.Count) return false;

        switch (test)
        {
            case RisingTest.TheLineOfBestFitClimbs:
                return ClosingSlopePerMinute(candles, index - window, index, intervalMinutes) > 0m;

            case RisingTest.TheLineOfBestFitClimbsFastEnough:
            {
                decimal range = averageTrueRange is not null && index < averageTrueRange.Count ? averageTrueRange[index] : 0m;
                if (range <= 0m) return false;
                return ClosingSlopePerMinute(candles, index - window, index, intervalMinutes) * 60m / range > slopeRangesPerHour;
            }

            case RisingTest.HigherThanTheWindowStart:
                return candles[index].Close > candles[index - window].Close;

            case RisingTest.MakingHigherLows:
            {
                // Two adjacent windows are compared rather than two individual bars, so one spike low
                // cannot decide it.
                if (index - 2 * window < 0) return false;
                decimal recent = candles[index].Low, earlier = candles[index - window].Low;
                for (int j = index - window + 1; j <= index; j++) if (candles[j].Low < recent) recent = candles[j].Low;
                for (int j = index - 2 * window + 1; j <= index - window; j++) if (candles[j].Low < earlier) earlier = candles[j].Low;
                return recent > earlier;
            }

            case RisingTest.AboveTheWindowMean:
            {
                decimal sum = 0m;
                for (int j = index - window + 1; j <= index; j++) sum += candles[j].Close;
                return candles[index].Close > sum / window;
            }

            case RisingTest.NewHighWithinTheWindow:
                return index - peakIndex < window;

            case RisingTest.TheHighestHighCameAfterTheLowestLow:
            {
                // The later of the two wins ties: scanning forward and keeping the last bar to equal
                // the extreme credits a flat stretch to its end, so the reading does not turn on a tie
                // struck long ago.
                int highestIndex = index - window, lowestIndex = index - window;
                for (int j = index - window; j <= index; j++)
                {
                    if (candles[j].High >= candles[highestIndex].High) highestIndex = j;
                    if (candles[j].Low <= candles[lowestIndex].Low) lowestIndex = j;
                }
                return highestIndex > lowestIndex;
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// The mean traded volume over the quiet stretch before a fall, excluding the approach window.
    /// The approach is cut out because volume usually builds there, and including it would flatter
    /// the baseline that the fall's own volume is later compared against.
    /// </summary>
    public static decimal BaselineMeanVolume(
        IReadOnlyList<AnalysisCandle> candles, int highIndex,
        PlummetStudyParameters studyParameters, PlummetAnatomyParameters anatomyParameters, int intervalMinutes)
    {
        if (highIndex < 0 || intervalMinutes <= 0) return 0m;

        int approachCandles = Math.Max(1, anatomyParameters.ApproachCharacterWindowMinutes / intervalMinutes);
        int baselineCandles = Math.Max(1, studyParameters.BaselineWindowMinutes / intervalMinutes);
        int baselineEnd = highIndex - approachCandles - 1;
        int baselineStart = baselineEnd - baselineCandles + 1;

        decimal volume = 0m;
        int counted = 0;
        for (int b = Math.Max(0, baselineStart); b <= baselineEnd && b < candles.Count; b++)
            if (IsTraded(candles[b])) { volume += candles[b].Volume; counted++; }

        return counted > 0 ? volume / counted : 0m;
    }

    /// <summary>The baseline volume for a detected fall.</summary>
    public static decimal BaselineVolumeFor(
        IReadOnlyList<AnalysisCandle> candles, PlummetEvent plummetEvent, int intervalMinutes,
        PlummetStudyParameters studyParameters, PlummetAnatomyParameters anatomyParameters) =>
        BaselineMeanVolume(candles, FindIndexAt(candles, plummetEvent.ReferenceHighTime), studyParameters, anatomyParameters, intervalMinutes);

    /// <summary>
    /// Takes readings at intervals from the trigger onwards, each asking whether the fall has finished.
    /// </summary>
    /// <param name="horizonMinutes">
    /// How far ahead to look when recording what actually happened. Pass zero for a live reading: it
    /// leaves the outcome unset, which is exactly the position a rule is in when it has to decide.
    /// </param>
    public static List<PlummetBottomObservation> BuildBottomObservations(
        IReadOnlyList<AnalysisCandle> candles,
        PlummetEvent plummetEvent,
        int intervalMinutes,
        IReadOnlyList<decimal>? averageTrueRange,
        decimal baselineMeanVolume,
        decimal baselineRangeFraction,
        PlummetAnatomyParameters anatomyParameters,
        int horizonMinutes)
    {
        var observations = new List<PlummetBottomObservation>();
        if (candles is null || plummetEvent is null || intervalMinutes <= 0) return observations;

        int triggerIndex = FindIndexAt(candles, plummetEvent.TriggerTime);
        if (triggerIndex < 0) return observations;

        int step = Math.Max(1, anatomyParameters.BottomSampleEveryMinutes / intervalMinutes);
        int horizon = horizonMinutes > 0 ? Math.Max(1, horizonMinutes / intervalMinutes) : 0;
        int last = Math.Min(candles.Count - 1 - horizon, triggerIndex + anatomyParameters.BottomSampleForMinutes / intervalMinutes);

        decimal triggerClose = candles[triggerIndex].Close;
        decimal runningLow = candles[triggerIndex].Low;
        int lastNewLowIndex = triggerIndex;
        int halfHour = Math.Max(1, 30 / intervalMinutes);
        int quarterHour = Math.Max(1, 15 / intervalMinutes);

        for (int j = triggerIndex; j <= last; j++)
        {
            // The running low advances every bar, not only on sampled bars, or the "minutes since the
            // last new low" reading would be quantised to the sampling cadence.
            if (candles[j].Low < runningLow) { runningLow = candles[j].Low; lastNewLowIndex = j; }
            if ((j - triggerIndex) % step != 0) continue;

            decimal dropSoFar = plummetEvent.ReferenceHigh - runningLow;
            decimal range = averageTrueRange is not null && j < averageTrueRange.Count ? averageTrueRange[j] : 0m;
            if (dropSoFar <= 0m || range <= 0m) continue;

            decimal close = candles[j].Close, lowestAhead = close;
            for (int k = j + 1; k <= j + horizon && k < candles.Count; k++)
                if (candles[k].Low < lowestAhead) lowestAhead = candles[k].Low;

            decimal volume = 0m;
            int counted = 0;
            for (int v = Math.Max(0, j - quarterHour + 1); v <= j; v++)
                if (IsTraded(candles[v])) { volume += candles[v].Volume; counted++; }

            observations.Add(new PlummetBottomObservation
            {
                ObservedAt = candles[j].OpenTime,
                MinutesSinceTheTrigger = (j - triggerIndex) * intervalMinutes,
                FallenBelowTheTriggerInDrops = (triggerClose - close) / dropSoFar,
                AboveTheRunningLowInRanges = (close - runningLow) / range,
                MinutesSinceTheLastNewLow = (j - lastNewLowIndex) * intervalMinutes,
                RecentVolumeMultiple = counted > 0 && baselineMeanVolume > 0m ? volume / counted / baselineMeanVolume : 0m,
                SlopeRangesPerHour = ClosingSlopePerMinute(candles, j - halfHour, j, intervalMinutes) * 60m / range,
                IsMakingHigherLows = IsStillRising(candles, j, j, RisingTest.MakingHigherLows, 15, 0m, intervalMinutes, averageTrueRange),
                TheHighCameAfterTheLow = IsStillRising(candles, j, j, RisingTest.TheHighestHighCameAfterTheLowestLow, 30, 0m, intervalMinutes, averageTrueRange),
                FallAgainstTheBaselineRange = baselineRangeFraction > 0m && plummetEvent.ReferenceHigh > 0m
                    ? dropSoFar / plummetEvent.ReferenceHigh / baselineRangeFraction
                    : 0m,
                FurtherFallInDrops = horizon > 0 ? (close - lowestAhead) / dropSoFar : 0m,
                HasOutcome = horizon > 0,
            });
        }
        return observations;
    }

    /// <summary>Takes one reading of a rebound in progress, asking whether it is still alive.</summary>
    public static PlummetRiseObservation ReadTheRise(
        IReadOnlyList<AnalysisCandle> candles, int index, decimal peak, int peakIndex,
        decimal referenceLow, decimal dropDistance, decimal deepestSurvivedInRanges,
        decimal range, decimal baselineMeanVolume, int intervalMinutes,
        IReadOnlyList<decimal>? averageTrueRange)
    {
        decimal price = candles[index].Close;
        decimal volume = 0m;
        int counted = 0;
        int halfHour = Math.Max(1, 30 / intervalMinutes);
        int quarterHour = Math.Max(1, 15 / intervalMinutes);

        for (int v = Math.Max(0, index - quarterHour + 1); v <= index; v++)
            if (IsTraded(candles[v])) { volume += candles[v].Volume; counted++; }

        return new PlummetRiseObservation
        {
            ObservedAt = candles[index].OpenTime,
            GainSoFarInDrops = dropDistance > 0m ? (peak - referenceLow) / dropDistance : 0m,
            DrawdownInRanges = range > 0m ? (peak - price) / range : 0m,
            DrawdownShareOfTheGain = peak > referenceLow ? (peak - price) / (peak - referenceLow) : 0m,
            MinutesSinceThePeak = (index - peakIndex) * intervalMinutes,
            DeepestFallAlreadySurvivedInRanges = deepestSurvivedInRanges,
            IsMakingHigherLows = IsStillRising(candles, index, peakIndex, RisingTest.MakingHigherLows, 30, 0m, intervalMinutes, averageTrueRange),
            TheHighCameAfterTheLow = IsStillRising(candles, index, peakIndex, RisingTest.TheHighestHighCameAfterTheLowestLow, 30, 0m, intervalMinutes, averageTrueRange),
            SlopeRangesPerHourOverHalfAnHour = range > 0m ? ClosingSlopePerMinute(candles, index - halfHour, index, intervalMinutes) * 60m / range : 0m,
            SlopeRangesPerHourOverTwoHours = range > 0m ? ClosingSlopePerMinute(candles, index - 4 * halfHour, index, intervalMinutes) * 60m / range : 0m,
            RecentVolumeMultiple = counted > 0 && baselineMeanVolume > 0m ? volume / counted / baselineMeanVolume : 0m,
            RangeFractionOfPrice = price > 0m ? range / price : 0m,
        };
    }

    /// <summary>
    /// Walks a rebound from the low, taking a reading each bar and recording whether a new peak
    /// followed — the outcome the rise judge is fitted against.
    /// </summary>
    public static List<PlummetRiseObservation> BuildRiseObservations(
        IReadOnlyList<AnalysisCandle> candles,
        PlummetEvent plummetEvent,
        int intervalMinutes,
        IReadOnlyList<decimal>? averageTrueRange,
        decimal baselineMeanVolume,
        int watchForMinutes = 4320)
    {
        var observations = new List<PlummetRiseObservation>();
        if (candles is null || plummetEvent is null || intervalMinutes <= 0) return observations;

        int lowIndex = FindIndexAt(candles, plummetEvent.EventLowTime);
        if (lowIndex < 0) return observations;

        int last = Math.Min(candles.Count - 1, lowIndex + watchForMinutes / intervalMinutes);
        decimal referenceLow = plummetEvent.EventLow;
        decimal dropDistance = plummetEvent.ReferenceHigh - referenceLow;
        if (dropDistance <= 0m) return observations;

        decimal peak = candles[lowIndex].High;
        int peakIndex = lowIndex;
        decimal deepestSurvivedInRanges = 0m;

        for (int j = lowIndex + 1; j <= last; j++)
        {
            decimal range = averageTrueRange is not null && j < averageTrueRange.Count ? averageTrueRange[j] : 0m;
            if (range <= 0m) continue;

            if (candles[j].High > peak) { peak = candles[j].High; peakIndex = j; }

            // The deepest pullback already lived through is what a later drawdown is judged against:
            // a fall this rebound has already survived once says less than a fall it never has.
            decimal drawdownInRanges = (peak - candles[j].Low) / range;
            if (drawdownInRanges > deepestSurvivedInRanges) deepestSurvivedInRanges = drawdownInRanges;

            var observation = ReadTheRise(
                candles, j, peak, peakIndex, referenceLow, dropDistance,
                deepestSurvivedInRanges, range, baselineMeanVolume, intervalMinutes, averageTrueRange);

            // Whether a higher peak ever came afterwards — hindsight, recorded for fitting only.
            decimal peakAtReading = peak;
            bool newPeakFollowed = false;
            for (int k = j + 1; k <= last; k++)
                if (candles[k].High > peakAtReading) { newPeakFollowed = true; break; }

            observation.MadeANewPeakAfterwards = newPeakFollowed;
            observation.HasOutcome = j < last;
            observations.Add(observation);
        }
        return observations;
    }
}
