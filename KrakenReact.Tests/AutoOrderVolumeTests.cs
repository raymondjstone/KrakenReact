using KrakenReact.Server.Models;
using KrakenReact.Server.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace KrakenReact.Tests;

/// <summary>The volume rules are about yesterday's volume, not the partial volume of the day still in progress.</summary>
public class AutoOrderVolumeTests
{
    private static async Task<string> ReasonFor(decimal yesterdayVolume, decimal todayVolume)
    {
        var state = new TradingStateService(new DelistedPriceService(new Mock<ILogger<DelistedPriceService>>().Object));
        var svc = new AutoOrderService(state, null!, null!, new Mock<ILogger<AutoOrderService>>().Object);

        var now = DateTime.UtcNow;
        var today = now.Date;
        var item = new PriceDataItem { Symbol = "SOL/USD", KrakenNewPricesLoadedEver = true };

        DerivedKline Daily(DateTime open, decimal close, decimal volume) => new()
            { Asset = "SOL/USD", Interval = "OneDay", OpenTime = open, Open = close, High = close, Low = close, Close = close, Volume = volume };

        var klines = new List<DerivedKline> { Daily(today.AddDays(-400), 100m, 5000m) };            // history older than a year
        for (var d = 70; d >= 2; d--) klines.Add(Daily(today.AddDays(-d), 100m, 5000m));           // flat history
        klines.Add(Daily(today.AddDays(-1), 100m, yesterdayVolume));                                 // yesterday: finished
        klines.Add(Daily(today, 100m, todayVolume));                                                 // today: still forming
        // A few recent ticks a bit higher, so today's average sits above the week's
        for (var m = 2; m >= 0; m--)
            klines.Add(new DerivedKline { Asset = "SOL/USD", Interval = "OneMinute", OpenTime = now.AddMinutes(-m), Close = 110m, Volume = 1m });
        item.AddKlineHistory(klines);

        return (await svc.CheckAsync(item, "Test")).Reason ?? "";
    }

    [Fact]
    public async Task AQuietStartToToday_DoesNotFailAPairThatTradedWellYesterday()
    {
        // Yesterday 1,500 x 100 = 150k; today so far only 10 x 100 = 1k - the old code judged the pair on that
        var reason = await ReasonFor(yesterdayVolume: 1500m, todayVolume: 10m);
        Assert.DoesNotContain("Low Volume", reason);
    }

    [Fact]
    public async Task ABigDayToday_DoesNotRescueAPairThatWasQuietYesterday()
    {
        // Yesterday 100 x 100 = 10k (under 20k); today already 5,000 x 100
        var reason = await ReasonFor(yesterdayVolume: 100m, todayVolume: 5000m);
        Assert.Contains("Low Volume yesterday under 20k", reason);
    }
}
