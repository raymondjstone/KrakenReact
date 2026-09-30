using KrakenReact.Server.Controllers;
using KrakenReact.Server.Models;
using KrakenReact.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace KrakenReact.Tests;

public class BacktestEngineTests
{
    private static List<DerivedKline> Days(params decimal[] closes) =>
        closes.Select((c, i) => new DerivedKline
        {
            Interval = "OneDay", Asset = "X/USD", OpenTime = new DateTime(2025, 1, 1).AddDays(i), Open = c, High = c, Low = c, Close = c, Volume = 1,
        }).ToList();

    // Flat at 100, one dip to 80 (buy), then back to 120 (sell)
    private static List<DerivedKline> DipAndRecover() =>
        Days(Enumerable.Repeat(100m, 10).Concat([80m, 120m]).ToArray());

    [Fact]
    public void ADipThenRecovery_IsOneWinningTrade()
    {
        var r = BacktestEngine.Run(DipAndRecover(), 8, 12, feePct: 0);

        var t = Assert.Single(r.Trades);
        Assert.Equal(80m, t.EntryPrice);
        Assert.Equal(120m, t.ExitPrice);
        Assert.Equal(50m, t.PlPct);                     // 80 -> 120
        Assert.Equal(15000m, r.FinalValue);
        Assert.False(r.OpenPosition);
    }

    [Fact]
    public void FeesAreChargedOnBothSides()
    {
        var r = BacktestEngine.Run(DipAndRecover(), 8, 12, feePct: 1m);

        // 10000 * 0.99 bought at 80, sold at 120 * 0.99
        Assert.Equal(Math.Round(10000m * 0.99m / 80m * 120m * 0.99m, 2), Math.Round(r.FinalValue, 2));
        Assert.True(r.Trades[0].PlPct < 50m);
    }

    [Fact]
    public void APositionStillOpenAtTheEnd_IsValuedAtTheLastClose_NotLost()
    {
        // Dip to 80 and the data ends there: still holding
        var klines = Days(Enumerable.Repeat(100m, 10).Concat([80m]).ToArray());
        var r = BacktestEngine.Run(klines, 8, 11, feePct: 0);

        Assert.Empty(r.Trades);
        Assert.True(r.OpenPosition);
        Assert.Equal(10000m, r.FinalValue);   // bought at 80, last close 80
    }

    [Fact]
    public void NoSignals_LeavesTheCashUntouched()
    {
        var r = BacktestEngine.Run(Days(Enumerable.Range(1, 30).Select(i => 100m + i * 3).ToArray()), 8, 30, 0);
        Assert.Empty(r.Trades);
        Assert.False(r.OpenPosition);
        Assert.Equal(BacktestEngine.StartingCash, r.FinalValue);
    }

    [Fact]
    public void ARangeBeyondTheData_IsClamped()
    {
        var r = BacktestEngine.Run(DipAndRecover(), 0, 1000, 0);
        Assert.NotNull(r);
    }
}

public class BacktestControllerReportTests
{
    private static (BacktestController Controller, TradingStateService State) Make()
    {
        var state = new TradingStateService(new DelistedPriceService(new Mock<ILogger<DelistedPriceService>>().Object));
        return (new BacktestController(state), state);
    }

    private static object Get(object o, string name) => o.GetType().GetProperty(name)!.GetValue(o)!;

    [Fact]
    public void AnOpenPositionAtTheEnd_DoesNotReportATotalLoss()
    {
        var (controller, state) = Make();
        var item = state.GetOrAddPrice("SOL/USD");
        // 70 flat days, then a dip that is still open at the end of the data
        var closes = Enumerable.Repeat(100m, 70).Concat([90m]).ToArray();
        for (var i = 0; i < closes.Length; i++)
            item.AddKline(new DerivedKline { Interval = "OneDay", Asset = "SOL/USD", OpenTime = new DateTime(2025, 1, 1).AddDays(i), Close = closes[i], Volume = 1 });

        var ok = Assert.IsType<OkObjectResult>(controller.RunBacktest("SOL/USD", 0m));
        var summary = Get(ok.Value!, "summary");

        Assert.Equal(true, Get(summary, "openPosition"));
        Assert.Equal(0m, Get(summary, "totalPlPct"));    // bought at 90, last close 90: flat, not -100%
    }

    [Fact]
    public void ANormalizedSpelling_FindsTheSameMarket()
    {
        var (controller, state) = Make();
        var item = state.GetOrAddPrice("XBT/USD");
        for (var i = 0; i < 70; i++)
            item.AddKline(new DerivedKline { Interval = "OneDay", Asset = "XBT/USD", OpenTime = new DateTime(2025, 1, 1).AddDays(i), Close = 100m, Volume = 1 });

        Assert.IsType<OkObjectResult>(controller.RunBacktest("BTC/USD"));
        Assert.IsType<OkObjectResult>(controller.WalkForward("BTC/USD"));
    }

    [Fact]
    public void TheFeeIsReportedBack()
    {
        var (controller, state) = Make();
        var item = state.GetOrAddPrice("ETH/USD");
        for (var i = 0; i < 70; i++)
            item.AddKline(new DerivedKline { Interval = "OneDay", Asset = "ETH/USD", OpenTime = new DateTime(2025, 1, 1).AddDays(i), Close = 100m, Volume = 1 });

        var ok = Assert.IsType<OkObjectResult>(controller.RunBacktest("ETH/USD"));
        Assert.Equal(BacktestController.DefaultFeePct, Get(Get(ok.Value!, "summary"), "feePct"));
    }
}
