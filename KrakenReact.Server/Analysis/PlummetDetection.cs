namespace KrakenReact.Server.Analysis;

/// <summary>Thresholds governing what counts as a plummet and how far its rebound is followed.</summary>
public class PlummetStudyParameters
{
    public static PlummetStudyParameters Default { get; } = new();

    /// <summary>The floor a fall must clear before it is a plummet at all.</summary>
    public decimal MinimumDropFraction { get; set; } = 0.10m;

    /// <summary>
    /// Multiple of the preceding baseline range the fall must also clear, so a market that swings
    /// this much routinely does not report a plummet every time it breathes.
    /// </summary>
    public decimal QuietnessMultiple { get; set; } = 0.75m;

    public int DropWindowMinutes { get; set; } = 120;
    public int BaselineWindowMinutes { get; set; } = 240;
    public int ReboundWindowMinutes { get; set; } = 4320;

    /// <summary>The bounce that stops the search for a lower low, so one event is not merged into the next.</summary>
    public decimal ReboundFreezeFraction { get; set; } = 0.25m;

    public int AveragePriceWindowMinutes { get; set; } = 720;

    /// <summary>How far below its own recent average the low must sit, which rejects falls from a spike.</summary>
    public decimal MinimumFallBelowAverageFraction { get; set; } = 0.02m;

    /// <summary>How far above that average the reference high may sit, for the same reason.</summary>
    public decimal MaximumReferenceHighAboveAverageFraction { get; set; } = 0.20m;
}

/// <summary>One detected plummet, together with what its rebound went on to do.</summary>
public class PlummetEvent
{
    public DateTime TriggerTime { get; set; }
    public decimal ReferenceHigh { get; set; }
    public DateTime ReferenceHighTime { get; set; }
    public decimal AveragePrice { get; set; }
    public decimal EventLow { get; set; }
    public DateTime EventLowTime { get; set; }

    /// <summary>Whether the low was a lone wick rather than a level the market actually traded at.</summary>
    public bool IsWick { get; set; }

    public decimal DropFraction { get; set; }
    public decimal RequiredDropFraction { get; set; }
    public decimal BaselineRangeFraction { get; set; }
    public decimal VolumeRatio { get; set; }
    public decimal OwnWindowChangeFraction { get; set; }

    /// <summary>The furthest the price recovered, as a share of the fall.</summary>
    public decimal MaximumReboundFraction { get; set; }

    public int? MinutesToFivePercentRebound { get; set; }
    public int? MinutesToTenPercentRebound { get; set; }
    public int? MinutesToFifteenPercentRebound { get; set; }
    public int? MinutesToQuarterRebound { get; set; }
    public int? MinutesToHalfRebound { get; set; }
    public int? MinutesToThreeQuarterRebound { get; set; }
    public int? MinutesToFullRebound { get; set; }

    public bool LowerLowAfterMaximumRebound { get; set; }
}

/// <summary>
/// One cell of the decay table: of the events still waiting for a rebound onset after this much
/// time, how many went on to recover half the fall.
/// </summary>
public class PlummetDecayCell
{
    public decimal OnsetFraction { get; set; }
    public int ElapsedMinutes { get; set; }
    public int PendingCount { get; set; }
    public int ReachedHalfCount { get; set; }
    public decimal ReachedHalfFraction => PendingCount > 0 ? (decimal)ReachedHalfCount / PendingCount : 0m;
}

/// <summary>
/// Finds sharp falls in a candle series and measures what happened afterwards, which is what turns
/// "the price dropped" into a question that history can answer: given a fall of this shape, how
/// often and how quickly did the market come back?
/// </summary>
public static class PlummetDetection
{
    private const decimal FivePercentReboundFraction = 0.05m;
    private const decimal TenPercentReboundFraction = 0.10m;
    private const decimal FifteenPercentReboundFraction = 0.15m;
    private const decimal QuarterReboundFraction = 0.25m;
    private const decimal HalfReboundFraction = 0.50m;
    private const decimal ThreeQuarterReboundFraction = 0.75m;
    private const decimal FullReboundFraction = 1.00m;

    /// <summary>Detects every plummet in the series, oldest first, each with its rebound history attached.</summary>
    public static List<PlummetEvent> DetectPlummetEvents(IReadOnlyList<AnalysisCandle> candles, int intervalMinutes, PlummetStudyParameters parameters)
    {
        var events = new List<PlummetEvent>();
        if (candles == null || intervalMinutes <= 0) return events;

        int windowCandles = parameters.DropWindowMinutes / intervalMinutes;
        int baselineCandles = parameters.BaselineWindowMinutes / intervalMinutes;
        int reboundCandles = parameters.ReboundWindowMinutes / intervalMinutes;
        if (windowCandles < 1 || baselineCandles < 1) return events;

        int averageCandles = Math.Max(1, parameters.AveragePriceWindowMinutes / intervalMinutes);
        var closeSums = new decimal[candles.Count + 1];
        for (int c = 0; c < candles.Count; c++) closeSums[c + 1] = closeSums[c] + candles[c].Close;

        for (int i = windowCandles + baselineCandles - 1; i < candles.Count; i++)
        {
            int referenceHighIndex = i - windowCandles + 1;
            decimal referenceHigh = candles[referenceHighIndex].High;
            for (int w = i - windowCandles + 2; w <= i; w++)
                if (candles[w].High > referenceHigh) { referenceHigh = candles[w].High; referenceHighIndex = w; }
            if (referenceHigh <= 0m || (referenceHigh - candles[i].Low) / referenceHigh < parameters.MinimumDropFraction) continue;

            int baselineStart = i - windowCandles - baselineCandles + 1, baselineEnd = i - windowCandles;
            decimal baselineHigh = candles[baselineStart].High, baselineLow = candles[baselineStart].Low, baselineVolume = candles[baselineStart].Volume;
            for (int b = baselineStart + 1; b <= baselineEnd; b++)
            {
                if (candles[b].High > baselineHigh) baselineHigh = candles[b].High;
                if (candles[b].Low < baselineLow) baselineLow = candles[b].Low;
                baselineVolume += candles[b].Volume;
            }
            if (baselineLow <= 0m) continue;

            decimal baselineRangeFraction = (baselineHigh - baselineLow) / baselineLow;
            decimal requiredDropFraction = Math.Max(parameters.MinimumDropFraction, parameters.QuietnessMultiple * baselineRangeFraction);
            if ((referenceHigh - candles[i].Low) / referenceHigh < requiredDropFraction) continue;

            int averageStart = Math.Max(0, baselineEnd - averageCandles + 1);
            decimal averagePrice = (closeSums[baselineEnd + 1] - closeSums[averageStart]) / (baselineEnd - averageStart + 1);
            if (averagePrice <= 0m) continue;
            if (parameters.MinimumFallBelowAverageFraction > 0m && candles[i].Low > averagePrice * (1m - parameters.MinimumFallBelowAverageFraction)) continue;
            if (parameters.MaximumReferenceHighAboveAverageFraction > 0m && referenceHigh > averagePrice * (1m + parameters.MaximumReferenceHighAboveAverageFraction)) continue;

            decimal windowVolume = 0m;
            for (int w = i - windowCandles + 1; w <= i; w++) windowVolume += candles[w].Volume;
            decimal averageBaselineVolume = baselineVolume / baselineCandles;
            decimal volumeRatio = averageBaselineVolume > 0m ? windowVolume / windowCandles / averageBaselineVolume : 0m;
            decimal windowStartClose = candles[i - windowCandles].Close;
            decimal ownWindowChangeFraction = windowStartClose > 0m ? (candles[i].Close - windowStartClose) / windowStartClose : 0m;

            // Walk forward for the true low, stopping once a meaningful bounce says the fall is over.
            int windowEnd = Math.Min(i + reboundCandles, candles.Count - 1);
            int lowIndex = i;
            decimal low = candles[i].Low;
            for (int j = i + 1; j <= windowEnd; j++)
            {
                if (candles[j].High >= low + parameters.ReboundFreezeFraction * (referenceHigh - low)) break;
                if (candles[j].Low < low) { low = candles[j].Low; lowIndex = j; }
            }

            decimal dropDistance = referenceHigh - low, maximumReboundFraction = 0m;
            int maximumReboundIndex = -1;
            int? minutesToFive = null, minutesToTen = null, minutesToFifteen = null;
            int? minutesToQuarter = null, minutesToHalf = null, minutesToThreeQuarter = null, minutesToFull = null;

            for (int j = lowIndex + 1; j <= windowEnd && dropDistance > 0m; j++)
            {
                decimal reboundFraction = (candles[j].High - low) / dropDistance;
                if (reboundFraction > maximumReboundFraction) { maximumReboundFraction = reboundFraction; maximumReboundIndex = j; }
                int minutes = (int)(candles[j].OpenTime - candles[lowIndex].OpenTime).TotalMinutes;
                if (minutesToFive == null && reboundFraction >= FivePercentReboundFraction) minutesToFive = minutes;
                if (minutesToTen == null && reboundFraction >= TenPercentReboundFraction) minutesToTen = minutes;
                if (minutesToFifteen == null && reboundFraction >= FifteenPercentReboundFraction) minutesToFifteen = minutes;
                if (minutesToQuarter == null && reboundFraction >= QuarterReboundFraction) minutesToQuarter = minutes;
                if (minutesToHalf == null && reboundFraction >= HalfReboundFraction) minutesToHalf = minutes;
                if (minutesToThreeQuarter == null && reboundFraction >= ThreeQuarterReboundFraction) minutesToThreeQuarter = minutes;
                if (minutesToFull == null && reboundFraction >= FullReboundFraction) minutesToFull = minutes;
            }

            bool lowerLowAfterMaximumRebound = false;
            for (int j = maximumReboundIndex + 1; j <= windowEnd && maximumReboundIndex >= 0 && !lowerLowAfterMaximumRebound; j++)
                lowerLowAfterMaximumRebound = candles[j].Low < low;

            var lowCandle = candles[lowIndex];
            decimal bodyBottom = Math.Min(lowCandle.Open, lowCandle.Close);
            bool isWick = lowIndex > 0 && low > 0m &&
                          bodyBottom >= low * (1m + requiredDropFraction) &&
                          candles[lowIndex - 1].Low >= low * (1m + requiredDropFraction);

            events.Add(new PlummetEvent
            {
                IsWick = isWick,
                TriggerTime = candles[i].OpenTime,
                ReferenceHigh = referenceHigh,
                ReferenceHighTime = candles[referenceHighIndex].OpenTime,
                AveragePrice = averagePrice,
                EventLow = low,
                EventLowTime = candles[lowIndex].OpenTime,
                DropFraction = (referenceHigh - low) / referenceHigh,
                RequiredDropFraction = requiredDropFraction,
                BaselineRangeFraction = baselineRangeFraction,
                VolumeRatio = volumeRatio,
                OwnWindowChangeFraction = ownWindowChangeFraction,
                MaximumReboundFraction = maximumReboundFraction,
                MinutesToFivePercentRebound = minutesToFive,
                MinutesToTenPercentRebound = minutesToTen,
                MinutesToFifteenPercentRebound = minutesToFifteen,
                MinutesToQuarterRebound = minutesToQuarter,
                MinutesToHalfRebound = minutesToHalf,
                MinutesToThreeQuarterRebound = minutesToThreeQuarter,
                MinutesToFullRebound = minutesToFull,
                LowerLowAfterMaximumRebound = lowerLowAfterMaximumRebound
            });
            i = windowEnd;
        }
        return events;
    }

    /// <summary>
    /// How the odds of a half recovery decay with waiting. Each cell asks: among the events that had
    /// still not bounced by this much of the fall after this many minutes, what share ever reached half?
    /// A column that falls away as the minutes grow is the market telling you when to stop waiting.
    /// </summary>
    public static List<PlummetDecayCell> ComputeDecayTable(IReadOnlyList<PlummetEvent> events, IReadOnlyList<int> elapsedMinutesGrid)
    {
        var onsets = new (decimal Fraction, Func<PlummetEvent, int?> MinutesToOnset)[]
        {
            (FivePercentReboundFraction, e => e.MinutesToFivePercentRebound),
            (TenPercentReboundFraction, e => e.MinutesToTenPercentRebound),
            (FifteenPercentReboundFraction, e => e.MinutesToFifteenPercentRebound),
            (QuarterReboundFraction, e => e.MinutesToQuarterRebound),
        };

        var cells = new List<PlummetDecayCell>();
        foreach (var (fraction, minutesToOnset) in onsets)
            foreach (int elapsedMinutes in elapsedMinutesGrid)
            {
                var pending = events.Where(e => minutesToOnset(e) == null || minutesToOnset(e) > elapsedMinutes).ToList();
                cells.Add(new PlummetDecayCell
                {
                    OnsetFraction = fraction,
                    ElapsedMinutes = elapsedMinutes,
                    PendingCount = pending.Count,
                    ReachedHalfCount = pending.Count(e => e.MinutesToHalfRebound != null)
                });
            }
        return cells;
    }
}
