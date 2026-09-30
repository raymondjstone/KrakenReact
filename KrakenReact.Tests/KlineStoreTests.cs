using KrakenReact.Server.Data;
using KrakenReact.Server.Models;
using KrakenReact.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace KrakenReact.Tests;

public class KlineRulesTests
{
    private static DerivedKline Day(DateTime open, string interval = "OneDay") => new() { Asset = "XBT/USD", Interval = interval, OpenTime = open };

    [Fact]
    public void ADayCandle_IsOnlyClosedOnceTheDayIsOver()
    {
        var open = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        Assert.False(KlineRules.IsClosed(Day(open), open.AddHours(4)));          // the 04:00 refresh sees a forming candle
        Assert.False(KlineRules.IsClosed(Day(open), open.AddDays(1).AddTicks(-1)));
        Assert.True(KlineRules.IsClosed(Day(open), open.AddDays(1)));
    }

    [Theory]
    [InlineData("OneMinute", 1)]
    [InlineData("FiveMinutes", 5)]
    [InlineData("OneHour", 60)]
    [InlineData("FourHour", 240)]
    public void ShorterIntervals_CloseAfterTheirOwnLength(string interval, int minutes)
    {
        var open = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(KlineRules.IsClosed(Day(open, interval), open.AddMinutes(minutes - 1)));
        Assert.True(KlineRules.IsClosed(Day(open, interval), open.AddMinutes(minutes)));
    }

    [Fact]
    public void AnUnknownInterval_IsTreatedAsClosed_SoSavingNeverSilentlyStops()
    {
        Assert.True(KlineRules.IsClosed(Day(DateTime.UtcNow, "SomethingNew"), DateTime.UtcNow));
    }

    [Fact]
    public void TheKeyFormat_IsDefinedInOnePlace()
    {
        var t = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal($"XBT/USDOneDay{t.Ticks}", DerivedKline.MakeKey("XBT/USD", "OneDay", t));
        Assert.Equal(string.Empty, new DerivedKline().Key);   // the parameterless one no longer invents a meaningless "_0" key
    }
}

public class KlineStoreTests
{
    private static readonly DateTime Now = new(2026, 3, 10, 4, 0, 0, DateTimeKind.Utc);   // when the daily refresh runs

    private static DerivedKline Candle(int daysAgo, decimal close, decimal volume = 100m, string asset = "XBT/USD")
    {
        var open = Now.Date.AddDays(-daysAgo);
        return new DerivedKline { Asset = asset, Interval = "OneDay", OpenTime = open, Open = close, High = close, Low = close, Close = close, Volume = volume };
    }

    private static (InMemoryDbFactory Factory, DbMethods Db) Make()
    {
        var factory = new InMemoryDbFactory($"kline-{Guid.NewGuid()}");
        return (factory, new DbMethods(factory, new Mock<ILogger<DbMethods>>().Object, TestDiagnostics.Create()));
    }

    private static async Task<List<DerivedKline>> Stored(InMemoryDbFactory f)
    {
        await using var c = f.CreateDbContext();
        return await c.DerivedKlines.AsNoTracking().OrderBy(k => k.OpenTime).ToListAsync();
    }

    [Fact]
    public async Task TheStillFormingCandle_IsNotStored()
    {
        var (f, _) = Make();
        // Yesterday is finished; today (opened 00:00, it is 04:00) is not
        var (added, _) = await KlineStore.UpsertAsync(f, [Candle(1, 100m), Candle(0, 50m, 10m)], Now);

        Assert.Equal(1, added);
        var rows = await Stored(f);
        Assert.Single(rows);
        Assert.Equal(100m, rows[0].Close);
    }

    [Fact]
    public async Task ACandleStoredWhileFormingByAnOlderVersion_IsCorrectedOnceComplete()
    {
        var (f, _) = Make();
        // What the old insert-only code left behind: yesterday's candle as it looked at 04:00 (a fraction of the day)
        await using (var c = f.CreateDbContext())
        {
            var partial = Candle(1, 90m, 5m);
            partial.Key = DerivedKline.MakeKey(partial.Asset, partial.Interval, partial.OpenTime);
            c.DerivedKlines.Add(partial);
            await c.SaveChangesAsync();
        }

        // The complete candle arrives
        var (added, updated) = await KlineStore.UpsertAsync(f, [Candle(1, 110m, 900m)], Now);

        Assert.Equal(0, added);
        Assert.Equal(1, updated);
        var row = Assert.Single(await Stored(f));
        Assert.Equal(110m, row.Close);
        Assert.Equal(900m, row.Volume);
    }

    [Fact]
    public async Task IdenticalCandles_AreNotRewritten()
    {
        var (f, _) = Make();
        await KlineStore.UpsertAsync(f, [Candle(2, 100m), Candle(1, 101m)], Now);

        var (added, updated) = await KlineStore.UpsertAsync(f, [Candle(2, 100m), Candle(1, 101m)], Now);

        Assert.Equal(0, added);
        Assert.Equal(0, updated);
        Assert.Equal(2, (await Stored(f)).Count);
    }

    [Fact]
    public async Task DuplicatesInOneBatch_CollapseToOneRow()
    {
        var (f, _) = Make();
        await KlineStore.UpsertAsync(f, [Candle(3, 100m), Candle(3, 100m)], Now);
        Assert.Single(await Stored(f));
    }

    [Fact]
    public async Task BatchingDoesNotLoseRows()
    {
        var (f, _) = Make();
        var candles = Enumerable.Range(1, 130).Select(d => Candle(d, 100m + d)).ToList();

        var (added, _) = await KlineStore.UpsertAsync(f, candles, Now, batchSize: 25);

        Assert.Equal(130, added);
        Assert.Equal(130, (await Stored(f)).Count);
    }

    [Fact]
    public async Task TheDbEntryPoint_FiltersNonDailyAndFormingCandles()
    {
        var (f, db) = Make();
        var minute = Candle(1, 5m); minute.Interval = "OneMinute";

        // The entry point uses the real clock, so build the candles from it: two days ago is finished, today's is still forming
        DerivedKline Real(int daysAgo, decimal close) => new() { Asset = "XBT/USD", Interval = "OneDay", OpenTime = DateTime.UtcNow.Date.AddDays(-daysAgo), Close = close };
        await db.AddKlineAsync([Real(2, 100m), Real(0, 50m), minute]);

        var rows = await Stored(f);
        Assert.Single(rows);
        Assert.Equal("OneDay", rows[0].Interval);
    }

    [Fact]
    public async Task AppSettings_RoundTrip()
    {
        var (_, db) = Make();
        Assert.Null(await db.GetAppSettingAsync("Flag"));

        await db.SetAppSettingAsync("Flag", "true");
        Assert.Equal("true", await db.GetAppSettingAsync("Flag"));

        await db.SetAppSettingAsync("Flag", "false");
        Assert.Equal("false", await db.GetAppSettingAsync("Flag"));
    }
}

public class VolatilityPrecisionTests
{
    [Fact]
    public void Std20_MatchesADoublePrecisionReference_OnSmallReturns()
    {
        // Returns around 0.001 - where the single-precision E[x^2]-E[x]^2 form throws most of its digits away
        var rng = new Random(7);
        var values = Enumerable.Range(0, 40).Select(_ => (float)(0.001 + rng.NextDouble() * 0.0005)).ToArray();

        double mean = 0; for (int j = 20; j < 40; j++) mean += values[j]; mean /= 20;
        double v = 0; for (int j = 20; j < 40; j++) v += (values[j] - mean) * (values[j] - mean);
        var expected = Math.Sqrt(v / 20);

        Assert.Equal(expected, FeatureEngineering.Std20(values, 39), 8);
    }

    [Fact]
    public void Std20_OfAConstantSeries_IsZero()
    {
        Assert.Equal(0f, FeatureEngineering.Std20(Enumerable.Repeat(0.001f, 25).ToArray(), 24));
    }
}
