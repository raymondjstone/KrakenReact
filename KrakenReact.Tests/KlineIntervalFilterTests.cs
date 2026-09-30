using KrakenReact.Server.Data;
using KrakenReact.Server.Models;
using Microsoft.Extensions.Logging;
using Moq;

namespace KrakenReact.Tests;

/// <summary>Minute candles share the daily bars' Asset key; loading them unfiltered evicted daily history.</summary>
public class KlineIntervalFilterTests
{
    private static async Task<DbMethods> SeedAsync()
    {
        var factory = new InMemoryDbFactory($"klines-{Guid.NewGuid()}");
        var db = new DbMethods(factory, new Mock<ILogger<DbMethods>>().Object, TestDiagnostics.Create());

        await using var ctx = factory.CreateDbContext();
        var day = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        ctx.DerivedKlines.Add(new DerivedKline { Key = "d1", Asset = "XBT/USD", Interval = "OneDay", OpenTime = day, Close = 1m });
        ctx.DerivedKlines.Add(new DerivedKline { Key = "m1", Asset = "XBT/USD", Interval = "OneMinute", OpenTime = day.AddMinutes(1), Close = 2m });
        ctx.DerivedKlines.Add(new DerivedKline { Key = "m2", Asset = "XBT/USD", Interval = "OneMinute", OpenTime = day.AddMinutes(2), Close = 3m });
        ctx.DerivedKlines.Add(new DerivedKline { Key = "o1", Asset = "ETH/USD", Interval = "OneDay", OpenTime = day, Close = 9m });
        await ctx.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task GetKlineAsync_DefaultsToDailyBarsOnly()
    {
        var db = await SeedAsync();
        var result = await db.GetKlineAsync("XBT/USD");

        Assert.Single(result);
        Assert.Equal("OneDay", result[0].Interval);
    }

    [Fact]
    public async Task GetKlineAsync_CanRequestAnotherInterval()
    {
        var db = await SeedAsync();
        Assert.Equal(2, (await db.GetKlineAsync("XBT/USD", "OneMinute")).Count);
    }

    [Fact]
    public async Task GetKlineAsync_NullIntervalReturnsEverything()
    {
        var db = await SeedAsync();
        Assert.Equal(3, (await db.GetKlineAsync("XBT/USD", null)).Count);
    }
}
