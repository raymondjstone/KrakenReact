using KrakenReact.Server.Analysis;

namespace KrakenReact.Tests;

/// <summary>
/// The anatomy readings and the judges fitted on them. The weights came from elsewhere and cannot be
/// re-derived here, so these tests pin the things that must hold regardless: that readings never look
/// forward, that the shapes line up with the weight vectors, and that the maths stays finite.
/// </summary>
public class PlummetAnatomyTests
{
    private static readonly DateTime Origin = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static List<AnalysisCandle> Minutes(IEnumerable<decimal> closes, decimal volume = 10m) =>
        closes.Select((c, i) => new AnalysisCandle(
            Origin.AddMinutes(i), c, c * 1.002m, c * 0.998m, c, volume, 5)).ToList();

    private static IEnumerable<decimal> Flat(decimal v, int n) => Enumerable.Repeat(v, n);

    private static IEnumerable<decimal> Ramp(decimal from, decimal to, int n) =>
        Enumerable.Range(0, n).Select(i => from + (to - from) * i / Math.Max(1, n - 1));

    /// <summary>Quiet, then a fall, then a recovery — all at minute resolution.</summary>
    private static List<AnalysisCandle> FallSeries() =>
        Minutes(Flat(100m, 600).Concat(Ramp(100m, 80m, 120)).Concat(Ramp(80m, 95m, 300)));

    private static PlummetEvent FallEvent(List<AnalysisCandle> candles) => new()
    {
        ReferenceHigh = 100m,
        ReferenceHighTime = candles[599].OpenTime,
        TriggerTime = candles[719].OpenTime,
        EventLow = 80m,
        EventLowTime = candles[719].OpenTime,
    };

    // ── Slope ───────────────────────────────────────────────────────────────

    [Fact]
    public void ClosingSlope_IsPositiveOnARise()
    {
        var candles = Minutes(Ramp(100m, 160m, 60));
        Assert.True(PlummetAnatomy.ClosingSlopePerMinute(candles, 0, 59, 1) > 0m);
    }

    [Fact]
    public void ClosingSlope_IsNegativeOnAFall()
    {
        var candles = Minutes(Ramp(160m, 100m, 60));
        Assert.True(PlummetAnatomy.ClosingSlopePerMinute(candles, 0, 59, 1) < 0m);
    }

    [Fact]
    public void ClosingSlope_IsZeroOnAFlatStretch()
    {
        Assert.Equal(0m, PlummetAnatomy.ClosingSlopePerMinute(Minutes(Flat(100m, 60)), 0, 59, 1));
    }

    [Fact]
    public void ClosingSlope_IsFarLessSensitiveToASpikeThanTwoEndpointsWouldBe()
    {
        // A least-squares line is used precisely so one bar cannot decide the answer. Reading the
        // same flat stretch from its endpoints calls it a steep climb; the fitted line barely moves.
        var closes = Flat(100m, 59).Append(400m).ToList();
        var candles = Minutes(closes);

        decimal fitted = PlummetAnatomy.ClosingSlopePerMinute(candles, 0, 59, 1);
        decimal naiveEndpoints = (closes[^1] - closes[0]) / 59m;

        Assert.True(fitted < naiveEndpoints / 5m,
            $"the fitted slope ({fitted}) should be a fraction of the endpoint slope ({naiveEndpoints})");
    }

    [Fact]
    public void ClosingSlope_NeedsTwoPointsToMeanAnything()
    {
        Assert.Equal(0m, PlummetAnatomy.ClosingSlopePerMinute(Minutes(Flat(100m, 10)), 5, 5, 1));
    }

    // ── Rising tests ────────────────────────────────────────────────────────

    [Fact]
    public void MakingHigherLows_IsTrueOnASteadyClimb()
    {
        var candles = Minutes(Ramp(100m, 200m, 120));
        Assert.True(PlummetAnatomy.IsStillRising(candles, 119, 119, RisingTest.MakingHigherLows, 30, 0m, 1));
    }

    [Fact]
    public void MakingHigherLows_IsFalseOnASteadyDecline()
    {
        var candles = Minutes(Ramp(200m, 100m, 120));
        Assert.False(PlummetAnatomy.IsStillRising(candles, 119, 119, RisingTest.MakingHigherLows, 30, 0m, 1));
    }

    [Fact]
    public void RisingTests_AreFalseBeforeTheWindowHasFilled()
    {
        // Judging on a window that runs off the front of the series would be reading data that is
        // not there; every test must decline rather than improvise.
        var candles = Minutes(Ramp(100m, 200m, 120));
        foreach (RisingTest test in Enum.GetValues<RisingTest>())
            Assert.False(PlummetAnatomy.IsStillRising(candles, 2, 2, test, 30, 0m, 1));
    }

    [Fact]
    public void RisingTest_None_IsAlwaysFalse()
    {
        var candles = Minutes(Ramp(100m, 200m, 120));
        Assert.False(PlummetAnatomy.IsStillRising(candles, 119, 119, RisingTest.None, 30, 0m, 1));
    }

    [Fact]
    public void HighestHighAfterLowestLow_CreditsAFlatStretchToItsEnd()
    {
        // Ties are resolved to the later bar, so a flat run does not turn the reading on an equality
        // struck at the very start of the window.
        var candles = Minutes(Flat(100m, 120));
        Assert.False(PlummetAnatomy.IsStillRising(candles, 119, 119, RisingTest.TheHighestHighCameAfterTheLowestLow, 30, 0m, 1));
    }

    [Fact]
    public void LineOfBestFitClimbsFastEnough_NeedsARangeToCompareAgainst()
    {
        var candles = Minutes(Ramp(100m, 200m, 120));
        Assert.False(PlummetAnatomy.IsStillRising(candles, 119, 119, RisingTest.TheLineOfBestFitClimbsFastEnough, 30, 1m, 1, null));
    }

    // ── Baseline volume ─────────────────────────────────────────────────────

    [Fact]
    public void BaselineVolume_AveragesTheQuietStretch()
    {
        var candles = Minutes(Flat(100m, 900), volume: 7m);
        decimal baseline = PlummetAnatomy.BaselineMeanVolume(candles, 800, PlummetStudyParameters.Default, PlummetAnatomyParameters.Default, 1);
        Assert.Equal(7m, baseline);
    }

    [Fact]
    public void BaselineVolume_SkipsBarsThatNeverTraded()
    {
        // An untraded bar is missing data, not zero volume; averaging it in would halve the baseline
        // and make every later fall look twice as heavily traded as it was.
        var candles = Minutes(Flat(100m, 900), volume: 8m)
            .Select((c, i) => i % 2 == 0 ? c with { Volume = 0m, TradeCount = 0 } : c)
            .ToList();

        decimal baseline = PlummetAnatomy.BaselineMeanVolume(candles, 800, PlummetStudyParameters.Default, PlummetAnatomyParameters.Default, 1);
        Assert.Equal(8m, baseline);
    }

    [Fact]
    public void BaselineVolume_IsZeroWhenTheHighIsUnknown()
    {
        Assert.Equal(0m, PlummetAnatomy.BaselineMeanVolume(Minutes(Flat(100m, 100)), -1, PlummetStudyParameters.Default, PlummetAnatomyParameters.Default, 1));
    }

    // ── Bottom observations ─────────────────────────────────────────────────

    [Fact]
    public void BottomObservations_AreTakenAcrossTheFall()
    {
        var candles = FallSeries();
        var atr = Indicators.AverageTrueRange(candles, 14);
        var observations = PlummetAnatomy.BuildBottomObservations(
            candles, FallEvent(candles), 1, atr, baselineMeanVolume: 10m, baselineRangeFraction: 0.01m,
            PlummetAnatomyParameters.Default, horizonMinutes: 60);

        Assert.NotEmpty(observations);
        Assert.All(observations, o => Assert.True(o.MinutesSinceTheTrigger >= 0));
    }

    [Fact]
    public void BottomObservations_CarryNoOutcomeWhenNoHorizonIsAsked()
    {
        // A horizon of nothing is the live case: the rule has no future to look at, and the reading
        // must say so rather than quietly reporting a zero that looks like a measurement.
        var candles = FallSeries();
        var atr = Indicators.AverageTrueRange(candles, 14);
        var observations = PlummetAnatomy.BuildBottomObservations(
            candles, FallEvent(candles), 1, atr, 10m, 0.01m, PlummetAnatomyParameters.Default, horizonMinutes: 0);

        Assert.NotEmpty(observations);
        Assert.All(observations, o =>
        {
            Assert.False(o.HasOutcome);
            Assert.Equal(0m, o.FurtherFallInDrops);
        });
    }

    [Fact]
    public void BottomObservations_StopShortOfTheHorizonSoOutcomesAreReal()
    {
        var candles = FallSeries();
        var atr = Indicators.AverageTrueRange(candles, 14);
        var observations = PlummetAnatomy.BuildBottomObservations(
            candles, FallEvent(candles), 1, atr, 10m, 0.01m, PlummetAnatomyParameters.Default, horizonMinutes: 120);

        var last = observations[^1];
        int barsLeft = (int)(candles[^1].OpenTime - last.ObservedAt).TotalMinutes;
        Assert.True(barsLeft >= 120, $"only {barsLeft} bars remained after the last reading, less than the horizon");
    }

    [Fact]
    public void BottomObservations_TrackTimeSinceTheLastNewLowBetweenSamples()
    {
        // The running low must advance on every bar, not only on sampled ones. If it only moved on
        // samples this reading would be a multiple of the cadence forever, losing most of its
        // resolution. A jagged descent puts new lows between the sample points on purpose.
        var jagged = new List<decimal>(Flat(100m, 600));
        for (int i = 0; i < 400; i++)
        {
            // A downward drift with a sawtooth on top, so lows land on non-sampled bars.
            decimal drift = 100m - i * 0.05m;
            jagged.Add(drift + (i % 7 == 0 ? -1.5m : 0.2m));
        }
        var candles = Minutes(jagged);
        var fall = new PlummetEvent
        {
            ReferenceHigh = 100m,
            ReferenceHighTime = candles[599].OpenTime,
            TriggerTime = candles[620].OpenTime,
            EventLow = 80m,
            EventLowTime = candles[620].OpenTime,
        };

        var observations = PlummetAnatomy.BuildBottomObservations(
            candles, fall, 1, Indicators.AverageTrueRange(candles, 14), 10m, 0.01m,
            PlummetAnatomyParameters.Default with { BottomSampleEveryMinutes = 15 }, horizonMinutes: 60);

        Assert.NotEmpty(observations);
        Assert.Contains(observations, o => o.MinutesSinceTheLastNewLow % 15 != 0);
    }

    [Fact]
    public void BottomObservations_AreEmptyWhenTheTriggerIsNotInTheSeries()
    {
        var candles = FallSeries();
        var stray = FallEvent(candles);
        stray.TriggerTime = Origin.AddYears(-5);

        Assert.Empty(PlummetAnatomy.BuildBottomObservations(
            candles, stray, 1, Indicators.AverageTrueRange(candles, 14), 10m, 0.01m, PlummetAnatomyParameters.Default, 60));
    }

    // ── Rise observations ───────────────────────────────────────────────────

    [Fact]
    public void RiseObservations_AreTakenAcrossTheRebound()
    {
        var candles = FallSeries();
        var atr = Indicators.AverageTrueRange(candles, 14);
        var observations = PlummetAnatomy.BuildRiseObservations(candles, FallEvent(candles), 1, atr, 10m);

        Assert.NotEmpty(observations);
        Assert.All(observations, o => Assert.True(o.MinutesSinceThePeak >= 0));
    }

    [Fact]
    public void RiseObservations_RecordTheDeepestPullbackSoFarWithoutEverShrinkingIt()
    {
        var candles = FallSeries();
        var atr = Indicators.AverageTrueRange(candles, 14);
        var observations = PlummetAnatomy.BuildRiseObservations(candles, FallEvent(candles), 1, atr, 10m);

        // "Deepest already survived" is a running maximum; a later shallower dip must not lower it.
        for (int i = 1; i < observations.Count; i++)
            Assert.True(observations[i].DeepestFallAlreadySurvivedInRanges >= observations[i - 1].DeepestFallAlreadySurvivedInRanges);
    }

    [Fact]
    public void RiseObservations_MarkTheFinalReadingAsUnsettled()
    {
        var candles = FallSeries();
        var atr = Indicators.AverageTrueRange(candles, 14);
        var observations = PlummetAnatomy.BuildRiseObservations(candles, FallEvent(candles), 1, atr, 10m);

        Assert.False(observations[^1].HasOutcome);
    }

    // ── Judges ──────────────────────────────────────────────────────────────

    [Fact]
    public void BottomJudge_HasOneWeightPerNamedFeature()
    {
        var judge = PlummetBottomJudge.Learned();
        Assert.Equal(PlummetBottomJudge.FeatureCount, judge.Weights.Count);
        Assert.Equal(PlummetBottomJudge.FeatureCount, PlummetBottomJudge.FeatureNames.Count);
    }

    [Fact]
    public void BottomJudge_ReturnsAProbabilityForARealObservation()
    {
        var candles = FallSeries();
        var atr = Indicators.AverageTrueRange(candles, 14);
        var observations = PlummetAnatomy.BuildBottomObservations(
            candles, FallEvent(candles), 1, atr, 10m, 0.01m, PlummetAnatomyParameters.Default, 0);

        var judge = PlummetBottomJudge.Learned();
        foreach (var o in observations.Take(20))
            Assert.InRange(judge.JudgeTheFallingIsFinished(o), 0m, 1m);
    }

    [Fact]
    public void BottomJudge_BuildsAFiniteReadingForEveryFeature()
    {
        var features = PlummetBottomJudge.BuildFeatures(new PlummetBottomObservation
        {
            FallenBelowTheTriggerInDrops = 0.4m,
            AboveTheRunningLowInRanges = 2.5m,
            MinutesSinceTheLastNewLow = 45,
            RecentVolumeMultiple = 3m,
            SlopeRangesPerHour = -20m,   // deliberately past the clamp
            FallAgainstTheBaselineRange = 1.2m,
            MinutesSinceTheTrigger = 90,
        });

        Assert.Equal(PlummetBottomJudge.FeatureCount, features.Length);
        Assert.All(features, f => Assert.False(double.IsNaN(f) || double.IsInfinity(f)));
        Assert.Equal(-5d, features[4]);   // clamped
    }

    [Fact]
    public void BottomJudge_ScoresZeroForAWronglySizedReading()
    {
        Assert.Equal(0m, PlummetBottomJudge.Learned().JudgeTheFallingIsFinished([1d, 2d]));
    }

    [Fact]
    public void BottomJudge_CanBeRefittedAndMeasuredOnRealObservations()
    {
        var candles = FallSeries();
        var atr = Indicators.AverageTrueRange(candles, 14);
        var observations = PlummetAnatomy.BuildBottomObservations(
            candles, FallEvent(candles), 1, atr, 10m, 0.01m, PlummetAnatomyParameters.Default, horizonMinutes: 60);

        var settled = observations.Where(o => o.HasOutcome).ToList();
        Assert.NotEmpty(settled);

        var refitted = PlummetBottomJudge.Fit(settled, o => o.FurtherFallInDrops <= 0.01m);
        if (refitted is not null)
            Assert.InRange(refitted.MeasureOrdering(settled, o => o.FurtherFallInDrops <= 0.01m), 0m, 1m);
    }

    [Fact]
    public void RiseJudge_HasNoLearnedWeightsToReachForByMistake()
    {
        // The source study never wrote a fitted set down for the rise judge. There must be no
        // Learned() to call, or someone will use uninitialised weights believing they are trained.
        Assert.Null(typeof(PlummetRiseJudge).GetMethod("Learned", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static));
    }

    [Fact]
    public void RiseJudge_BuildsAFiniteReadingEvenWithNothingSurvivedYet()
    {
        // Nothing survived yet means dividing by zero unless the denominator is floored.
        var features = PlummetRiseJudge.BuildFeatures(new PlummetRiseObservation
        {
            DrawdownInRanges = 2m,
            DeepestFallAlreadySurvivedInRanges = 0m,
            DrawdownShareOfTheGain = 0.3m,
            MinutesSinceThePeak = 20,
            GainSoFarInDrops = 0.4m,
            RecentVolumeMultiple = 2m,
            SlopeRangesPerHourOverTwoHours = 99m,
        });

        Assert.Equal(PlummetRiseJudge.FeatureCount, features.Length);
        Assert.All(features, f => Assert.False(double.IsNaN(f) || double.IsInfinity(f)));
        Assert.Equal(5d, features[6]);   // clamped
    }

    [Fact]
    public void RiseJudge_CanBeFittedOnRealObservations()
    {
        var candles = FallSeries();
        var atr = Indicators.AverageTrueRange(candles, 14);
        var settled = PlummetAnatomy.BuildRiseObservations(candles, FallEvent(candles), 1, atr, 10m)
            .Where(o => o.HasOutcome).ToList();

        Assert.NotEmpty(settled);
        var judge = PlummetRiseJudge.Fit(settled, o => o.MadeANewPeakAfterwards);
        if (judge is not null)
            Assert.All(settled.Take(20), o => Assert.InRange(judge.JudgeTheRiseIsAlive(o), 0m, 1m));
    }

    [Fact]
    public void Judges_DeclineToFitOnNothing()
    {
        Assert.Null(PlummetBottomJudge.Fit([], _ => true));
        Assert.Null(PlummetRiseJudge.Fit([], _ => true));
    }
}
