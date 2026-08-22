namespace KrakenReact.Server.Analysis;

/// <summary>Thresholds governing what counts as a surge and how long its reclaim is followed.</summary>
public class SurgeStudyParameters
{
    public static SurgeStudyParameters Default { get; } = new();

    /// <summary>
    /// The windows a surge may be measured over. A rise is a surge at whichever scale it clears the
    /// bar on, so a slow grind over three days and a violent quarter-hour both get found.
    /// </summary>
    public int[] SurgeWindowsMinutes { get; set; } = [15, 60, 240, 1440, 4320];

    public decimal MinimumSurgeFraction { get; set; } = 0.10m;

    /// <summary>Multiple of the baseline range the rise must also clear, relative to how quiet the market was.</summary>
    public decimal QuietnessMultiple { get; set; } = 2.0m;

    /// <summary>The retrace off the peak that qualifies the surge as having broken down.</summary>
    public decimal CrashRetraceMinimumFraction { get; set; } = 0.25m;

    /// <summary>How many times the surge's own duration the breakdown is allowed to take.</summary>
    public decimal CrashSpeedMultiple { get; set; } = 2.0m;

    public int TrendWindowMinutes { get; set; } = 1440;
    public int ReclaimHorizonMinutes { get; set; } = 4320;

    /// <summary>Quiet period after an event before another may be detected; zero uses the reclaim horizon.</summary>
    public int DetectionMemoryMinutes { get; set; }
}

/// <summary>One detected surge, its breakdown, and whether the peak was ever reclaimed.</summary>
public class SurgeEvent
{
    public int SurgeWindowMinutes { get; set; }
    public DateTime SurgeLowTime { get; set; }
    public decimal SurgeLow { get; set; }
    public DateTime SurgePeakTime { get; set; }
    public decimal SurgePeak { get; set; }
    public decimal SurgeRiseFraction { get; set; }
    public decimal RequiredRiseFraction { get; set; }
    public decimal BaselineRangeFraction { get; set; }
    public int SurgeDurationMinutes { get; set; }

    /// <summary>Whether the peak was a lone wick rather than a level the market actually traded at.</summary>
    public bool IsWick { get; set; }

    public int PeakTradeCount { get; set; }
    public decimal PeakVolumeRatio { get; set; }

    public DateTime CrashQualificationTime { get; set; }
    public int CrashDurationMinutes { get; set; }
    public decimal CrashLow { get; set; }
    public DateTime CrashLowTime { get; set; }
    public decimal RetraceFractionReached { get; set; }

    public int? MinutesToReclaim { get; set; }
    public DateTime? ReclaimTime { get; set; }
    public bool IsReclaimed => MinutesToReclaim != null;

    public decimal MaximumRiseAbovePeakFraction { get; set; }
    public decimal SurgeSpeedFractionPerHour { get; set; }
    public decimal? ReclaimSpeedFractionPerHour { get; set; }

    public decimal? TrendChangeFraction { get; set; }
    public decimal? ShortTrendChangeFraction { get; set; }
    public decimal? LongTrendChangeFraction { get; set; }

    public int PriorEventCount { get; set; }
    public int PriorReclaimedCount { get; set; }
}

/// <summary>A surge that has not yet broken down, as it stands at the end of the series.</summary>
public class SurgeInProgress
{
    public decimal SurgeLow { get; set; }
    public DateTime SurgeLowTime { get; set; }
    public decimal SurgePeak { get; set; }
    public DateTime SurgePeakTime { get; set; }
    public decimal SurgeRiseFraction { get; set; }
    public int SurgeDurationMinutes { get; set; }

    /// <summary>Whether the price has already turned down off the peak.</summary>
    public bool IsCrashing { get; set; }
}

/// <summary>
/// Finds sharp rises that then broke down, and measures whether the peak was reclaimed. The mirror
/// of <see cref="PlummetDetection"/>: it turns "the price spiked" into the question history can
/// answer, which is whether a spike of this shape in this market ever gets back to its high.
/// </summary>
public static class SurgeDetection
{
    /// <summary>Detects every surge across all configured windows, deduplicated and oldest first.</summary>
    public static List<SurgeEvent> DetectSurgeEvents(IReadOnlyList<AnalysisCandle> candles, int intervalMinutes, SurgeStudyParameters parameters)
    {
        var events = new List<SurgeEvent>();
        if (candles == null || candles.Count == 0 || intervalMinutes <= 0) return events;

        var volumePrefix = new decimal[candles.Count + 1];
        for (int c = 0; c < candles.Count; c++) volumePrefix[c + 1] = volumePrefix[c] + candles[c].Volume;

        foreach (int windowMinutes in parameters.SurgeWindowsMinutes.Distinct())
        {
            int windowCandles = windowMinutes / intervalMinutes;
            if (windowCandles >= 1) DetectSurgeEventsForWindow(candles, intervalMinutes, windowCandles, windowMinutes, volumePrefix, parameters, events);
        }

        var deduplicated = DeduplicateOverlappingEvents(events);
        AttachEventHistory(deduplicated);
        return deduplicated;
    }

    /// <summary>The strongest surge still live at the end of the series, or null when there is none.</summary>
    public static SurgeInProgress? DetectSurgeInProgress(IReadOnlyList<AnalysisCandle> candles, int intervalMinutes, SurgeStudyParameters parameters)
    {
        if (candles == null || candles.Count == 0 || intervalMinutes <= 0) return null;

        SurgeInProgress? best = null;
        foreach (int windowMinutes in parameters.SurgeWindowsMinutes.Distinct())
        {
            int windowCandles = windowMinutes / intervalMinutes;
            if (windowCandles < 1 || candles.Count <= 2 * windowCandles) continue;
            var candidate = DetectSurgeInProgressForWindow(candles, intervalMinutes, windowCandles, parameters);
            if (candidate != null && (best == null || candidate.SurgeRiseFraction > best.SurgeRiseFraction)) best = candidate;
        }
        return best;
    }

    private static void DetectSurgeEventsForWindow(
        IReadOnlyList<AnalysisCandle> candles, int intervalMinutes, int windowCandles, int windowMinutes,
        decimal[] volumePrefix, SurgeStudyParameters parameters, List<SurgeEvent> events)
    {
        int count = candles.Count;
        if (count <= 2 * windowCandles) return;

        int[] trailingLowIndices = ComputeSlidingMinimumLowIndices(candles, windowCandles + 1);
        var (baselineLows, baselineHighs) = ComputeSlidingBaselineExtremes(candles, windowCandles);
        int horizonCandles = Math.Max(1, parameters.ReclaimHorizonMinutes / intervalMinutes);
        int memoryCandles = parameters.DetectionMemoryMinutes > 0 ? Math.Max(1, parameters.DetectionMemoryMinutes / intervalMinutes) : horizonCandles;

        int i = windowCandles;
        while (i < count)
        {
            int lowIndex = trailingLowIndices[i];
            decimal low = candles[lowIndex].Low;
            if (lowIndex < windowCandles || low <= 0m) { i++; continue; }

            decimal baselineLow = baselineLows[lowIndex - 1], baselineHigh = baselineHighs[lowIndex - 1];
            if (baselineLow <= 0m) { i++; continue; }
            decimal baselineRangeFraction = (baselineHigh - baselineLow) / baselineLow;
            decimal requiredRiseFraction = Math.Max(parameters.MinimumSurgeFraction, parameters.QuietnessMultiple * baselineRangeFraction);
            if ((candles[i].High - low) / low < requiredRiseFraction) { i++; continue; }

            // Walk forward for the peak and the retrace that qualifies the breakdown, allowing the
            // peak to extend while the rise is still running.
            int peakIndex = i;
            decimal peak = candles[i].High;
            decimal retraceLevel = peak - parameters.CrashRetraceMinimumFraction * (peak - low);
            int crashWindowCandles = ComputeCrashWindowCandles(peakIndex, lowIndex, parameters);
            int qualificationIndex = candles[i].Close <= retraceLevel ? i : -1;

            for (int j = i + 1; qualificationIndex < 0 && j < count; j++)
            {
                if (j <= peakIndex + windowCandles && candles[j].High > peak)
                {
                    peak = candles[j].High;
                    peakIndex = j;
                    retraceLevel = peak - parameters.CrashRetraceMinimumFraction * (peak - low);
                    crashWindowCandles = ComputeCrashWindowCandles(peakIndex, lowIndex, parameters);
                    if (candles[j].Close <= retraceLevel) qualificationIndex = j;
                }
                else
                {
                    if (j > peakIndex + crashWindowCandles) break;
                    if (candles[j].Low <= retraceLevel) qualificationIndex = j;
                }
            }
            if (qualificationIndex < 0) { i = peakIndex + crashWindowCandles + 1; continue; }

            events.Add(BuildSurgeEvent(candles, intervalMinutes, windowCandles, windowMinutes, lowIndex, peakIndex, peak,
                                       qualificationIndex, horizonCandles, requiredRiseFraction, baselineRangeFraction, volumePrefix, parameters));
            i = qualificationIndex + memoryCandles + 1;
        }
    }

    private static SurgeInProgress? DetectSurgeInProgressForWindow(
        IReadOnlyList<AnalysisCandle> candles, int intervalMinutes, int windowCandles, SurgeStudyParameters parameters)
    {
        int count = candles.Count, last = count - 1;
        int[] trailingLowIndices = ComputeSlidingMinimumLowIndices(candles, windowCandles + 1);
        var (baselineLows, baselineHighs) = ComputeSlidingBaselineExtremes(candles, windowCandles);
        int earliestTrigger = Math.Max(windowCandles, last - 3 * windowCandles);

        for (int i = earliestTrigger; i <= last; i++)
        {
            int lowIndex = trailingLowIndices[i];
            decimal low = candles[lowIndex].Low;
            if (lowIndex < windowCandles || low <= 0m) continue;

            decimal baselineLow = baselineLows[lowIndex - 1], baselineHigh = baselineHighs[lowIndex - 1];
            if (baselineLow <= 0m) continue;
            decimal requiredRiseFraction = Math.Max(parameters.MinimumSurgeFraction, parameters.QuietnessMultiple * (baselineHigh - baselineLow) / baselineLow);
            if ((candles[i].High - low) / low < requiredRiseFraction) continue;

            int peakIndex = i;
            decimal peak = candles[i].High;
            decimal retraceLevel = peak - parameters.CrashRetraceMinimumFraction * (peak - low);
            int crashWindowCandles = ComputeCrashWindowCandles(peakIndex, lowIndex, parameters);
            bool qualified = candles[i].Close <= retraceLevel;

            for (int j = i + 1; !qualified && j <= last; j++)
            {
                if (j <= peakIndex + windowCandles && candles[j].High > peak)
                {
                    peak = candles[j].High;
                    peakIndex = j;
                    retraceLevel = peak - parameters.CrashRetraceMinimumFraction * (peak - low);
                    crashWindowCandles = ComputeCrashWindowCandles(peakIndex, lowIndex, parameters);
                    qualified = candles[j].Close <= retraceLevel;
                }
                else if (j > peakIndex + crashWindowCandles) break;
                else qualified = candles[j].Low <= retraceLevel;
            }

            // A surge that has already qualified is history, not in progress; one whose breakdown
            // window has run out without qualifying never broke down at all.
            if (qualified || last > peakIndex + crashWindowCandles) continue;

            return new SurgeInProgress
            {
                SurgeLow = low,
                SurgeLowTime = candles[lowIndex].OpenTime,
                SurgePeak = peak,
                SurgePeakTime = candles[peakIndex].OpenTime,
                SurgeRiseFraction = (peak - low) / low,
                SurgeDurationMinutes = Math.Max(1, peakIndex - lowIndex) * intervalMinutes,
                IsCrashing = peakIndex < last
            };
        }
        return null;
    }

    private static int ComputeCrashWindowCandles(int peakIndex, int lowIndex, SurgeStudyParameters parameters) =>
        Math.Max(1, (int)Math.Ceiling(parameters.CrashSpeedMultiple * Math.Max(1, peakIndex - lowIndex)));

    private static SurgeEvent BuildSurgeEvent(
        IReadOnlyList<AnalysisCandle> candles, int intervalMinutes, int windowCandles, int windowMinutes,
        int lowIndex, int peakIndex, decimal peak, int qualificationIndex, int horizonCandles,
        decimal requiredRiseFraction, decimal baselineRangeFraction, decimal[] volumePrefix, SurgeStudyParameters parameters)
    {
        decimal low = candles[lowIndex].Low;
        decimal riseFraction = (peak - low) / low;
        int durationCandles = Math.Max(1, peakIndex - lowIndex);
        int horizonEnd = Math.Min(qualificationIndex + horizonCandles, candles.Count - 1);

        decimal crashLow = qualificationIndex == peakIndex ? candles[qualificationIndex].Close : candles[qualificationIndex].Low;
        int crashLowIndex = qualificationIndex, reclaimIndex = -1;
        decimal maximumHigh = peak;
        for (int j = qualificationIndex + 1; j <= horizonEnd; j++)
        {
            if (reclaimIndex < 0 && candles[j].Low < crashLow) { crashLow = candles[j].Low; crashLowIndex = j; }
            if (reclaimIndex < 0 && candles[j].High > peak) reclaimIndex = j;
            if (candles[j].High > maximumHigh) maximumHigh = candles[j].High;
        }

        decimal? reclaimSpeedFractionPerHour = null;
        if (reclaimIndex >= 0 && crashLow > 0m)
        {
            decimal reclaimHours = (decimal)(candles[reclaimIndex].OpenTime - candles[crashLowIndex].OpenTime).TotalHours;
            if (reclaimHours > 0m) reclaimSpeedFractionPerHour = (peak - crashLow) / crashLow / reclaimHours;
        }

        var peakCandle = candles[peakIndex];
        decimal bodyTop = Math.Max(peakCandle.Open, peakCandle.Close);
        bool isWick = durationCandles == 1 && peakIndex > 0 && bodyTop > 0m &&
                      peak >= bodyTop * (1m + requiredRiseFraction) &&
                      peak >= candles[peakIndex - 1].High * (1m + requiredRiseFraction);

        decimal averageBaselineVolume = (volumePrefix[lowIndex] - volumePrefix[lowIndex - windowCandles]) / windowCandles;
        DateTime trendEndTime = candles[lowIndex - 1].OpenTime;

        return new SurgeEvent
        {
            SurgeWindowMinutes = windowMinutes,
            SurgeLowTime = candles[lowIndex].OpenTime,
            SurgeLow = low,
            SurgePeakTime = peakCandle.OpenTime,
            SurgePeak = peak,
            SurgeRiseFraction = riseFraction,
            RequiredRiseFraction = requiredRiseFraction,
            BaselineRangeFraction = baselineRangeFraction,
            SurgeDurationMinutes = durationCandles * intervalMinutes,
            IsWick = isWick,
            PeakTradeCount = peakCandle.TradeCount,
            PeakVolumeRatio = averageBaselineVolume > 0m ? peakCandle.Volume / averageBaselineVolume : 0m,
            CrashQualificationTime = candles[qualificationIndex].OpenTime,
            CrashDurationMinutes = (qualificationIndex - peakIndex) * intervalMinutes,
            CrashLow = crashLow,
            CrashLowTime = candles[crashLowIndex].OpenTime,
            RetraceFractionReached = (peak - crashLow) / (peak - low),
            MinutesToReclaim = reclaimIndex >= 0 ? (int)(candles[reclaimIndex].OpenTime - candles[qualificationIndex].OpenTime).TotalMinutes : null,
            ReclaimTime = reclaimIndex >= 0 ? candles[reclaimIndex].OpenTime : null,
            MaximumRiseAbovePeakFraction = (maximumHigh - peak) / peak,
            SurgeSpeedFractionPerHour = riseFraction * 60m / (durationCandles * intervalMinutes),
            ReclaimSpeedFractionPerHour = reclaimSpeedFractionPerHour,
            TrendChangeFraction = Indicators.WindowChangeFraction(candles, trendEndTime, parameters.TrendWindowMinutes),
            ShortTrendChangeFraction = Indicators.WindowChangeFraction(candles, trendEndTime, Math.Max(1, parameters.TrendWindowMinutes / 4)),
            LongTrendChangeFraction = Indicators.WindowChangeFraction(candles, trendEndTime, parameters.TrendWindowMinutes * 3)
        };
    }

    /// <summary>
    /// Keeps the largest rise from each cluster of overlapping detections, because the same move found
    /// at four window scales is one surge, not four.
    /// </summary>
    private static List<SurgeEvent> DeduplicateOverlappingEvents(List<SurgeEvent> events)
    {
        var ordered = events.OrderBy(e => e.SurgeLowTime).ThenBy(e => e.SurgePeakTime).ToList();
        var kept = new List<SurgeEvent>();
        int groupStart = 0;
        while (groupStart < ordered.Count)
        {
            var best = ordered[groupStart];
            DateTime groupEnd = ordered[groupStart].SurgePeakTime;
            int next = groupStart + 1;
            while (next < ordered.Count && ordered[next].SurgeLowTime <= groupEnd)
            {
                if (ordered[next].SurgePeakTime > groupEnd) groupEnd = ordered[next].SurgePeakTime;
                if (ordered[next].SurgeRiseFraction > best.SurgeRiseFraction) best = ordered[next];
                next++;
            }
            kept.Add(best);
            groupStart = next;
        }
        return kept;
    }

    /// <summary>
    /// Records, for each event, how many earlier surges this market had and how many of those got back
    /// to their peak — the track record that was knowable at the time, without hindsight.
    /// </summary>
    private static void AttachEventHistory(List<SurgeEvent> events)
    {
        for (int e = 0; e < events.Count; e++)
        {
            int priorCount = 0, priorReclaimedCount = 0;
            for (int p = 0; p < e; p++)
            {
                if (events[p].SurgePeakTime >= events[e].SurgeLowTime) continue;
                priorCount++;
                if (events[p].ReclaimTime != null && events[p].ReclaimTime < events[e].SurgeLowTime) priorReclaimedCount++;
            }
            events[e].PriorEventCount = priorCount;
            events[e].PriorReclaimedCount = priorReclaimedCount;
        }
    }

    private static int[] ComputeSlidingMinimumLowIndices(IReadOnlyList<AnalysisCandle> candles, int windowCandles)
    {
        var indices = new int[candles.Count];
        var deque = new int[candles.Count];
        int head = 0, tail = 0;
        for (int i = 0; i < candles.Count; i++)
        {
            while (tail > head && candles[deque[tail - 1]].Low > candles[i].Low) tail--;
            deque[tail++] = i;
            if (deque[head] <= i - windowCandles) head++;
            indices[i] = deque[head];
        }
        return indices;
    }

    private static (decimal[] Lows, decimal[] Highs) ComputeSlidingBaselineExtremes(IReadOnlyList<AnalysisCandle> candles, int windowCandles)
    {
        var lows = new decimal[candles.Count];
        var highs = new decimal[candles.Count];
        var lowDeque = new int[candles.Count];
        var highDeque = new int[candles.Count];
        int lowHead = 0, lowTail = 0, highHead = 0, highTail = 0;

        for (int i = 0; i < candles.Count; i++)
        {
            while (lowTail > lowHead && candles[lowDeque[lowTail - 1]].Low > candles[i].Low) lowTail--;
            lowDeque[lowTail++] = i;
            if (lowDeque[lowHead] <= i - windowCandles) lowHead++;
            lows[i] = candles[lowDeque[lowHead]].Low;

            while (highTail > highHead && candles[highDeque[highTail - 1]].High < candles[i].High) highTail--;
            highDeque[highTail++] = i;
            if (highDeque[highHead] <= i - windowCandles) highHead++;
            highs[i] = candles[highDeque[highHead]].High;
        }
        return (lows, highs);
    }
}
