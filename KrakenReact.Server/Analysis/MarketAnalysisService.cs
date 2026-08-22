using KrakenReact.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Analysis;

/// <summary>
/// Runs the whole analysis suite over one market's stored candles and assembles the answer the UI
/// asks for: where the trend stands, what the price is doing right now against its own history of
/// falls and spikes, and which levels above and below have actually meant something.
/// </summary>
public class MarketAnalysisService
{
    private readonly IDbContextFactory<KrakenDbContext> _dbFactory;
    private readonly ILogger<MarketAnalysisService> _logger;

    /// <summary>
    /// Elapsed-time grid the rebound decay table is reported over, in bars. Bars rather than minutes
    /// because a column has to mean the same thing on a daily series as on an hourly one.
    /// </summary>
    private static readonly int[] DecayGridBars = [1, 3, 6, 12, 24, 48];

    public MarketAnalysisService(IDbContextFactory<KrakenDbContext> dbFactory, ILogger<MarketAnalysisService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    /// <summary>Loads a market's stored candles for one interval, chronologically.</summary>
    public async Task<List<AnalysisCandle>> LoadCandlesAsync(string symbol, string interval, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var klines = await db.DerivedKlines
            .Where(k => k.Asset == symbol && k.Interval == interval && k.Close > 0)
            .AsNoTracking()
            .OrderBy(k => k.OpenTime)
            .ToListAsync(ct);
        return AnalysisCandle.FromKlines(klines);
    }

    /// <summary>The symbols and intervals that have enough stored candles to analyse.</summary>
    public async Task<List<AnalysisSymbolOption>> ListAnalysableAsync(int minimumCandles, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var groups = await db.DerivedKlines
            .AsNoTracking()
            .GroupBy(k => new { k.Asset, k.Interval })
            .Select(g => new { g.Key.Asset, g.Key.Interval, Count = g.Count(), LastOpenTime = g.Max(k => k.OpenTime) })
            .Where(g => g.Count >= minimumCandles)
            .ToListAsync(ct);

        return groups
            .Select(g => new AnalysisSymbolOption(g.Asset, g.Interval, g.Count, g.LastOpenTime))
            .OrderByDescending(o => o.CandleCount)
            .ThenBy(o => o.Symbol)
            .ToList();
    }

    /// <summary>Runs every analysis over the candles and returns the combined report.</summary>
    public MarketAnalysisReport Analyse(string symbol, string interval, IReadOnlyList<AnalysisCandle> candles, AnalysisOptions options)
    {
        int intervalMinutes = Indicators.InferIntervalMinutes(candles);
        if (candles.Count < 60 || intervalMinutes <= 0)
            return MarketAnalysisReport.Insufficient(symbol, interval, candles.Count, intervalMinutes);

        var last = candles[^1];
        var trendParameters = new TrendDetectionParameters
        {
            FastPeriod = options.MacdFastPeriod,
            SlowPeriod = options.MacdSlowPeriod,
            SignalPeriod = options.MacdSignalPeriod,
            UseZeroLagMovingAverages = options.UseZeroLagMovingAverages,
        };

        var trend = TrendDetection.ComputeLatestTrendState(candles, trendParameters);
        var (slopes, rSquared) = Indicators.RegressionTrend(candles, Math.Min(options.RegressionWindowBars, candles.Count));
        var atr = Indicators.AverageTrueRange(candles, trendParameters.AtrPeriod);
        var rsi = Indicators.RelativeStrengthIndex(candles.Select(c => c.Close).ToList(), trendParameters.RsiPeriod);

        var trendReport = new TrendReport(
            trend.Direction.ToString(),
            trend.Phase.ToString(),
            Math.Round(trend.StrengthPercent, 1),
            trend.IsNeutral,
            trend.FallingBarCount,
            trend.DirectionChangePrice,
            last.Close > 0m ? Math.Round((trend.DirectionChangePrice - last.Close) / last.Close * 100m, 2) : 0m,
            Math.Round(slopes[^1] * 100m, 4),
            Math.Round(rSquared[^1], 3),
            Math.Round(rsi[^1], 1),
            last.Close > 0m ? Math.Round(atr[^1] / last.Close * 100m, 2) : 0m);

        // Detect the falls once. The report describes them and the backtest trades them, and running
        // the detector twice over the same three and a half thousand candles is the single largest
        // avoidable cost in answering this request.
        var plummetParameters = BuildPlummetParameters(options, intervalMinutes);
        var plummetEvents = PlummetDetection.DetectPlummetEvents(candles, intervalMinutes, plummetParameters);

        var plummetReport = BuildPlummetReport(candles, intervalMinutes, options, last, plummetParameters, plummetEvents);
        var backtest = StrategyBacktest.Run(
            candles, plummetEvents, options.TradeWindowBars, options.Stake,
            options.FeeFractionPerSide, options.SpreadAllowanceFraction);
        var surgeReport = BuildSurgeReport(candles, intervalMinutes, options, last);
        var levelReport = BuildLevelReport(candles, options, last);

        return new MarketAnalysisReport(
            symbol, interval, "ok", null,
            candles.Count, intervalMinutes, candles[0].OpenTime, last.OpenTime, last.Close,
            trendReport, plummetReport, surgeReport, levelReport, backtest);
    }

    /// <summary>
    /// The detector takes its windows in minutes, but they are only meaningful as a number of bars:
    /// these thresholds were calibrated on one-minute candles, where 120 minutes is 120 bars. Handed
    /// hourly bars that same 120 becomes a two-bar window, and daily bars round it to zero, which
    /// switches detection off entirely. Scaling by the interval keeps every timeframe looking at the
    /// same shape.
    /// </summary>
    private static PlummetStudyParameters BuildPlummetParameters(AnalysisOptions options, int intervalMinutes) => new()
    {
        MinimumDropFraction = options.MinimumDropFraction,
        QuietnessMultiple = options.PlummetQuietnessMultiple,
        DropWindowMinutes = options.DropWindowBars * intervalMinutes,
        BaselineWindowMinutes = options.BaselineWindowBars * intervalMinutes,
        AveragePriceWindowMinutes = options.AveragePriceWindowBars * intervalMinutes,
        ReboundWindowMinutes = options.ReboundWindowBars * intervalMinutes,
        MinimumFallBelowAverageFraction = options.MinimumFallBelowAverageFraction,
        MaximumReferenceHighAboveAverageFraction = options.MaximumReferenceHighAboveAverageFraction,
    };

    private PlummetReport BuildPlummetReport(
        IReadOnlyList<AnalysisCandle> candles, int intervalMinutes, AnalysisOptions options,
        AnalysisCandle last, PlummetStudyParameters parameters, IReadOnlyList<PlummetEvent> events)
    {
        var decayGridMinutes = DecayGridBars.Select(b => b * intervalMinutes).ToArray();

        if (events.Count == 0)
            return new PlummetReport(0, 0, 0m, 0m, 0m, null, null, [], [], decayGridMinutes);

        // An event whose rebound window runs past the end of the data has an unfinished outcome.
        // Training on it would teach the model that falls near the end of history rarely recover.
        var cutoff = last.OpenTime.AddMinutes(-parameters.ReboundWindowMinutes);
        var settled = events.Where(e => e.EventLowTime <= cutoff).ToList();
        var pending = events.Count - settled.Count;

        var fit = ReboundJudge.FitFromHistory(settled);
        var latest = events[^1];

        decimal? liveScore = null;
        if (fit != null)
            liveScore = Math.Round(fit.Judge.Judge(ReboundJudge.BuildFeatures(latest, events.Count - 1)), 4);

        var decay = PlummetDetection.ComputeDecayTable(settled, decayGridMinutes)
            .Select(c => new DecayCellDto(c.OnsetFraction, c.ElapsedMinutes, c.PendingCount, c.ReachedHalfCount, Math.Round(c.ReachedHalfFraction, 3)))
            .ToList();

        var recent = events
            .OrderByDescending(e => e.EventLowTime)
            .Take(options.MaxEvents)
            .Select(e => new PlummetEventDto(
                e.TriggerTime, e.ReferenceHigh, e.ReferenceHighTime, e.EventLow, e.EventLowTime,
                Math.Round(e.DropFraction * 100m, 2), Math.Round(e.RequiredDropFraction * 100m, 2),
                Math.Round(e.BaselineRangeFraction * 100m, 2), Math.Round(e.VolumeRatio, 2), e.IsWick,
                Math.Round(e.MaximumReboundFraction * 100m, 1),
                e.MinutesToTenPercentRebound, e.MinutesToQuarterRebound, e.MinutesToHalfRebound, e.MinutesToFullRebound,
                e.LowerLowAfterMaximumRebound,
                e.EventLowTime > cutoff))
            .ToList();

        var recovered = settled.Count(ReboundJudge.Succeeded);

        return new PlummetReport(
            events.Count,
            pending,
            settled.Count == 0 ? 0m : Math.Round((decimal)recovered / settled.Count * 100m, 1),
            settled.Count == 0 ? 0m : Math.Round(settled.Average(e => e.DropFraction) * 100m, 2),
            settled.Count == 0 ? 0m : Math.Round(settled.Average(e => e.MaximumReboundFraction) * 100m, 1),
            liveScore,
            fit == null ? null : new JudgeReport(
                ReboundJudge.FeatureNames.Zip(fit.Judge.Weights, (name, weight) => new JudgeWeight(name, Math.Round(weight, 4))).ToList(),
                Math.Round(fit.Judge.Intercept, 4),
                fit.TrainCount, fit.TestCount,
                Math.Round(fit.TrainSuccessRate * 100m, 1),
                Math.Round(fit.TestSuccessRate * 100m, 1),
                Math.Round(fit.TestOrdering, 3),
                fit.TestPositiveCount,
                fit.TestNegativeCount,
                fit.IsReliable),
            decay,
            recent,
            decayGridMinutes);
    }

    private static SurgeReport BuildSurgeReport(IReadOnlyList<AnalysisCandle> candles, int intervalMinutes, AnalysisOptions options, AnalysisCandle last)
    {
        // Scaled by the interval for the same reason as the fall windows: a fifteen-minute surge
        // window is zero bars on an hourly series, so the shortest scales silently drop out.
        var parameters = new SurgeStudyParameters
        {
            MinimumSurgeFraction = options.MinimumSurgeFraction,
            QuietnessMultiple = options.SurgeQuietnessMultiple,
            SurgeWindowsMinutes = options.SurgeWindowsBars.Select(b => b * intervalMinutes).ToArray(),
            ReclaimHorizonMinutes = options.ReclaimHorizonBars * intervalMinutes,
            TrendWindowMinutes = options.BaselineWindowBars * intervalMinutes,
        };

        var events = SurgeDetection.DetectSurgeEvents(candles, intervalMinutes, parameters);
        var inProgress = SurgeDetection.DetectSurgeInProgress(candles, intervalMinutes, parameters);

        var cutoff = last.OpenTime.AddMinutes(-parameters.ReclaimHorizonMinutes);
        var settled = events.Where(e => e.CrashQualificationTime <= cutoff).ToList();
        var reclaimed = settled.Count(e => e.IsReclaimed);
        var reclaimTimes = settled.Where(e => e.MinutesToReclaim != null).Select(e => (decimal)e.MinutesToReclaim!.Value).ToList();

        var recent = events
            .OrderByDescending(e => e.SurgePeakTime)
            .Take(options.MaxEvents)
            .Select(e => new SurgeEventDto(
                e.SurgeWindowMinutes, e.SurgeLowTime, e.SurgeLow, e.SurgePeakTime, e.SurgePeak,
                Math.Round(e.SurgeRiseFraction * 100m, 2), e.SurgeDurationMinutes, e.IsWick,
                Math.Round(e.PeakVolumeRatio, 2), e.CrashQualificationTime, e.CrashLow, e.CrashLowTime,
                Math.Round(e.RetraceFractionReached * 100m, 1), e.MinutesToReclaim, e.IsReclaimed,
                Math.Round(e.SurgeSpeedFractionPerHour * 100m, 2),
                e.CrashQualificationTime > cutoff))
            .ToList();

        return new SurgeReport(
            events.Count,
            events.Count - settled.Count,
            settled.Count == 0 ? 0m : Math.Round((decimal)reclaimed / settled.Count * 100m, 1),
            reclaimTimes.Count == 0 ? null : (int)Indicators.Median(reclaimTimes),
            settled.Count == 0 ? 0m : Math.Round(settled.Average(e => e.SurgeRiseFraction) * 100m, 2),
            inProgress == null ? null : new SurgeInProgressDto(
                inProgress.SurgeLow, inProgress.SurgeLowTime, inProgress.SurgePeak, inProgress.SurgePeakTime,
                Math.Round(inProgress.SurgeRiseFraction * 100m, 2), inProgress.SurgeDurationMinutes, inProgress.IsCrashing,
                inProgress.SurgePeak > 0m ? Math.Round((last.Close - inProgress.SurgePeak) / inProgress.SurgePeak * 100m, 2) : 0m),
            recent);
    }

    private static LevelReport BuildLevelReport(IReadOnlyList<AnalysisCandle> candles, AnalysisOptions options, AnalysisCandle last)
    {
        var parameters = new SupportResistanceStudyParameters
        {
            ReversalAtrMultiple = options.PivotReversalAtrMultiple,
        };

        // Both of these derive the same average true range and the same pivots from the whole
        // series; computing them here means walking the candles once instead of twice.
        var atr = Indicators.AverageTrueRange(candles, parameters.AtrPeriod);
        var pivots = TrendDetection.FindTrendPivots(candles, atr, parameters.AtrPeriod, parameters.ReversalAtrMultiple);

        var levels = SupportResistance.BuildLevels(candles, parameters, atr, pivots);
        var encounters = SupportResistance.AnalyzeLevelEncounters(candles, parameters, atr, pivots);
        var summaries = SupportResistance.Summarize(encounters);

        // The rate at which a level of each maturity held, pooled across kind and trend, is the number
        // that answers "is a four-touch level worth more than a two-touch one in this market?".
        var byTier = encounters
            .GroupBy(e => SupportResistance.GetTouchTier(e.PriorTouchCount))
            .Select(g => new TierRateDto(
                g.Key,
                g.Count(),
                g.Count(e => e.Outcome == LevelEncounterOutcome.Rejected),
                Math.Round((decimal)g.Count(e => e.Outcome == LevelEncounterOutcome.Rejected) / g.Count() * 100m, 1),
                g.Count(e => e.FollowThrough == LevelEncounterFollowThrough.Reached)))
            .OrderBy(t => t.Tier)
            .ToList();

        var nearest = levels
            .Where(l => l.Price > 0m)
            .OrderBy(l => Math.Abs(l.Price - last.Close))
            .Take(options.MaxLevels)
            .Select(l => new PriceLevelDto(
                l.Id, l.Price, l.TouchCount,
                l.Price >= last.Close ? "Resistance" : "Support",
                l.LastActedKind.ToString(),
                Math.Round(l.ZoneWidthFraction * 100m, 2),
                last.Close > 0m ? Math.Round((l.Price - last.Close) / last.Close * 100m, 2) : 0m,
                encounters.Count(e => e.LevelId == l.Id),
                encounters.Count(e => e.LevelId == l.Id && e.Outcome == LevelEncounterOutcome.Rejected)))
            .OrderByDescending(l => l.DistancePercent)
            .ToList();

        var contexts = summaries
            .Select(s => new LevelSummaryDto(s.TouchTier, s.Kind.ToString(), s.TrendContext.ToString(), s.Rejected, s.Total, Math.Round(s.RejectionRate * 100m, 1)))
            .ToList();

        return new LevelReport(levels.Count, encounters.Count, nearest, byTier, contexts);
    }
}

/// <summary>Knobs the caller may turn without editing the detectors.</summary>
public sealed record AnalysisOptions
{
    public int MacdFastPeriod { get; init; } = 12;
    public int MacdSlowPeriod { get; init; } = 26;
    public int MacdSignalPeriod { get; init; } = 9;
    public bool UseZeroLagMovingAverages { get; init; }
    public int RegressionWindowBars { get; init; } = 60;
    public decimal MinimumDropFraction { get; init; } = 0.10m;
    public decimal PlummetQuietnessMultiple { get; init; } = 0.75m;
    public decimal MinimumSurgeFraction { get; init; } = 0.10m;
    public decimal SurgeQuietnessMultiple { get; init; } = 2.0m;
    public decimal PivotReversalAtrMultiple { get; init; } = 3m;

    /// <summary>Bars a fall must happen within to count as one event.</summary>
    public int DropWindowBars { get; init; } = 12;

    /// <summary>Bars of quiet before the fall, against which its size is judged.</summary>
    public int BaselineWindowBars { get; init; } = 24;

    /// <summary>Bars the running average price is taken over.</summary>
    public int AveragePriceWindowBars { get; init; } = 72;

    /// <summary>
    /// Bars a fall's rebound is followed for before its outcome is called. This doubles as the
    /// distance the scan skips after an event, so a long window does not merely follow rebounds for
    /// longer — it caps how many events a series of a given length can yield at all.
    /// </summary>
    public int ReboundWindowBars { get; init; } = 72;

    /// <summary>The bar spans a surge may be measured over, shortest first.</summary>
    public int[] SurgeWindowsBars { get; init; } = [4, 12, 24, 72, 168];

    /// <summary>Bars a surge's peak is watched for a reclaim.</summary>
    public int ReclaimHorizonBars { get; init; } = 72;

    /// <summary>How far below the running average the low must sit; zero disables the gate.</summary>
    public decimal MinimumFallBelowAverageFraction { get; init; } = 0.02m;

    /// <summary>How far above that average the fall's reference high may sit; zero disables the gate.</summary>
    public decimal MaximumReferenceHighAboveAverageFraction { get; init; } = 0.20m;

    /// <summary>Bars a simulated trade is held before it gives up and exits at the close.</summary>
    public int TradeWindowBars { get; init; } = 72;

    /// <summary>The notional put into each simulated trade.</summary>
    public decimal Stake { get; init; } = 1000m;

    /// <summary>Exchange fee charged on each side of a trade.</summary>
    public decimal FeeFractionPerSide { get; init; } = 0.0026m;

    /// <summary>An allowance for crossing the spread, charged once per round trip.</summary>
    public decimal SpreadAllowanceFraction { get; init; } = 0.001m;

    public int MaxEvents { get; init; } = 25;
    public int MaxLevels { get; init; } = 12;
}

public sealed record AnalysisSymbolOption(string Symbol, string Interval, int CandleCount, DateTime LastOpenTime);

public sealed record MarketAnalysisReport(
    string Symbol, string Interval, string Status, string? Message,
    int CandleCount, int IntervalMinutes, DateTime? FirstOpenTime, DateTime? LastOpenTime, decimal LastClose,
    TrendReport? Trend, PlummetReport? Plummets, SurgeReport? Surges, LevelReport? Levels, BacktestReport? Backtest)
{
    public static MarketAnalysisReport Insufficient(string symbol, string interval, int candleCount, int intervalMinutes) =>
        new(symbol, interval, "insufficient_data",
            $"Only {candleCount} candles stored for this market and interval. At least 60 with a readable spacing are needed.",
            candleCount, intervalMinutes, null, null, 0m, null, null, null, null, null);
}

public sealed record TrendReport(
    string Direction, string Phase, decimal StrengthPercent, bool IsNeutral, int FallingBarCount,
    decimal DirectionChangePrice, decimal DirectionChangeDistancePercent,
    decimal RegressionSlopePercentPerBar, decimal RegressionRSquared, decimal Rsi, decimal AtrPercent);

public sealed record PlummetReport(
    int EventCount, int PendingCount, decimal RecoveredHalfPercent, decimal AverageDropPercent, decimal AverageMaxReboundPercent,
    decimal? LiveReboundProbability, JudgeReport? Judge,
    IReadOnlyList<DecayCellDto> Decay, IReadOnlyList<PlummetEventDto> RecentEvents, IReadOnlyList<int> DecayGridMinutes);

public sealed record JudgeWeight(string Feature, double Weight);

public sealed record JudgeReport(
    IReadOnlyList<JudgeWeight> Weights, double Intercept,
    int TrainCount, int TestCount, decimal TrainSuccessPercent, decimal TestSuccessPercent, decimal TestOrdering,
    int TestPositiveCount, int TestNegativeCount, bool IsReliable);

public sealed record DecayCellDto(decimal OnsetFraction, int ElapsedMinutes, int PendingCount, int ReachedHalfCount, decimal ReachedHalfFraction);

public sealed record PlummetEventDto(
    DateTime TriggerTime, decimal ReferenceHigh, DateTime ReferenceHighTime, decimal EventLow, DateTime EventLowTime,
    decimal DropPercent, decimal RequiredDropPercent, decimal BaselineRangePercent, decimal VolumeRatio, bool IsWick,
    decimal MaxReboundPercent, int? MinutesToTenPercent, int? MinutesToQuarter, int? MinutesToHalf, int? MinutesToFull,
    bool LowerLowAfterMaxRebound, bool IsPending);

public sealed record SurgeReport(
    int EventCount, int PendingCount, decimal ReclaimedPercent, int? MedianMinutesToReclaim, decimal AverageRisePercent,
    SurgeInProgressDto? InProgress, IReadOnlyList<SurgeEventDto> RecentEvents);

public sealed record SurgeInProgressDto(
    decimal SurgeLow, DateTime SurgeLowTime, decimal SurgePeak, DateTime SurgePeakTime,
    decimal RisePercent, int DurationMinutes, bool IsCrashing, decimal DistanceFromPeakPercent);

public sealed record SurgeEventDto(
    int WindowMinutes, DateTime SurgeLowTime, decimal SurgeLow, DateTime SurgePeakTime, decimal SurgePeak,
    decimal RisePercent, int DurationMinutes, bool IsWick, decimal PeakVolumeRatio,
    DateTime CrashQualificationTime, decimal CrashLow, DateTime CrashLowTime, decimal RetracePercent,
    int? MinutesToReclaim, bool IsReclaimed, decimal SpeedPercentPerHour, bool IsPending);

public sealed record LevelReport(
    int LevelCount, int EncounterCount,
    IReadOnlyList<PriceLevelDto> Nearest, IReadOnlyList<TierRateDto> ByTouchTier, IReadOnlyList<LevelSummaryDto> ByContext);

public sealed record PriceLevelDto(
    int Id, decimal Price, int TouchCount, string SideNow, string LastActedKind,
    decimal ZoneWidthPercent, decimal DistancePercent, int Encounters, int Rejections);

public sealed record TierRateDto(string Tier, int Total, int Rejected, decimal RejectionRatePercent, int FollowedThrough);

public sealed record LevelSummaryDto(string Tier, string Kind, string TrendContext, int Rejected, int Total, decimal RejectionRatePercent);
