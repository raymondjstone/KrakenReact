using KrakenReact.Server.Analysis;

namespace KrakenReact.Tests;

/// <summary>
/// The grade model carries coefficients fitted elsewhere, so these tests pin the contract around it
/// rather than the numbers themselves: that it refuses input it cannot read, that it degrades
/// sensibly when a reading is missing, and that its output stays in range.
/// </summary>
public class PlummetGradeTests
{
    private static readonly DateTime Origin = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Minute candles at a flat price, which is the backdrop a fall is measured against.</summary>
    private static List<AnalysisCandle> MinuteBars(int count, decimal price = 100m, decimal volume = 10m) =>
        Enumerable.Range(0, count)
            .Select(i => new AnalysisCandle(Origin.AddMinutes(i), price, price * 1.001m, price * 0.999m, price, volume, 5))
            .ToList();

    /// <summary>A quiet stretch of minute bars followed by a fall over the final two hours.</summary>
    private static List<AnalysisCandle> QuietThenFall(int quietMinutes = 600, int fallMinutes = 120, decimal from = 100m, decimal to = 85m)
    {
        var bars = MinuteBars(quietMinutes);
        for (int i = 0; i < fallMinutes; i++)
        {
            decimal p = from + (to - from) * i / (fallMinutes - 1);
            var t = Origin.AddMinutes(quietMinutes + i);
            bars.Add(new AnalysisCandle(t, p, p * 1.001m, p * 0.998m, p, 40m, 20));
        }
        return bars;
    }

    // ── Interval guard ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(60)]
    [InlineData(1440)]
    [InlineData(15)]
    public void GradeSeries_RefusesAnythingButMinuteBars(int intervalMinutes)
    {
        // The features fold sixty candles into an hour. On any other spacing that measures the wrong
        // span, and a number would come back that looks fine and means nothing.
        var bars = Enumerable.Range(0, 800)
            .Select(i => new AnalysisCandle(Origin.AddMinutes((long)i * intervalMinutes), 100m, 101m, 99m, 100m, 10m, 5))
            .ToList();

        Assert.Null(PlummetGrade.GradeSeries(bars, 120m, bars[0].OpenTime));
    }

    [Fact]
    public void GradeSeries_AcceptsMinuteBars()
    {
        var bars = QuietThenFall();
        var grade = PlummetGrade.GradeSeries(bars, 100m, bars[600].OpenTime);

        Assert.NotNull(grade);
        Assert.InRange(grade!.Value, 0d, 1d);
    }

    [Fact]
    public void GradeSeries_DeclinesASeriesTooShortToMeasure()
    {
        // Six hours of history cannot supply a two-hour fall on top of a four-hour baseline plus the
        // fourteen hourly bars the volatility reading needs.
        var bars = MinuteBars(120);
        Assert.Null(PlummetGrade.GradeSeries(bars, 120m, bars[0].OpenTime));
    }

    [Fact]
    public void GradeSeries_DeclinesWhenTheReferenceHighPredatesTheSeries()
    {
        var bars = QuietThenFall();
        Assert.Null(PlummetGrade.GradeSeries(bars, 100m, Origin.AddDays(-30)));
    }

    // ── Hourly folding ──────────────────────────────────────────────────────

    [Fact]
    public void BuildHourlyBars_FoldsSixtyMinutesIntoOne()
    {
        var bars = PlummetGrade.BuildHourlyBars(MinuteBars(180), firstMinuteOffset: 0);
        Assert.Equal(3, bars.Count);
    }

    [Fact]
    public void BuildHourlyBars_AlignsToWhereTheSeriesStarts()
    {
        // Starting at :30 means the first bar holds only thirty minutes, so 180 minutes spans four.
        var bars = PlummetGrade.BuildHourlyBars(MinuteBars(180), firstMinuteOffset: 30);
        Assert.Equal(4, bars.Count);
    }

    [Fact]
    public void BuildHourlyBars_TakesTheExtremesAndSumsTheVolume()
    {
        var minutes = new List<AnalysisCandle>
        {
            new(Origin, 100m, 105m, 99m, 101m, 10m, 1),
            new(Origin.AddMinutes(1), 101m, 110m, 95m, 108m, 20m, 2),
            new(Origin.AddMinutes(2), 108m, 109m, 97m, 106m, 30m, 3),
        };

        var bar = Assert.Single(PlummetGrade.BuildHourlyBars(minutes, 0));
        Assert.Equal(100m, bar.Open);      // first open
        Assert.Equal(110m, bar.High);      // highest high
        Assert.Equal(95m, bar.Low);        // lowest low
        Assert.Equal(106m, bar.Close);     // last close
        Assert.Equal(60m, bar.Volume);     // summed
        Assert.Equal(6, bar.TradeCount);
    }

    [Fact]
    public void BuildHourlyBars_HandlesAnEmptySeries()
    {
        Assert.Empty(PlummetGrade.BuildHourlyBars([], 0));
    }

    // ── The model itself ────────────────────────────────────────────────────

    [Fact]
    public void Grade_StaysWithinRangeForExtremeReadings()
    {
        // Clipping is what keeps a wild reading from dragging the grade somewhere the model was never
        // fitted; without it a single outlier could pin the output at an extreme.
        foreach (var value in new[] { -1e9, 0d, 1e9 })
        {
            double grade = PlummetGrade.Grade(Enumerable.Repeat(value, PlummetGrade.FeatureNames.Count).ToArray());
            Assert.InRange(grade, 0d, 1d);
        }
    }

    [Fact]
    public void Grade_IgnoresAReadingTheSeriesCouldNotSupply()
    {
        // A NaN means "not measurable here", not "zero". Weighing it would poison the whole grade.
        var withNan = Enumerable.Repeat(double.NaN, PlummetGrade.FeatureNames.Count).ToArray();
        double grade = PlummetGrade.Grade(withNan);

        Assert.InRange(grade, 0d, 1d);
        Assert.False(double.IsNaN(grade));
    }

    [Fact]
    public void Grade_IsDeterministic()
    {
        var features = new[] { 0.12, 0.027, 9.8, 0.81, 371.5, 0.53, 98.6 };
        Assert.Equal(PlummetGrade.Grade(features), PlummetGrade.Grade(features));
    }

    [Fact]
    public void Grade_MovesWithTheDropSize()
    {
        // The drop-size weight is negative, so a deeper fall grades lower — a better-looking setup.
        var shallow = new[] { 0.10, 0.027, 9.8, 0.81, 371.5, 0.53, 98.6 };
        var deep = new[] { 0.23, 0.027, 9.8, 0.81, 371.5, 0.53, 98.6 };
        Assert.True(PlummetGrade.Grade(deep) < PlummetGrade.Grade(shallow));
    }

    // ── Stake sizing ────────────────────────────────────────────────────────

    [Fact]
    public void StakeMultiplier_StakesEverythingEquallyWhenTiltIsOff()
    {
        Assert.Equal(1m, PlummetGrade.StakeMultiplier(0.1, 0));
        Assert.Equal(1m, PlummetGrade.StakeMultiplier(0.9, 0));
    }

    [Fact]
    public void StakeMultiplier_GivesLessToAWorseGrade()
    {
        decimal good = PlummetGrade.StakeMultiplier(0.2, 1.0);
        decimal bad = PlummetGrade.StakeMultiplier(0.8, 1.0);

        Assert.True(bad < good, $"a worse grade should stake less, got {bad} against {good}");
        Assert.Equal(0.8m, Math.Round(good, 6));
        Assert.Equal(0.2m, Math.Round(bad, 6));
    }

    [Fact]
    public void StakeMultiplier_BitesHarderAsTheTiltRises()
    {
        decimal gentle = PlummetGrade.StakeMultiplier(0.5, 1.0);
        decimal fierce = PlummetGrade.StakeMultiplier(0.5, 3.0);
        Assert.True(fierce < gentle);
    }

    [Fact]
    public void StakeMultiplier_NeverGoesNegativeOrAboveOne()
    {
        foreach (var grade in new[] { -5d, 0d, 0.5d, 1d, 5d })
            Assert.InRange(PlummetGrade.StakeMultiplier(grade, 2.0), 0m, 1m);
    }
}
