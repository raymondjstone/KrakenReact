namespace KrakenReact.Server.Analysis;

public enum LevelKind { Support, Resistance }

public enum LevelEncounterOutcome { Rejected, Broken }

/// <summary>What happened after a level held: whether the move that followed paid or gave it all back.</summary>
public enum LevelEncounterFollowThrough { NotMeasured, Reached, Failed }

/// <summary>Thresholds governing how pivots are clustered into levels and how encounters are judged.</summary>
public class SupportResistanceStudyParameters
{
    public static SupportResistanceStudyParameters Default { get; } = new();

    public int AtrPeriod { get; init; } = 14;

    /// <summary>The reversal, in ranges, that confirms a swing extreme as a pivot.</summary>
    public decimal ReversalAtrMultiple { get; init; } = 3m;

    /// <summary>How close two pivots must be, in ranges, to count as the same level.</summary>
    public decimal ClusterAtrMultiple { get; init; } = 0.75m;

    public decimal RejectAtrMultiple { get; init; } = 1m;
    public decimal BreakAtrMultiple { get; init; } = 1m;

    /// <summary>The move, in ranges, a held level must deliver for the hold to have been worth taking.</summary>
    public decimal RewardAtrMultiple { get; init; } = 3m;

    public int TrendWindowBars { get; init; } = 200;
    public int TrendSlopeLookbackBars { get; init; } = 50;
}

/// <summary>A price level built from clustered pivots.</summary>
public sealed record PriceLevel(
    int Id,
    decimal Price,
    int TouchCount,
    LevelKind LastActedKind,
    decimal LowestTouchPrice,
    decimal HighestTouchPrice)
{
    /// <summary>How wide the level really is, as a fraction of its price — a level is a zone, not a line.</summary>
    public decimal ZoneWidthFraction => Price > 0m ? (HighestTouchPrice - LowestTouchPrice) / Price : 0m;
}

/// <summary>One occasion on which price approached a level, and what came of it.</summary>
public sealed record LevelEncounter(
    LevelEncounterOutcome Outcome,
    LevelKind Kind,
    TrendDirection TrendContext,
    int PriorTouchCount,
    decimal Price,
    DateTime OpenTime,
    int LevelId,
    decimal TouchVolumeRatio,
    decimal PriorBounceAtrMean,
    decimal ApproachSpeedAtr,
    LevelEncounterFollowThrough FollowThrough);

/// <summary>How often levels of a given maturity held, cut by kind and by the trend they sat in.</summary>
public sealed record LevelEncounterSummary(string TouchTier, LevelKind Kind, TrendDirection TrendContext, int Rejected, int Total)
{
    public decimal RejectionRate => Total > 0 ? (decimal)Rejected / Total : 0m;
}

/// <summary>
/// Builds support and resistance levels by clustering confirmed swing pivots, then replays the series
/// to record every approach and whether the level held or broke. The point is not to draw lines: it is
/// to measure, from this market's own history, how much a level that has been touched four times is
/// actually worth compared with one touched twice.
/// </summary>
public static class SupportResistance
{
    private const int VolumeAveragePeriodBars = 50;

    /// <summary>The levels the series has built, most-touched first.</summary>
    public static List<PriceLevel> BuildLevels(IReadOnlyList<AnalysisCandle> candles, SupportResistanceStudyParameters parameters)
    {
        var result = new List<PriceLevel>();
        int count = candles?.Count ?? 0;
        if (count < parameters.AtrPeriod + 2) return result;

        var atr = Indicators.AverageTrueRange(candles!, parameters.AtrPeriod);
        var pivots = TrendDetection.FindTrendPivots(candles!, atr, parameters.AtrPeriod, parameters.ReversalAtrMultiple);
        if (pivots.Count == 0) return result;

        var volumeAverages = ComputeVolumeAverages(candles!, VolumeAveragePeriodBars);
        var levels = new List<Level>();
        TrendPivot? previousPivot = null;
        Level? previousPivotLevel = null;

        foreach (var pivot in pivots)
        {
            decimal range = atr[pivot.Index] > 0m ? atr[pivot.Index] : candles![pivot.Index].Close * 0.0005m;
            if (range <= 0m) continue;
            if (previousPivot != null && previousPivotLevel != null && atr[previousPivot.Index] > 0m)
                previousPivotLevel.AddBounce(Math.Abs(pivot.Price - previousPivot.Price) / atr[previousPivot.Index]);
            decimal volumeRatio = volumeAverages[pivot.Index] > 0m ? candles![pivot.Index].Volume / volumeAverages[pivot.Index] : 0m;
            previousPivot = pivot;
            previousPivotLevel = TouchLevel(levels, pivot, parameters.ClusterAtrMultiple * range, volumeRatio);
        }

        return levels
            .Select(l => new PriceLevel(l.Id, l.Price, l.TouchCount, l.LastActedKind, l.LowestTouchPrice, l.HighestTouchPrice))
            .ToList();
    }

    /// <summary>Every approach to a level in the series, in the order they occurred.</summary>
    public static List<LevelEncounter> AnalyzeLevelEncounters(IReadOnlyList<AnalysisCandle> candles, SupportResistanceStudyParameters parameters)
    {
        var encounters = new List<LevelEncounter>();
        int count = candles?.Count ?? 0;
        if (count < parameters.AtrPeriod + 2) return encounters;

        var atr = Indicators.AverageTrueRange(candles!, parameters.AtrPeriod);
        var pivots = TrendDetection.FindTrendPivots(candles!, atr, parameters.AtrPeriod, parameters.ReversalAtrMultiple);
        if (pivots.Count == 0) return encounters;

        var contexts = ComputeTrendContexts(candles!, parameters);
        var volumeAverages = ComputeVolumeAverages(candles!, VolumeAveragePeriodBars);
        var levels = new List<Level>();
        int nextPivotPosition = 0;
        TrendPivot? previousPivot = null;
        Level? previousPivotLevel = null;

        // Levels are only ever built from pivots already in the past at the bar being replayed, so an
        // encounter is judged against what was knowable then rather than against the finished chart.
        for (int i = pivots[0].Index; i < count; i++)
        {
            decimal range = atr[i] > 0m ? atr[i] : candles![i].Close * 0.0005m;
            if (range <= 0m) continue;

            while (nextPivotPosition < pivots.Count && pivots[nextPivotPosition].Index == i)
            {
                var pivot = pivots[nextPivotPosition++];
                if (previousPivot != null && previousPivotLevel != null && atr[previousPivot.Index] > 0m)
                    previousPivotLevel.AddBounce(Math.Abs(pivot.Price - previousPivot.Price) / atr[previousPivot.Index]);
                decimal pivotVolumeRatio = volumeAverages[pivot.Index] > 0m ? candles![pivot.Index].Volume / volumeAverages[pivot.Index] : 0m;
                previousPivot = pivot;
                previousPivotLevel = TouchLevel(levels, pivot, parameters.ClusterAtrMultiple * range, pivotVolumeRatio);
            }

            decimal approachSpeed = i >= 10 && atr[i] > 0m ? Math.Abs(candles![i].Close - candles[i - 10].Close) / (10m * atr[i]) : 0m;
            foreach (var level in levels) UpdateLevelApproachState(encounters, level, candles!, i, range, approachSpeed, contexts[i], parameters);
        }
        return encounters;
    }

    /// <summary>Groups encounters by level maturity, kind and trend, giving the rate at which each held.</summary>
    public static List<LevelEncounterSummary> Summarize(IEnumerable<LevelEncounter> encounters) =>
        encounters
            .GroupBy(e => (Tier: GetTouchTier(e.PriorTouchCount), e.Kind, e.TrendContext))
            .Select(g => new LevelEncounterSummary(g.Key.Tier, g.Key.Kind, g.Key.TrendContext,
                                                   g.Count(e => e.Outcome == LevelEncounterOutcome.Rejected), g.Count()))
            .OrderBy(s => s.Kind).ThenBy(s => s.TrendContext).ThenByDescending(s => s.Total)
            .ToList();

    /// <summary>Buckets a touch count, collapsing the thin tail so the rates it reports mean something.</summary>
    public static string GetTouchTier(int priorTouchCount) =>
        priorTouchCount >= 4 ? "4+" : priorTouchCount.ToString();

    /// <summary>The prevailing trend at each bar, from the slope of a long moving average.</summary>
    public static TrendDirection[] ComputeTrendContexts(IReadOnlyList<AnalysisCandle> candles, SupportResistanceStudyParameters parameters)
    {
        int count = candles.Count;
        var contexts = new TrendDirection[count];
        var averages = new decimal[count];
        decimal sum = 0m;

        for (int i = 0; i < count; i++)
        {
            sum += candles[i].Close;
            if (i >= parameters.TrendWindowBars) sum -= candles[i - parameters.TrendWindowBars].Close;
            averages[i] = sum / Math.Min(i + 1, parameters.TrendWindowBars);
        }

        for (int i = 0; i < count; i++)
        {
            if (i < parameters.TrendWindowBars + parameters.TrendSlopeLookbackBars) { contexts[i] = TrendDirection.None; continue; }
            decimal slope = averages[i] - averages[i - parameters.TrendSlopeLookbackBars];
            contexts[i] = slope > 0m ? TrendDirection.Up : slope < 0m ? TrendDirection.Down : TrendDirection.None;
        }
        return contexts;
    }

    private static void UpdateLevelApproachState(
        List<LevelEncounter> encounters, Level level, IReadOnlyList<AnalysisCandle> candles, int index,
        decimal range, decimal approachSpeedAtr, TrendDirection context, SupportResistanceStudyParameters parameters)
    {
        decimal close = candles[index].Close;
        if (!level.IsInApproach)
        {
            // A level arms on the side price is clearly on, and only counts as approached once price
            // has crossed back towards it — which is what stops one drift being counted many times.
            decimal distance = close - level.Price;
            int sideSign = Math.Abs(distance) >= parameters.ClusterAtrMultiple * range ? Math.Sign(distance) : 0;
            if (sideSign == level.ArmedSideSign) return;
            if (level.ArmedSideSign == 0) { level.ArmedSideSign = sideSign; return; }
            level.BeginApproach(
                level.Price + level.ArmedSideSign * parameters.RejectAtrMultiple * range,
                level.Price - level.ArmedSideSign * parameters.BreakAtrMultiple * range,
                range, candles[index].OpenTime, context, approachSpeedAtr);
            if (sideSign == 0) return;
        }

        bool broken = level.ApproachSideSign > 0 ? close <= level.ApproachBreakPrice : close >= level.ApproachBreakPrice;
        bool rejected = level.ApproachSideSign > 0 ? close >= level.ApproachRejectPrice : close <= level.ApproachRejectPrice;
        if (!broken && !rejected) return;

        var outcome = broken ? LevelEncounterOutcome.Broken : LevelEncounterOutcome.Rejected;
        var followThrough = outcome == LevelEncounterOutcome.Rejected
            ? ComputeFollowThrough(candles, index, level.ApproachLevelPrice, level.ApproachSideSign, level.ApproachRange, level.ApproachBreakPrice, parameters.RewardAtrMultiple)
            : LevelEncounterFollowThrough.NotMeasured;

        encounters.Add(new LevelEncounter(
            outcome,
            level.ApproachSideSign > 0 ? LevelKind.Support : LevelKind.Resistance,
            level.ApproachTrendContext, level.ApproachPriorTouchCount, level.ApproachLevelPrice,
            level.ApproachOpenTime, level.Id, level.ApproachTouchVolumeRatio,
            level.ApproachPriorBounceAtrMean, level.ApproachSpeedAtr, followThrough));
        level.EndApproach();
    }

    private static LevelEncounterFollowThrough ComputeFollowThrough(
        IReadOnlyList<AnalysisCandle> candles, int fromIndex, decimal levelPrice, int sideSign,
        decimal range, decimal breakPrice, decimal rewardAtrMultiple)
    {
        decimal rewardPrice = levelPrice + sideSign * rewardAtrMultiple * range;
        for (int j = fromIndex; j < candles.Count; j++)
        {
            decimal close = candles[j].Close;
            if (sideSign > 0 ? close <= breakPrice : close >= breakPrice) return LevelEncounterFollowThrough.Failed;
            if (sideSign > 0 ? close >= rewardPrice : close <= rewardPrice) return LevelEncounterFollowThrough.Reached;
        }
        return LevelEncounterFollowThrough.NotMeasured;
    }

    private static Level TouchLevel(List<Level> levels, TrendPivot pivot, decimal clusterTolerance, decimal volumeRatio)
    {
        var matched = FindNearestLevel(levels, pivot.Price, clusterTolerance);
        if (matched != null) matched.AddTouch(pivot.Price, volumeRatio, pivot.IsHigh);
        else { matched = new Level(levels.Count, pivot.Price, volumeRatio, pivot.IsHigh); levels.Add(matched); }
        return matched;
    }

    private static Level? FindNearestLevel(List<Level> levels, decimal price, decimal tolerance)
    {
        Level? nearest = null;
        decimal best = decimal.MaxValue;
        foreach (var level in levels)
        {
            decimal distance = Math.Abs(level.Price - price);
            if (distance <= tolerance && distance < best) { best = distance; nearest = level; }
        }
        return nearest;
    }

    private static decimal[] ComputeVolumeAverages(IReadOnlyList<AnalysisCandle> candles, int period)
    {
        int count = candles.Count;
        var averages = new decimal[count];
        decimal sum = 0m;
        for (int i = 0; i < count; i++)
        {
            sum += candles[i].Volume;
            if (i >= period) sum -= candles[i - period].Volume;
            averages[i] = i >= period - 1 ? sum / period : 0m;
        }
        return averages;
    }

    /// <summary>A level under construction, carrying the running state the replay needs.</summary>
    private sealed class Level
    {
        private int _bounceCount;

        public Level(int id, decimal price, decimal touchVolumeRatio, bool isHigh)
        {
            Id = id;
            Price = price;
            LowestTouchPrice = price;
            HighestTouchPrice = price;
            TouchCount = 1;
            TouchVolumeRatioMean = touchVolumeRatio;
            LastActedKind = isHigh ? LevelKind.Resistance : LevelKind.Support;
        }

        public int Id { get; }
        public decimal Price { get; private set; }
        public decimal LowestTouchPrice { get; private set; }
        public decimal HighestTouchPrice { get; private set; }
        public int TouchCount { get; private set; }
        public LevelKind LastActedKind { get; private set; }
        public decimal TouchVolumeRatioMean { get; private set; }
        public decimal PriorBounceAtrMean { get; private set; }

        public int ArmedSideSign { get; set; }
        public bool IsInApproach { get; private set; }
        public int ApproachSideSign { get; private set; }
        public decimal ApproachLevelPrice { get; private set; }
        public decimal ApproachRejectPrice { get; private set; }
        public decimal ApproachBreakPrice { get; private set; }
        public decimal ApproachRange { get; private set; }
        public int ApproachPriorTouchCount { get; private set; }
        public decimal ApproachTouchVolumeRatio { get; private set; }
        public decimal ApproachPriorBounceAtrMean { get; private set; }
        public decimal ApproachSpeedAtr { get; private set; }
        public DateTime ApproachOpenTime { get; private set; }
        public TrendDirection ApproachTrendContext { get; private set; }

        public void AddTouch(decimal price, decimal volumeRatio, bool isHigh)
        {
            if (price < LowestTouchPrice) LowestTouchPrice = price;
            if (price > HighestTouchPrice) HighestTouchPrice = price;
            Price = (Price * TouchCount + price) / (TouchCount + 1);
            TouchVolumeRatioMean = (TouchVolumeRatioMean * TouchCount + volumeRatio) / (TouchCount + 1);
            TouchCount++;
            LastActedKind = isHigh ? LevelKind.Resistance : LevelKind.Support;
        }

        public void AddBounce(decimal bounceAtr)
        {
            PriorBounceAtrMean = (PriorBounceAtrMean * _bounceCount + bounceAtr) / (_bounceCount + 1);
            _bounceCount++;
        }

        public void BeginApproach(decimal rejectPrice, decimal breakPrice, decimal range, DateTime openTime, TrendDirection trendContext, decimal approachSpeedAtr)
        {
            IsInApproach = true;
            ApproachSideSign = ArmedSideSign;
            ApproachLevelPrice = Price;
            ApproachRejectPrice = rejectPrice;
            ApproachBreakPrice = breakPrice;
            ApproachRange = range;
            ApproachPriorTouchCount = TouchCount;
            ApproachTouchVolumeRatio = TouchVolumeRatioMean;
            ApproachPriorBounceAtrMean = PriorBounceAtrMean;
            ApproachSpeedAtr = approachSpeedAtr;
            ApproachOpenTime = openTime;
            ApproachTrendContext = trendContext;
            ArmedSideSign = 0;
        }

        public void EndApproach() => IsInApproach = false;
    }
}
