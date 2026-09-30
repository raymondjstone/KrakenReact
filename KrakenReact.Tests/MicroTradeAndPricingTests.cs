using Kraken.Net.Objects.Models;
using KrakenReact.Server.Models;
using KrakenReact.Server.Services;

namespace KrakenReact.Tests;

public class MicroTradeHelperTests
{
    [Theory]
    [InlineData(1.239, 2, 1.23)]
    [InlineData(1.99999, 4, 1.9999)]
    [InlineData(5, 0, 5)]
    [InlineData(0.123456789, 8, 0.12345678)]
    public void FloorToDecimals_NeverRoundsUp(double value, int decimals, double expected)
    {
        Assert.Equal((decimal)expected, MicroTradeJob.FloorToDecimals((decimal)value, decimals));
    }

    [Fact]
    public void FloorToDecimals_HighPrecisionStaysExact()
    {
        // Math.Pow-based versions drift for many decimals; this must stay exact
        Assert.Equal(0.123456789012m, MicroTradeJob.FloorToDecimals(0.1234567890129m, 12));
    }

    [Fact]
    public void ShrinkQuantitySlightly_OverPrecise_FloorsToLotPrecision()
    {
        Assert.Equal(1.23m, MicroTradeJob.ShrinkQuantitySlightly(1.239m, 2));
    }

    [Fact]
    public void ShrinkQuantitySlightly_AtPrecision_SubtractsOneStep()
    {
        Assert.Equal(1.22m, MicroTradeJob.ShrinkQuantitySlightly(1.23m, 2));
    }

    [Fact]
    public void ShrinkQuantitySlightly_NeverGoesNegative()
    {
        Assert.Equal(0m, MicroTradeJob.ShrinkQuantitySlightly(0.01m, 2));
    }

    [Theory]
    [InlineData("EOrder:Insufficient funds", true)]
    [InlineData("EGeneral:Invalid arguments", true)]
    [InlineData("Request timed out", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsDefiniteRejection_OnlyForKrakenErrorCodes(string? message, bool expected)
    {
        Assert.Equal(expected, MicroTradeJob.IsDefiniteRejection(message));
    }

    [Theory]
    [InlineData("EOrder:Post only order", true)]
    [InlineData("EOrder:Insufficient funds", false)]
    [InlineData(null, false)]
    public void IsPostOnlyRejection_Detected(string? message, bool expected)
    {
        Assert.Equal(expected, MicroTradeJob.IsPostOnlyRejection(message));
    }

    [Theory]
    [InlineData(-2.5, 3, 2, true)]    // 0.5 pts from the -3% trigger, inside a 2-pt threshold
    [InlineData(-1.0, 3, 2, false)]   // 2.0 pts away — not strictly inside the threshold
    [InlineData(0.5, 3, 2, false)]    // far above trigger
    [InlineData(-3.0, 3, 2, false)]   // exactly at the trigger — that is a buy, not "nearing"
    [InlineData(-4.0, 3, 2, false)]   // already past the trigger
    public void IsNearingBuy_UsesDistanceToTrigger(double change, double drop, double threshold, bool expected)
    {
        Assert.Equal(expected, MicroTradeJob.IsNearingBuy((decimal)change, (decimal)drop, (decimal)threshold));
    }
}

public class PriceAtTests
{
    private static KrakenKline Candle(DateTime open, decimal o, decimal c) =>
        new() { OpenTime = open, OpenPrice = o, ClosePrice = c };

    [Fact]
    public void PriceAt_InterpolatesWithinTheContainingCandle()
    {
        var t0 = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var klines = new List<KrakenKline> { Candle(t0, 100m, 110m) };

        Assert.Equal(105m, PriceChangeService.PriceAt(klines, t0.AddMinutes(30)));
        Assert.Equal(100m, PriceChangeService.PriceAt(klines, t0));
    }

    [Fact]
    public void PriceAt_PicksLatestCandleAtOrBeforeTarget()
    {
        var t0 = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var klines = new List<KrakenKline>
        {
            Candle(t0, 100m, 110m),
            Candle(t0.AddHours(1), 110m, 120m),
        };

        Assert.Equal(115m, PriceChangeService.PriceAt(klines, t0.AddMinutes(90)));
    }

    [Fact]
    public void PriceAt_NoCandleBeforeTarget_ReturnsNull()
    {
        var t0 = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        Assert.Null(PriceChangeService.PriceAt(new List<KrakenKline> { Candle(t0, 100m, 110m) }, t0.AddMinutes(-5)));
        Assert.Null(PriceChangeService.PriceAt(new List<KrakenKline>(), t0));
    }
}

public class LiveKlineTests
{
    private static DerivedKline Tick(decimal close) =>
        new() { Interval = "OneMinute", OpenTime = DateTime.UtcNow, Close = close };

    [Fact]
    public void SetLiveKline_ReplacesPreviousTick_AndKeepsDailyHistory()
    {
        var item = new PriceDataItem { Symbol = "XBT/USD" };
        item.AddKlineHistory(new List<DerivedKline>
        {
            new() { Interval = "OneDay", OpenTime = DateTime.UtcNow.AddDays(-400), Close = 10m },
            new() { Interval = "OneDay", OpenTime = DateTime.UtcNow.AddDays(-1), Close = 20m },
        });

        for (var i = 0; i < 25_000; i++) item.SetLiveKline(Tick(100m + i));

        var snapshot = item.GetKlineSnapshot();
        Assert.Equal(3, snapshot.Count); // 2 daily + exactly one live tick, however many ticks arrived
        Assert.Equal(24_999m + 100m, item.LatestKline!.Close);
        Assert.Contains(snapshot, k => k.Close == 10m); // year-old history survived
    }
}

public class ProximityAlertTests
{
    private static TradingStateService NewState() =>
        new(new DelistedPriceService(new Moq.Mock<Microsoft.Extensions.Logging.ILogger<DelistedPriceService>>().Object));

    [Fact]
    public void TryMarkProximityAlerted_TrueOnlyOnce_UntilCleared()
    {
        var s = NewState();
        Assert.True(s.TryMarkProximityAlerted("o1"));
        Assert.False(s.TryMarkProximityAlerted("o1"));

        s.ClearProximityAlerted("o1");
        Assert.True(s.TryMarkProximityAlerted("o1"));
    }

    [Fact]
    public void TryMarkProximityAlerted_IsAtomicUnderContention()
    {
        var s = NewState();
        var wins = 0;
        Parallel.For(0, 200, _ => { if (s.TryMarkProximityAlerted("same")) Interlocked.Increment(ref wins); });
        Assert.Equal(1, wins);
    }

    [Fact]
    public void TryMarkProximityAlerted_EvictsOldestFirst_NotEverything()
    {
        var s = NewState();
        for (var i = 0; i < 5001; i++) s.TryMarkProximityAlerted($"o{i}");

        Assert.False(s.TryMarkProximityAlerted("o5000")); // recent entries are still remembered
        Assert.True(s.TryMarkProximityAlerted("o0"));     // only the oldest aged out
    }
}
