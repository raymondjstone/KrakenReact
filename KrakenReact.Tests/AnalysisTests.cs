using KrakenReact.Server.Analysis;

namespace KrakenReact.Tests;

/// <summary>
/// Exercises the ported analysis routines on synthetic series whose right answer is known by
/// construction, so a regression shows up as a wrong number rather than merely as an exception.
/// </summary>
public class AnalysisTests
{
    private const int IntervalMinutes = 60;
    private static readonly DateTime Origin = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Builds a candle series from closes, with a small symmetric high/low wrapped around each.</summary>
    private static List<AnalysisCandle> Series(IEnumerable<decimal> closes, decimal wickFraction = 0.002m)
    {
        var list = new List<AnalysisCandle>();
        decimal previous = 0m;
        int i = 0;
        foreach (var close in closes)
        {
            decimal open = i == 0 ? close : previous;
            decimal high = Math.Max(open, close) * (1m + wickFraction);
            decimal low = Math.Min(open, close) * (1m - wickFraction);
            list.Add(new AnalysisCandle(Origin.AddMinutes(i * IntervalMinutes), open, high, low, close, 100m, 10));
            previous = close;
            i++;
        }
        return list;
    }

    private static IEnumerable<decimal> Flat(decimal value, int count) => Enumerable.Repeat(value, count);

    private static IEnumerable<decimal> Ramp(decimal from, decimal to, int count) =>
        Enumerable.Range(0, count).Select(i => from + (to - from) * i / Math.Max(1, count - 1));

    // ── Indicators ──────────────────────────────────────────────────────────

    [Fact]
    public void RelativeStrengthIndex_IsHundred_WhenEveryBarRises()
    {
        var rsi = Indicators.RelativeStrengthIndex(Ramp(100m, 200m, 60).ToList(), 14);
        Assert.Equal(100m, rsi[^1]);
    }

    [Fact]
    public void RelativeStrengthIndex_IsNeutral_AcrossWarmup()
    {
        var rsi = Indicators.RelativeStrengthIndex(Ramp(100m, 200m, 60).ToList(), 14);
        Assert.All(rsi.Take(14), v => Assert.Equal(50m, v));
    }

    [Fact]
    public void AverageTrueRange_MatchesConstantBarRange()
    {
        // Every bar spans exactly 10, so the average of the true ranges must be 10 as well.
        var candles = Enumerable.Range(0, 40)
            .Select(i => new AnalysisCandle(Origin.AddMinutes(i * IntervalMinutes), 100m, 105m, 95m, 100m, 1m, 1))
            .ToList();
        var atr = Indicators.AverageTrueRange(candles, 14);
        Assert.Equal(10m, atr[^1]);
    }

    [Fact]
    public void RegressionTrend_ReportsHighRSquared_OnAStraightLine()
    {
        var (slope, rSquared) = Indicators.RegressionTrend(Series(Ramp(100m, 200m, 120)).ToList(), 60);
        Assert.True(slope[^1] > 0m, "a rising line should slope up");
        Assert.True(rSquared[^1] > 0.99m, $"a straight line should be almost fully explained, got {rSquared[^1]}");
    }

    [Fact]
    public void RegressionTrend_ReportsLowRSquared_OnAZigZag()
    {
        var zigzag = Enumerable.Range(0, 200).Select(i => 100m + (i % 2 == 0 ? 5m : -5m));
        var (_, rSquared) = Indicators.RegressionTrend(Series(zigzag).ToList(), 60);
        Assert.True(rSquared[^1] < 0.2m, $"alternating noise should explain almost nothing, got {rSquared[^1]}");
    }

    [Fact]
    public void InferIntervalMinutes_ReadsTheSpacing()
    {
        Assert.Equal(IntervalMinutes, Indicators.InferIntervalMinutes(Series(Flat(100m, 5))));
    }

    [Fact]
    public void Median_AveragesTheMiddlePair_WhenCountIsEven()
    {
        Assert.Equal(3m, Indicators.Median([1m, 2m, 4m, 8m]));
    }

    // ── Trend detection ─────────────────────────────────────────────────────

    [Fact]
    public void TrendDetection_CallsASustainedRise_Up()
    {
        var state = TrendDetection.ComputeLatestTrendState(Series(Ramp(100m, 300m, 400)));
        Assert.Equal(TrendDirection.Up, state.Direction);
    }

    [Fact]
    public void TrendDetection_CallsASustainedFall_Down()
    {
        var state = TrendDetection.ComputeLatestTrendState(Series(Ramp(300m, 100m, 400)));
        Assert.Equal(TrendDirection.Down, state.Direction);
    }

    [Fact]
    public void TrendDetection_ReachesTheEndPhase_WhenMomentumDecays()
    {
        // A rise that flattens out: the histogram must contract for long enough to be called ended.
        var closes = Ramp(100m, 300m, 300).Concat(Flat(300m, 120));
        var series = TrendDetection.ComputeTrendStates(Series(closes));
        Assert.Contains(TrendPhase.End, series.Phases);
    }

    [Fact]
    public void DirectionChangePrice_FlipsTheHistogram_WhenUsedAsTheNextClose()
    {
        // The whole point of the closed-form solve: feeding the price back in must land on the flip.
        var candles = Series(Ramp(100m, 300m, 400));
        var state = TrendDetection.ComputeLatestTrendState(candles);
        Assert.True(state.DirectionChangePrice > 0m);

        var parameters = TrendDetectionParameters.Default;
        var closes = candles.Select(c => c.Close).ToList();
        decimal before = Indicators.MacdHistogram(closes, parameters.FastPeriod, parameters.SlowPeriod, parameters.SignalPeriod, false)[^1];

        closes.Add(state.DirectionChangePrice);
        decimal atFlip = Indicators.MacdHistogram(closes, parameters.FastPeriod, parameters.SlowPeriod, parameters.SignalPeriod, false)[^1];

        Assert.True(Math.Abs(atFlip) < Math.Abs(before) / 1000m,
            $"the solved price should zero the histogram, got {atFlip} against {before}");
    }

    [Fact]
    public void TrendDetection_ReportsNothing_ForAnEmptySeries()
    {
        var state = TrendDetection.ComputeLatestTrendState([]);
        Assert.Equal(TrendDirection.None, state.Direction);
        Assert.Equal(0m, state.DirectionChangePrice);
    }

    [Fact]
    public void FindTrendPivots_AlternatesHighsAndLows()
    {
        var closes = new List<decimal>();
        for (int cycle = 0; cycle < 6; cycle++)
        {
            closes.AddRange(Ramp(100m, 160m, 40));
            closes.AddRange(Ramp(160m, 100m, 40));
        }
        var pivots = TrendDetection.FindTrendPivots(Series(closes), 14, 3m);

        Assert.True(pivots.Count >= 4, $"a repeating saw should give several pivots, got {pivots.Count}");
        for (int i = 1; i < pivots.Count; i++)
            Assert.NotEqual(pivots[i - 1].IsHigh, pivots[i].IsHigh);
    }

    // ── Plummet detection ───────────────────────────────────────────────────

    /// <summary>
    /// Quiet, then a 40% collapse, then a recovery back to where it started. The recovery is kept
    /// inside the detector's 72-candle rebound window on purpose: a slower one is not "no recovery",
    /// it is a recovery the detector was never asked to look far enough ahead to see.
    /// </summary>
    private static List<AnalysisCandle> CrashAndRecover() =>
        Series(Flat(100m, 300)
            .Concat(Ramp(100m, 60m, 6))
            .Concat(Flat(60m, 10))
            .Concat(Ramp(60m, 100m, 40))
            .Concat(Flat(100m, 200)));

    [Fact]
    public void PlummetDetection_FindsTheCollapse()
    {
        var events = PlummetDetection.DetectPlummetEvents(CrashAndRecover(), IntervalMinutes, PlummetStudyParameters.Default);
        var found = Assert.Single(events);
        Assert.InRange(found.DropFraction, 0.35m, 0.45m);
        Assert.InRange(found.EventLow, 59m, 61m);
    }

    [Fact]
    public void PlummetDetection_MeasuresTheFullRecovery()
    {
        var found = Assert.Single(PlummetDetection.DetectPlummetEvents(CrashAndRecover(), IntervalMinutes, PlummetStudyParameters.Default));
        Assert.True(found.MaximumReboundFraction >= 0.9m, $"price came all the way back, got {found.MaximumReboundFraction}");
        Assert.NotNull(found.MinutesToHalfRebound);
        Assert.NotNull(found.MinutesToFullRebound);
        // The onset milestones must be ordered: a rebound cannot reach half before it reaches a tenth.
        Assert.True(found.MinutesToTenPercentRebound <= found.MinutesToHalfRebound);
        Assert.True(found.MinutesToHalfRebound <= found.MinutesToFullRebound);
    }

    [Fact]
    public void PlummetDetection_IgnoresAFallSmallerThanTheThreshold()
    {
        var mild = Series(Flat(100m, 300).Concat(Ramp(100m, 96m, 6)).Concat(Flat(96m, 100)));
        Assert.Empty(PlummetDetection.DetectPlummetEvents(mild, IntervalMinutes, PlummetStudyParameters.Default));
    }

    [Fact]
    public void PlummetDetection_IgnoresAMarketThatSwingsThisMuchAnyway()
    {
        // A market whose baseline range is already huge should not report a plummet for a routine
        // swing: that is exactly what the quietness multiple is there to suppress.
        var choppy = new List<decimal>();
        for (int cycle = 0; cycle < 30; cycle++)
        {
            choppy.AddRange(Ramp(100m, 160m, 10));
            choppy.AddRange(Ramp(160m, 100m, 10));
        }
        var quiet = PlummetDetection.DetectPlummetEvents(Series(choppy), IntervalMinutes,
            new PlummetStudyParameters { QuietnessMultiple = 5m });
        Assert.Empty(quiet);
    }

    [Fact]
    public void PlummetDetection_HandlesAnEmptySeries()
    {
        Assert.Empty(PlummetDetection.DetectPlummetEvents([], IntervalMinutes, PlummetStudyParameters.Default));
    }

    [Fact]
    public void DecayTable_CountsOnlyTheEventsStillWaiting()
    {
        var events = PlummetDetection.DetectPlummetEvents(CrashAndRecover(), IntervalMinutes, PlummetStudyParameters.Default);
        var cells = PlummetDetection.ComputeDecayTable(events, [60, 240]);
        Assert.NotEmpty(cells);
        Assert.All(cells, c => Assert.True(c.ReachedHalfCount <= c.PendingCount));
    }

    // ── Surge detection ─────────────────────────────────────────────────────

    [Fact]
    public void SurgeDetection_FindsASpikeThatBrokeDown()
    {
        var candles = Series(Flat(100m, 200)
            .Concat(Ramp(100m, 200m, 10))
            .Concat(Ramp(200m, 120m, 20))
            .Concat(Flat(120m, 200)));

        var events = SurgeDetection.DetectSurgeEvents(candles, IntervalMinutes, SurgeStudyParameters.Default);
        Assert.NotEmpty(events);
        Assert.True(events[0].SurgeRiseFraction >= 0.5m, $"the rise was ~100%, got {events[0].SurgeRiseFraction}");
        Assert.False(events[0].IsReclaimed, "the price never got back to the peak");
    }

    [Fact]
    public void SurgeDetection_RecordsAReclaim_WhenThePeakIsRetaken()
    {
        var candles = Series(Flat(100m, 200)
            .Concat(Ramp(100m, 200m, 10))
            .Concat(Ramp(200m, 130m, 20))
            .Concat(Ramp(130m, 260m, 60))
            .Concat(Flat(260m, 100)));

        var events = SurgeDetection.DetectSurgeEvents(candles, IntervalMinutes, SurgeStudyParameters.Default);
        Assert.NotEmpty(events);
        Assert.True(events[0].IsReclaimed, "the peak was retaken and should be recorded as reclaimed");
        Assert.NotNull(events[0].MinutesToReclaim);
    }

    [Fact]
    public void SurgeDetection_FindsNothing_InAFlatMarket()
    {
        Assert.Empty(SurgeDetection.DetectSurgeEvents(Series(Flat(100m, 500)), IntervalMinutes, SurgeStudyParameters.Default));
    }

    [Fact]
    public void SurgeInProgress_IsReported_WhileTheRiseIsStillRunning()
    {
        var candles = Series(Flat(100m, 200).Concat(Ramp(100m, 200m, 10)));
        var live = SurgeDetection.DetectSurgeInProgress(candles, IntervalMinutes, SurgeStudyParameters.Default);
        Assert.NotNull(live);
        Assert.True(live!.SurgeRiseFraction >= 0.5m);
    }

    // ── Support and resistance ──────────────────────────────────────────────

    [Fact]
    public void SupportResistance_ClustersRepeatedTurnsIntoOneLevel()
    {
        // Six round trips between the same two prices: the pivots should collapse onto two levels,
        // each touched several times, rather than twelve separate ones.
        var closes = new List<decimal>();
        for (int cycle = 0; cycle < 6; cycle++)
        {
            closes.AddRange(Ramp(100m, 160m, 40));
            closes.AddRange(Ramp(160m, 100m, 40));
        }
        var levels = SupportResistance.BuildLevels(Series(closes), SupportResistanceStudyParameters.Default);

        Assert.NotEmpty(levels);
        Assert.True(levels.Count <= 4, $"repeated turns at two prices should not build many levels, got {levels.Count}");
        Assert.Contains(levels, l => l.TouchCount >= 2);
    }

    [Fact]
    public void SupportResistance_SummarisesEncountersWithConsistentCounts()
    {
        var closes = new List<decimal>();
        for (int cycle = 0; cycle < 8; cycle++)
        {
            closes.AddRange(Ramp(100m, 160m, 30));
            closes.AddRange(Ramp(160m, 100m, 30));
        }
        var encounters = SupportResistance.AnalyzeLevelEncounters(Series(closes), SupportResistanceStudyParameters.Default);
        var summaries = SupportResistance.Summarize(encounters);

        Assert.All(summaries, s => Assert.True(s.Rejected <= s.Total));
        Assert.Equal(encounters.Count, summaries.Sum(s => s.Total));
    }

    [Fact]
    public void GetTouchTier_CollapsesTheThinTail()
    {
        Assert.Equal("2", SupportResistance.GetTouchTier(2));
        Assert.Equal("4+", SupportResistance.GetTouchTier(4));
        Assert.Equal("4+", SupportResistance.GetTouchTier(11));
    }

    // ── Logistic judge ──────────────────────────────────────────────────────

    [Fact]
    public void LogisticJudge_LearnsAPerfectlySeparableRule()
    {
        var rows = Enumerable.Range(0, 100).Select(i => new[] { (double)i }).ToArray();
        var targets = Enumerable.Range(0, 100).Select(i => i >= 50 ? 1d : 0d).ToArray();

        var judge = LogisticJudge.Fit(rows, targets, passes: 500);
        Assert.NotNull(judge);
        Assert.True(judge!.Judge([90d]) > judge.Judge([10d]), "a higher reading must score higher");
        Assert.Equal(1m, judge.MeasureOrdering(rows, targets.Select(t => t == 1d).ToList()));
    }

    [Fact]
    public void LogisticJudge_ScoresAHalf_WhenItCannotSeparateAtAll()
    {
        // Every reading identical: the judge expresses no preference, and the tie handling in the
        // rank sum must give it the half it has earned rather than zero or one.
        var rows = Enumerable.Repeat(new[] { 1d }, 50).ToArray();
        var positives = Enumerable.Range(0, 50).Select(i => i % 2 == 0).ToList();

        var judge = new LogisticJudge([0d], 0d, [1d], [1d]);
        Assert.Equal(0.5m, judge.MeasureOrdering(rows, positives));
    }

    [Fact]
    public void LogisticJudge_ReturnsNothing_WhenThereIsNothingToFitOn()
    {
        Assert.Null(LogisticJudge.Fit([], []));
        Assert.Null(LogisticJudge.Fit([[1d]], [1d, 0d]));
    }

    [Fact]
    public void LogisticJudge_ReturnsZero_ForAWronglySizedReading()
    {
        var judge = new LogisticJudge([1d, 1d], 0d, [0d, 0d], [1d, 1d]);
        Assert.Equal(0m, judge.Judge([1d]));
    }

    // ── Rebound judge ───────────────────────────────────────────────────────

    [Fact]
    public void ReboundJudge_BuildsOneReadingPerNamedFeature()
    {
        var features = ReboundJudge.BuildFeatures(new PlummetEvent
        {
            DropFraction = 0.4m,
            BaselineRangeFraction = 0.05m,
            VolumeRatio = 3m,
            AveragePrice = 100m,
            ReferenceHigh = 110m,
            EventLow = 60m,
        }, priorEventCount: 4);

        Assert.Equal(ReboundJudge.FeatureNames.Count, features.Length);
        Assert.All(features, f => Assert.False(double.IsNaN(f) || double.IsInfinity(f)));
    }

    [Fact]
    public void ReboundJudge_DeclinesToFit_OnTooLittleHistory()
    {
        var events = Enumerable.Range(0, 5)
            .Select(i => new PlummetEvent { EventLowTime = Origin.AddDays(i), MaximumReboundFraction = 0.6m })
            .ToList();
        Assert.Null(ReboundJudge.FitFromHistory(events));
    }

    [Fact]
    public void ReboundJudge_DeclinesToFit_WhenEveryOutcomeIsTheSame()
    {
        // Nothing to separate, so a fitted judge would only be reporting the base rate back.
        var events = Enumerable.Range(0, 40)
            .Select(i => new PlummetEvent
            {
                EventLowTime = Origin.AddDays(i),
                DropFraction = 0.2m + i * 0.001m,
                MaximumReboundFraction = 0.9m,
            })
            .ToList();
        Assert.Null(ReboundJudge.FitFromHistory(events));
    }

    [Fact]
    public void ReboundJudge_Fits_AndOrdersASeparableHistory()
    {
        // Deep falls recover, shallow ones do not — a rule the judge should find and rank by.
        var events = Enumerable.Range(0, 60)
            .Select(i => new PlummetEvent
            {
                EventLowTime = Origin.AddDays(i),
                DropFraction = i % 2 == 0 ? 0.15m : 0.45m,
                BaselineRangeFraction = 0.03m,
                VolumeRatio = 2m,
                AveragePrice = 100m,
                ReferenceHigh = 105m,
                EventLow = i % 2 == 0 ? 89m : 58m,
                MaximumReboundFraction = i % 2 == 0 ? 0.2m : 0.8m,
            })
            .ToList();

        var fit = ReboundJudge.FitFromHistory(events);
        Assert.NotNull(fit);
        Assert.Equal(events.Count, fit!.TrainCount + fit.TestCount);
        Assert.True(fit.TestOrdering > 0.9m, $"a clean rule should be ordered almost perfectly, got {fit.TestOrdering}");
    }

    // ── End-to-end report ───────────────────────────────────────────────────

    [Fact]
    public void Analyse_RefusesASeriesTooShortToMeanAnything()
    {
        var service = new MarketAnalysisService(null!, null!);
        var report = service.Analyse("TEST/USD", "OneHour", Series(Flat(100m, 10)), new AnalysisOptions());
        Assert.Equal("insufficient_data", report.Status);
        Assert.Null(report.Trend);
    }

    /// <summary>Builds the same crash-and-recover shape at an arbitrary bar spacing.</summary>
    private static List<AnalysisCandle> CrashAndRecoverAt(int intervalMinutes)
    {
        var closes = Flat(100m, 300)
            .Concat(Ramp(100m, 60m, 6))
            .Concat(Flat(60m, 10))
            .Concat(Ramp(60m, 100m, 40))
            .Concat(Flat(100m, 200))
            .ToList();

        var list = new List<AnalysisCandle>();
        decimal previous = 0m;
        for (int i = 0; i < closes.Count; i++)
        {
            decimal close = closes[i], open = i == 0 ? close : previous;
            list.Add(new AnalysisCandle(
                Origin.AddMinutes((long)i * intervalMinutes),
                open,
                Math.Max(open, close) * 1.002m,
                Math.Min(open, close) * 0.998m,
                close, 100m, 10));
            previous = close;
        }
        return list;
    }

    [Theory]
    [InlineData(1)]      // one-minute bars, the resolution the thresholds were calibrated on
    [InlineData(60)]     // hourly
    [InlineData(1440)]   // daily
    public void Analyse_FindsTheSameFall_AtEveryBarSpacing(int intervalMinutes)
    {
        // The detector's windows arrive in minutes but only mean anything as a count of bars. Before
        // they were scaled by the interval, a 120-minute drop window was two bars on an hourly series
        // and rounded to zero on a daily one, which switched fall detection off altogether.
        var service = new MarketAnalysisService(null!, null!);
        var report = service.Analyse("TEST/USD", "X", CrashAndRecoverAt(intervalMinutes), new AnalysisOptions());

        Assert.Equal("ok", report.Status);
        Assert.Equal(intervalMinutes, report.IntervalMinutes);
        Assert.True(report.Plummets!.EventCount >= 1,
            $"the same 40% collapse should be found on {intervalMinutes}-minute bars, got {report.Plummets.EventCount}");
    }

    [Fact]
    public void Analyse_ScalesTheDecayGridWithTheInterval()
    {
        var service = new MarketAnalysisService(null!, null!);
        var hourly = service.Analyse("TEST/USD", "X", CrashAndRecoverAt(60), new AnalysisOptions());
        var daily = service.Analyse("TEST/USD", "X", CrashAndRecoverAt(1440), new AnalysisOptions());

        // A decay column has to describe the same number of bars whatever the bar length is.
        Assert.Equal(hourly.Plummets!.DecayGridMinutes.Count, daily.Plummets!.DecayGridMinutes.Count);
        Assert.Equal(
            hourly.Plummets.DecayGridMinutes.Select(m => m / 60).ToList(),
            daily.Plummets.DecayGridMinutes.Select(m => m / 1440).ToList());
    }

    [Fact]
    public void ReboundJudge_MarksAOneSidedTestSetUnreliable()
    {
        // 90% recovered: the later slice holds barely any failures, so its ordering score lands on an
        // extreme by luck. The fit must say so rather than present a confident number.
        var events = Enumerable.Range(0, 60)
            .Select(i => new PlummetEvent
            {
                EventLowTime = Origin.AddDays(i),
                DropFraction = 0.2m + i * 0.001m,
                BaselineRangeFraction = 0.03m,
                VolumeRatio = 2m,
                AveragePrice = 100m,
                ReferenceHigh = 105m,
                EventLow = 80m,
                MaximumReboundFraction = i % 10 == 0 ? 0.2m : 0.9m,
            })
            .ToList();

        var fit = ReboundJudge.FitFromHistory(events);
        Assert.NotNull(fit);
        Assert.True(fit!.TestNegativeCount < ReboundJudge.ReliableTestClassCount);
        Assert.False(fit.IsReliable, "a test set with almost no failures cannot validate anything");
    }

    [Fact]
    public void ReboundJudge_MarksABalancedTestSetReliable()
    {
        var events = Enumerable.Range(0, 60)
            .Select(i => new PlummetEvent
            {
                EventLowTime = Origin.AddDays(i),
                DropFraction = i % 2 == 0 ? 0.15m : 0.45m,
                BaselineRangeFraction = 0.03m,
                VolumeRatio = 2m,
                AveragePrice = 100m,
                ReferenceHigh = 105m,
                EventLow = i % 2 == 0 ? 89m : 58m,
                MaximumReboundFraction = i % 2 == 0 ? 0.2m : 0.8m,
            })
            .ToList();

        var fit = ReboundJudge.FitFromHistory(events);
        Assert.NotNull(fit);
        Assert.True(fit!.IsReliable, "an evenly split test set is worth reading");
    }

    [Fact]
    public void Analyse_ProducesEverySectionForARealisticSeries()
    {
        var service = new MarketAnalysisService(null!, null!);
        var report = service.Analyse("TEST/USD", "OneHour", CrashAndRecover(), new AnalysisOptions());

        Assert.Equal("ok", report.Status);
        Assert.NotNull(report.Trend);
        Assert.NotNull(report.Plummets);
        Assert.NotNull(report.Surges);
        Assert.NotNull(report.Levels);
        Assert.Equal(IntervalMinutes, report.IntervalMinutes);
        Assert.True(report.Plummets!.EventCount >= 1, "the 40% collapse should be reported");
    }
}
